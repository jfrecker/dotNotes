using System.Text;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using DotNotes.Core.Vault;
using Microsoft.Extensions.Logging;

namespace DotNotes.Core.Reorganization;

/// <summary>
/// Default <see cref="IVaultReorganizationService"/> implementation. See
/// that interface's remarks and docs/06-DATA-MODEL.md's "Folder &amp;
/// note move/rename" section for the full contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm shape (shared by both operations via
/// <see cref="MoveAndRewriteAsync"/>):</b>
/// </para>
/// <list type="number">
/// <item><description>
/// Snapshot the vault's known note paths and predict the old-&gt;new path
/// mapping for whatever is about to move (one entry for a note move, one
/// per note in the subtree for a folder move - the subtree is walked
/// <i>before</i> the move, since the source won't exist afterward).
/// </description></item>
/// <item><description>
/// Scan every known note's <i>current on-disk content</i> (not the link
/// index - see remarks below) for non-code wikilink occurrences,
/// recording (a) every note that contains at least one non-code
/// occurrence at all, and (b) the subset whose occurrence resolves,
/// against the pre-move known paths, to something in the mapping - a
/// rewrite <i>candidate</i>. This is a full scan with a cheap
/// <c>"[["</c> pre-filter, same trade-off as <c>get_recent_notes</c> -
/// acceptable at a personal vault's scale.
/// </description></item>
/// <item><description>
/// Perform the actual move through <see cref="INoteRepository"/>, which
/// also validates the destination name(s) and throws (unchanged) if the
/// destination already exists or the source is missing - since step 2 is
/// read-only, nothing has been touched yet if this throws.
/// </description></item>
/// <item><description>
/// For each candidate note found in step 2, <i>re-read its content fresh
/// from disk</i> at its current (post-move) path - not the copy captured
/// during the step-2 scan - then compute the rewrite against that fresh
/// content and save through <see cref="INoteRepository.SaveAsync"/>
/// (atomic) if anything changed. Re-reading immediately before rewriting
/// shrinks the window in which a concurrent write to that same note (a
/// debounced autosave <c>PUT</c>, or an MCP <c>update_note</c> call - the
/// semaphore only serializes reorganizations against each other, not
/// against ordinary saves) could otherwise be silently clobbered by a
/// rewrite computed from stale, scan-time content. Best-effort per note:
/// a failure here is logged and that note is omitted from the returned
/// list, without rolling back the move itself.
/// </description></item>
/// <item><description>
/// Before returning, synchronously tell every <see cref="IVaultChangeListener"/>
/// about a deletion for each old path and a change for every moved note,
/// every rewritten note, and every *other* note that merely contains a
/// wikilink at all (even one that didn't qualify as a candidate) - see
/// this method's remarks on why the last group matters too. This closes
/// the exact gap documented on <see cref="Notes.INoteRepository.MoveFolderAsync"/>
/// (the file-watcher's <c>Renamed</c> event never fires per nested note
/// for a folder move) and the equivalent one-off gap for a plain note
/// move.
/// </description></item>
/// </list>
/// <para>
/// <b>Why scan disk, not <see cref="ILinkIndex.GetBacklinks"/>:</b> the
/// frontend flushes a pending autosave immediately before issuing a move,
/// but the in-memory link index only catches up after the file-watcher's
/// debounce window - so a link the user typed a moment ago can already be
/// on disk while still absent from the index. Trusting the index here
/// would silently orphan that link. Scanning disk directly closes this
/// race entirely, at the cost of reading every note once per move (fine
/// for a personal vault).
/// </para>
/// <para>
/// <b>Why every link-bearing note, not just candidates, gets re-notified:</b>
/// <see cref="Links.InMemoryLinkIndex"/> resolves a note's outgoing links
/// once, at <see cref="IVaultChangeListener.NoteChanged"/> call time, and
/// never re-resolves a different note's already-cached links later (see
/// its remarks). A move can change how a link in some *other*,
/// non-candidate note resolves without that note's own text ever
/// qualifying as a candidate - e.g. a bare-title link that previously
/// resolved to an unrelated note now loses a tie-break to the freshly
/// moved note because they now share a title, or gains a target it
/// previously lacked. Re-notifying every link-bearing note (not just
/// candidates) with its current content makes the link index end up
/// equivalent to a full <see cref="ILinkIndex.RebuildAsync"/> for exactly
/// the notes that could possibly be affected - the same O(vault) trade-off
/// <c>FoldersEndpoints</c> already accepted via a full rebuild, just
/// narrowed to link-bearing notes. Notes that are neither moved nor
/// rewritten are *not* re-read from disk for this - nothing here writes
/// them, and a genuinely concurrent edit to one of them is reconciled by
/// the file-watcher's own later event, same as any other edit.
/// </para>
/// </remarks>
public sealed class VaultReorganizationService : IVaultReorganizationService
{
    private readonly INoteRepository _noteRepository;
    private readonly IReadOnlyList<IVaultChangeListener> _changeListeners;
    private readonly ILogger<VaultReorganizationService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public VaultReorganizationService(
        INoteRepository noteRepository,
        IEnumerable<IVaultChangeListener> changeListeners,
        ILogger<VaultReorganizationService> logger)
    {
        _noteRepository = noteRepository;
        _changeListeners = changeListeners.ToArray();
        _logger = logger;
    }

    public async Task<NoteMoveResult> MoveNoteAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedSource = WikiLinkResolver.NormalizeToNotePath(sourcePath);
            var normalizedDestination = WikiLinkResolver.NormalizeToNotePath(destinationPath);

            var predictedMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [normalizedSource] = normalizedDestination
            };

            var preMoveKnownPaths = await GetKnownNotePathsAsync(cancellationToken).ConfigureAwait(false);

            NoteWriteResult? moveResult = null;

            var (pathMapping, rewrittenNotes) = await MoveAndRewriteAsync(
                preMoveKnownPaths,
                predictedMapping,
                async innerCt =>
                {
                    moveResult = await _noteRepository.MoveAsync(sourcePath, destinationPath, innerCt).ConfigureAwait(false);
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [normalizedSource] = moveResult.Path
                    };
                },
                cancellationToken).ConfigureAwait(false);

            var finalPath = pathMapping[normalizedSource];

            // If the moved note's own content was rewritten (a self-link),
            // UpdatedAt must reflect that later save, not the bare move -
            // re-read it fresh rather than tracking two separate
            // timestamps through the shared rewrite pass below.
            var finalNote = await _noteRepository.GetAsync(finalPath, cancellationToken).ConfigureAwait(false);
            var updatedAt = finalNote?.UpdatedAt ?? moveResult!.UpdatedAt;

            return new NoteMoveResult
            {
                Path = finalPath,
                UpdatedAt = updatedAt,
                RewrittenNotes = rewrittenNotes
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<FolderMoveResult> MoveFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedSourceFolder = NormalizeFolderPath(sourcePath);
            var normalizedDestinationFolder = NormalizeFolderPath(destinationPath);
            var subtreePrefix = normalizedSourceFolder + "/";

            var preMoveKnownPaths = await GetKnownNotePathsAsync(cancellationToken).ConfigureAwait(false);

            // Walk the subtree *before* the move - the source directory
            // won't exist afterward, so this is the only chance to capture
            // the full old-> predicted-new mapping for every note nested
            // inside it, at any depth.
            var predictedMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in preMoveKnownPaths)
            {
                if (path.StartsWith(subtreePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = path[normalizedSourceFolder.Length..];
                    predictedMapping[path] = normalizedDestinationFolder + suffix;
                }
            }

            string? finalFolderPath = null;

            var (_, rewrittenNotes) = await MoveAndRewriteAsync(
                preMoveKnownPaths,
                predictedMapping,
                async innerCt =>
                {
                    finalFolderPath = await _noteRepository.MoveFolderAsync(sourcePath, destinationPath, innerCt).ConfigureAwait(false);

                    var authoritative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var oldPath in predictedMapping.Keys)
                    {
                        var suffix = oldPath[normalizedSourceFolder.Length..];
                        authoritative[oldPath] = finalFolderPath + suffix;
                    }

                    return authoritative;
                },
                cancellationToken).ConfigureAwait(false);

            return new FolderMoveResult
            {
                Path = finalFolderPath!,
                RewrittenNotes = rewrittenNotes
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Shared scan -&gt; move -&gt; rewrite -&gt; index-update pipeline used by
    /// both <see cref="MoveNoteAsync"/> and <see cref="MoveFolderAsync"/>.
    /// See this type's remarks for the full algorithm.
    /// </summary>
    /// <param name="preMoveKnownPaths">Every note path known to exist before the move.</param>
    /// <param name="predictedPathMapping">
    /// Old vault-relative path -&gt; predicted new vault-relative path, for
    /// every note about to move. Used only to drive the pre-move scan;
    /// <paramref name="performMoveAsync"/> returns the authoritative
    /// version actually used for rewriting/index updates.
    /// </param>
    /// <param name="performMoveAsync">
    /// Performs the real <see cref="INoteRepository"/> move and returns the
    /// authoritative old-&gt;new mapping (same keys as
    /// <paramref name="predictedPathMapping"/>, values corrected to
    /// whatever the repository actually produced). Exceptions from this
    /// propagate unchanged - since everything before this point is
    /// read-only, nothing has been mutated if it throws.
    /// </param>
    private async Task<(IReadOnlyDictionary<string, string> PathMapping, IReadOnlyList<string> RewrittenNotes)> MoveAndRewriteAsync(
        HashSet<string> preMoveKnownPaths,
        Dictionary<string, string> predictedPathMapping,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> performMoveAsync,
        CancellationToken cancellationToken)
    {
        // Full-vault scan (read-only): for every note currently on disk,
        // record (a) every note containing at least one non-code wikilink
        // occurrence at all - linkBearingContentByOldPath, used purely for
        // index re-notification below, never for rewriting/saving - and
        // (b) the subset whose occurrence resolves, pre-move, to something
        // about to move - candidateOldPaths, which need their content
        // re-read fresh (see below) and considered for an actual rewrite.
        var linkBearingContentByOldPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var candidateOldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in preMoveKnownPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NoteContent? note;
            try
            {
                note = await _noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Skipping '{Path}' while scanning the vault for wikilinks that need rewriting.", path);
                continue;
            }

            if (note is null || !note.Content.Contains("[[", StringComparison.Ordinal))
            {
                continue;
            }

            var hasNonCodeOccurrence = false;
            var hasQualifyingLink = false;
            foreach (var occurrence in WikiLinkParser.ParseWithPositions(note.Content))
            {
                if (occurrence.IsCode)
                {
                    continue;
                }

                hasNonCodeOccurrence = true;

                var resolved = WikiLinkResolver.Resolve(occurrence.RawTarget, preMoveKnownPaths);
                if (predictedPathMapping.ContainsKey(resolved.TargetPath))
                {
                    hasQualifyingLink = true;
                }
            }

            if (hasNonCodeOccurrence)
            {
                linkBearingContentByOldPath[path] = note.Content;
            }

            if (hasQualifyingLink)
            {
                candidateOldPaths.Add(path);
            }
        }

        // Perform the actual move now. If this throws, nothing above
        // mutated anything, so the vault is left exactly as it was.
        var pathMapping = await performMoveAsync(cancellationToken).ConfigureAwait(false);

        var postMoveKnownPaths = new HashSet<string>(preMoveKnownPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var oldPath in pathMapping.Keys)
        {
            postMoveKnownPaths.Remove(oldPath);
        }

        foreach (var newPath in pathMapping.Values)
        {
            postMoveKnownPaths.Add(newPath);
        }

        var rewrittenNotes = new List<string>();

        // Final content for every candidate, at its current (post-move)
        // path - fed into index notifications below regardless of whether
        // an actual rewrite happened, since this is always the *fresh*
        // on-disk truth for that note.
        var finalContentByCurrentPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var oldPath in candidateOldPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentPath = pathMapping.TryGetValue(oldPath, out var mappedNew) ? mappedNew : oldPath;

            // Re-read fresh from disk at the note's *current* path right
            // before computing/saving its rewrite - not the copy captured
            // during the scan above - so the window in which a concurrent
            // write to this note could be clobbered is only
            // read -> compute -> save, not scan -> move -> compute -> save.
            NoteContent? freshNote;
            try
            {
                freshNote = await _noteRepository.GetAsync(currentPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to re-read '{Path}' before computing its wikilink rewrite; leaving it unchanged.", currentPath);
                continue;
            }

            if (freshNote is null)
            {
                // Deleted concurrently between the scan and now - nothing
                // left to rewrite or re-index.
                continue;
            }

            string? rewritten;
            try
            {
                rewritten = RewriteContent(freshNote.Content, pathMapping, preMoveKnownPaths, postMoveKnownPaths);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to compute a wikilink rewrite for '{Path}'; leaving its content unchanged.", currentPath);
                finalContentByCurrentPath[currentPath] = freshNote.Content;
                continue;
            }

            if (rewritten is null)
            {
                // Nothing needed rewriting against the fresh content
                // either - still re-index it below with that fresh
                // content (the "Minimal" byte-identical case, or a
                // concurrent edit that removed the link already).
                finalContentByCurrentPath[currentPath] = freshNote.Content;
                continue;
            }

            try
            {
                await _noteRepository.SaveAsync(currentPath, rewritten, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort per docs/06-DATA-MODEL.md: the move itself is
                // not rolled back, and this note is simply omitted from
                // RewrittenNotes. Re-index with what's actually on disk
                // (the fresh, unrewritten content), since the save failed.
                _logger.LogWarning(ex, "Failed to save rewritten wikilinks to '{Path}'; the move was not rolled back.", currentPath);
                finalContentByCurrentPath[currentPath] = freshNote.Content;
                continue;
            }

            rewrittenNotes.Add(currentPath);
            finalContentByCurrentPath[currentPath] = rewritten;
        }

        // Build the full set of index notifications:
        //  1. Every candidate's fresh/rewritten content (fix for the
        //     stale-scan-content save race above).
        //  2. Every moved note's new path - even one that wasn't itself a
        //     candidate (e.g. it has no outgoing links at all) - with
        //     fresh content, or the index would never learn it exists
        //     there.
        //  3. Every *other* link-bearing note (neither moved nor a
        //     rewrite candidate) - re-notified with its scan-time content
        //     (safe to reuse: nothing here writes it, and a genuinely
        //     concurrent edit to it is reconciled by the file-watcher's
        //     own later event) so InMemoryLinkIndex re-resolves its
        //     outgoing links against the now-current known-paths set. See
        //     this method's remarks for why this third group matters.
        var indexNotifications = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (currentPath, content) in finalContentByCurrentPath)
        {
            indexNotifications[currentPath] = content;
        }

        foreach (var newPath in pathMapping.Values)
        {
            if (indexNotifications.ContainsKey(newPath))
            {
                continue;
            }

            var note = await _noteRepository.GetAsync(newPath, cancellationToken).ConfigureAwait(false);
            indexNotifications[newPath] = note?.Content ?? string.Empty;
        }

        foreach (var (oldPath, content) in linkBearingContentByOldPath)
        {
            if (candidateOldPaths.Contains(oldPath) || pathMapping.ContainsKey(oldPath))
            {
                // Already handled above with fresh content - a candidate,
                // or itself moved (and thus already registered at its new
                // path via the loop above).
                continue;
            }

            indexNotifications[oldPath] = content;
        }

        ApplyIndexUpdates(pathMapping.Keys, indexNotifications);

        return (pathMapping, rewrittenNotes);
    }

    /// <summary>
    /// Synchronously brings every registered <see cref="IVaultChangeListener"/>
    /// up to date: a deletion for each old (pre-move) path, then a change
    /// for every path in <paramref name="indexNotifications"/> - every
    /// moved note's new path, every rewritten note, every candidate whose
    /// content ended up unchanged, and every other note that merely
    /// contains a wikilink (see <see cref="MoveAndRewriteAsync"/>'s
    /// remarks for why that last group is included too). Called
    /// before either public method returns, so
    /// <see cref="ILinkIndex.GetBacklinks"/>, <see cref="ILinkIndex.GetGraph"/>
    /// and <see cref="Search.ISearchIndex.Search"/> are never stale for the
    /// caller's very next request.
    /// </summary>
    /// <remarks>
    /// <see cref="ILinkIndex"/>'s incremental <see cref="IVaultChangeListener.NoteChanged"/>
    /// resolves a note's outgoing wikilinks against whatever known-paths
    /// set it currently holds *at the moment of that one call* (see
    /// <see cref="InMemoryLinkIndex"/>'s remarks) - it does not retroactively
    /// re-resolve a note processed earlier once a later call registers a
    /// path it referenced. A folder move can notify several new paths that
    /// reference *each other*, in a dictionary-enumeration order this type
    /// does not control, so a naive single pass here could leave an
    /// earlier-processed note's link resolved against an incomplete
    /// known-paths snapshot. Two passes over every notification - the
    /// first to get every new path registered as known at all, the second
    /// to re-resolve every one of them now that the *complete* post-move
    /// known-paths set is in place - closes that gap. Both
    /// <see cref="IVaultChangeListener.NoteChanged"/> and
    /// <see cref="Search.ISearchIndex"/>'s own tokenization are
    /// idempotent, so redoing the second pass costs a little extra
    /// in-memory work, not correctness.
    /// </remarks>
    private void ApplyIndexUpdates(
        IEnumerable<string> oldPaths,
        IReadOnlyDictionary<string, string> indexNotifications)
    {
        foreach (var oldPath in oldPaths)
        {
            foreach (var listener in _changeListeners)
            {
                listener.NoteDeleted(oldPath);
            }
        }

        // Pass 1: get every notified path registered as known at all.
        // Pass 2: re-resolve every one of them now that the full post-move
        // known-paths set is actually in place - see this method's remarks.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var (path, content) in indexNotifications)
            {
                foreach (var listener in _changeListeners)
                {
                    listener.NoteChanged(path, content);
                }
            }
        }
    }

    /// <summary>
    /// Rewrites <paramref name="content"/>'s non-code wikilink occurrences
    /// that resolved, pre-move, to a moved path (a key in
    /// <paramref name="pathMapping"/>) but no longer resolve to the same
    /// moved note post-move - per docs/06-DATA-MODEL.md's minimal,
    /// style-preserving, alias-preserving rules. Returns
    /// <see langword="null"/> if nothing needed rewriting (including the
    /// case where the note has links, but all of them still resolve
    /// correctly post-move).
    /// </summary>
    private static string? RewriteContent(
        string content,
        IReadOnlyDictionary<string, string> pathMapping,
        IReadOnlyCollection<string> preMoveKnownPaths,
        IReadOnlyCollection<string> postMoveKnownPaths)
    {
        var occurrences = WikiLinkParser.ParseWithPositions(content);
        if (occurrences.Count == 0)
        {
            return null;
        }

        List<(int Start, int Length, string NewText)>? replacements = null;

        foreach (var occurrence in occurrences)
        {
            if (occurrence.IsCode)
            {
                continue;
            }

            var preResolved = WikiLinkResolver.Resolve(occurrence.RawTarget, preMoveKnownPaths);
            if (!pathMapping.TryGetValue(preResolved.TargetPath, out var newPath))
            {
                // Doesn't target anything that moved - leave untouched.
                continue;
            }

            // Minimal: if this occurrence still resolves to the same moved
            // note after the move (e.g. a bare title that stayed unique),
            // it is left byte-for-byte untouched.
            var postResolved = WikiLinkResolver.Resolve(occurrence.RawTarget, postMoveKnownPaths);
            if (string.Equals(postResolved.TargetPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedRawTarget = occurrence.RawTarget.Replace('\\', '/');
            var wasBareTitle = !normalizedRawTarget.Contains('/');

            string newTarget;
            if (wasBareTitle)
            {
                var newBareTitle = WikiLinkResolver.GetBareTitle(newPath);
                var titleResolution = WikiLinkResolver.Resolve(newBareTitle, postMoveKnownPaths);
                newTarget = titleResolution.Exists && string.Equals(titleResolution.TargetPath, newPath, StringComparison.OrdinalIgnoreCase)
                    ? newBareTitle
                    : StripMdExtension(newPath);
            }
            else
            {
                newTarget = StripMdExtension(newPath);
            }

            (replacements ??= []).Add((occurrence.TargetStart, occurrence.TargetLength, newTarget));
        }

        if (replacements is null || replacements.Count == 0)
        {
            return null;
        }

        return ApplyReplacements(content, replacements);
    }

    private static string StripMdExtension(string notePath) =>
        notePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? notePath[..^".md".Length] : notePath;

    /// <summary>
    /// Applies every <c>(Start, Length, NewText)</c> replacement to
    /// <paramref name="content"/>, leaving every other character - inner
    /// whitespace, pipe aliases, brackets, everything outside a rewritten
    /// span - byte-identical.
    /// </summary>
    private static string ApplyReplacements(string content, List<(int Start, int Length, string NewText)> replacements)
    {
        replacements.Sort((a, b) => a.Start.CompareTo(b.Start));

        var builder = new StringBuilder(content.Length);
        var cursor = 0;
        foreach (var (start, length, newText) in replacements)
        {
            builder.Append(content, cursor, start - cursor);
            builder.Append(newText);
            cursor = start + length;
        }

        builder.Append(content, cursor, content.Length - cursor);
        return builder.ToString();
    }

    private async Task<HashSet<string>> GetKnownNotePathsAsync(CancellationToken cancellationToken)
    {
        var tree = await _noteRepository.GetTreeAsync(cancellationToken).ConfigureAwait(false);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFilePaths(tree, paths);
        return paths;
    }

    private static void CollectFilePaths(IReadOnlyList<NoteTreeEntry> entries, HashSet<string> paths)
    {
        foreach (var entry in entries)
        {
            if (entry.Type == NoteEntryType.File)
            {
                paths.Add(entry.Path);
            }
            else if (entry.Children is not null)
            {
                CollectFilePaths(entry.Children, paths);
            }
        }
    }

    private static string NormalizeFolderPath(string rawPath)
    {
        var segments = rawPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments);
    }
}
