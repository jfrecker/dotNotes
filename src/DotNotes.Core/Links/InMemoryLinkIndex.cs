using DotNotes.Core.Notes;

namespace DotNotes.Core.Links;

/// <summary>
/// Default <see cref="ILinkIndex"/> implementation: three in-memory
/// dictionaries (known note paths, each note's current outgoing link
/// targets, and the resulting target -&gt; sources backlink map) guarded
/// by a single lock. This is a derived cache only - see
/// docs/06-DATA-MODEL.md's "Backlink index" section - and is safe to
/// discard/rebuild at any time via <see cref="RebuildAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Known, documented trade-off: adding or removing a note changes the
/// "known note paths" universe that <see cref="WikiLinkResolver"/> uses
/// to disambiguate bare-title links (docs/06-DATA-MODEL.md's ambiguous
/// case - see <see cref="WikiLinkResolver"/>'s remarks for the rule this
/// codebase applies). That means a create/delete elsewhere in the vault
/// can, in principle, change what a *different*, previously-resolved or
/// previously-ambiguous bare-title link *should* now resolve to. This
/// implementation does not proactively re-resolve every other note's
/// links whenever the known-paths set changes - only the note that
/// actually changed gets re-parsed/re-resolved (<see cref="NoteChanged"/>
/// / <see cref="NoteDeleted"/> touch exactly one source's outgoing
/// links). Doing a full-vault re-resolution on every single file event
/// would turn an O(1 file) incremental update into an O(vault) one,
/// which does not scale to "hundreds to a few thousand notes" as a
/// per-keystroke-adjacent-save cost. A stale ambiguous/unresolved link
/// self-corrects the next time its own source note is saved, or a full
/// <see cref="RebuildAsync"/> is performed. This is called out
/// explicitly since it is a real (if edge-case) deviation from "always
/// perfectly up to date" - flagged for product-owner review rather than
/// silently assumed.
/// </para>
/// </remarks>
public sealed class InMemoryLinkIndex : ILinkIndex
{
    private readonly INoteRepository _noteRepository;
    private readonly object _lock = new();

    /// <summary>Every note path currently known to exist on disk.</summary>
    private readonly HashSet<string> _knownNotePaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// sourcePath -&gt; the set of already-resolved target paths that
    /// source's content currently links to. Internal bookkeeping used to
    /// diff a note's old vs. new outgoing links on <see cref="NoteChanged"/>
    /// and to know what to remove on <see cref="NoteDeleted"/> - not
    /// itself part of docs/06-DATA-MODEL.md's public backlink shape.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _outgoingLinksBySource =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// targetPath -&gt; sourcePaths that link to it. This is
    /// docs/06-DATA-MODEL.md's <c>Dictionary&lt;targetPath, List&lt;sourcePath&gt;&gt;</c>
    /// backlink index; a <see cref="HashSet{T}"/> is used per target
    /// instead of a list purely as an implementation detail (dedup plus
    /// O(1) add/remove), and is projected to a list at the
    /// <see cref="GetBacklinks"/> boundary.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _backlinksByTarget =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemoryLinkIndex(INoteRepository noteRepository)
    {
        _noteRepository = noteRepository;
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var tree = await _noteRepository.GetTreeAsync(cancellationToken).ConfigureAwait(false);
        var filePaths = new List<string>();
        CollectFilePaths(tree, filePaths);

        var newKnownPaths = new HashSet<string>(filePaths, StringComparer.OrdinalIgnoreCase);
        var newOutgoing = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var newBacklinks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The note could have been deleted between GetTreeAsync's
            // snapshot and this read (e.g. a concurrent edit mid-startup);
            // skip it rather than fail the whole rebuild - a subsequent
            // file-watcher event (or the next rebuild) reconciles it.
            var note = await _noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (note is null)
            {
                newKnownPaths.Remove(path);
                continue;
            }

            var targets = ResolveOutgoingTargets(note.Content, newKnownPaths);
            newOutgoing[path] = targets;

            foreach (var target in targets)
            {
                AddBacklink(newBacklinks, target, path);
            }
        }

        lock (_lock)
        {
            ReplaceContents(_knownNotePaths, newKnownPaths);
            ReplaceContents(_outgoingLinksBySource, newOutgoing);
            ReplaceContents(_backlinksByTarget, newBacklinks);
        }
    }

    public IReadOnlyList<string> GetBacklinks(string targetPath)
    {
        var normalized = WikiLinkResolver.NormalizeToNotePath(targetPath);
        lock (_lock)
        {
            if (_backlinksByTarget.TryGetValue(normalized, out var sources))
            {
                return sources.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            return Array.Empty<string>();
        }
    }

    public LinkGraph GetGraph()
    {
        lock (_lock)
        {
            var nodePaths = new HashSet<string>(_knownNotePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var target in _backlinksByTarget.Keys)
            {
                // Every target with at least one recorded source becomes
                // a node even if it doesn't exist (yet, or any more) -
                // this is exactly docs/06-DATA-MODEL.md's "unresolved
                // link targets flagged exists: false" requirement.
                nodePaths.Add(target);
            }

            var nodes = nodePaths
                .Select(path => new LinkGraphNode
                {
                    Id = path,
                    Label = WikiLinkResolver.GetBareTitle(path),
                    Exists = _knownNotePaths.Contains(path)
                })
                .OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var edges = new List<LinkGraphEdge>();
            foreach (var (target, sources) in _backlinksByTarget)
            {
                foreach (var source in sources)
                {
                    edges.Add(new LinkGraphEdge { Source = source, Target = target });
                }
            }

            return new LinkGraph
            {
                Nodes = nodes,
                Edges = edges
                    .OrderBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Target, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
    }

    public void NoteChanged(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        lock (_lock)
        {
            _knownNotePaths.Add(path);

            var newTargets = ResolveOutgoingTargets(content, _knownNotePaths);

            _outgoingLinksBySource.TryGetValue(path, out var oldTargets);
            oldTargets ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var removedTarget in oldTargets.Except(newTargets, StringComparer.OrdinalIgnoreCase))
            {
                RemoveBacklink(_backlinksByTarget, removedTarget, path);
            }

            foreach (var addedTarget in newTargets.Except(oldTargets, StringComparer.OrdinalIgnoreCase))
            {
                AddBacklink(_backlinksByTarget, addedTarget, path);
            }

            _outgoingLinksBySource[path] = newTargets;
        }
    }

    public void NoteDeleted(string path)
    {
        lock (_lock)
        {
            // Remove from the known-paths set *before* removing this
            // note's own outgoing links below: if the deleted note had a
            // self-link ([[itself]]), that target's source set can then
            // correctly drop to zero and be pruned (see RemoveBacklink) -
            // ordering this the other way around would leave a
            // zero-source, exists:false "ghost" node behind for a note
            // that linked only to itself.
            _knownNotePaths.Remove(path);

            if (_outgoingLinksBySource.TryGetValue(path, out var targets))
            {
                foreach (var target in targets)
                {
                    RemoveBacklink(_backlinksByTarget, target, path);
                }

                _outgoingLinksBySource.Remove(path);
            }

            // Note: `path` may still be a key in _backlinksByTarget with
            // other sources remaining (other notes' links to it) - that
            // entry is deliberately left alone, so it stays resolvable as
            // a "missing" backlink target per docs/06-DATA-MODEL.md.
        }
    }

    /// <summary>
    /// Parses <paramref name="content"/>'s wikilinks and resolves each
    /// one's raw target against <paramref name="knownNotePaths"/>,
    /// de-duplicating so linking to the same target twice in one note
    /// only produces one edge (per docs/06-DATA-MODEL.md's "Edges ...
    /// deduplicated" rule).
    /// </summary>
    private static HashSet<string> ResolveOutgoingTargets(string content, IReadOnlyCollection<string> knownNotePaths)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in WikiLinkParser.Parse(content))
        {
            var resolved = WikiLinkResolver.Resolve(occurrence.RawTarget, knownNotePaths);
            targets.Add(resolved.TargetPath);
        }

        return targets;
    }

    private static void AddBacklink(Dictionary<string, HashSet<string>> backlinks, string target, string source)
    {
        if (!backlinks.TryGetValue(target, out var sources))
        {
            sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            backlinks[target] = sources;
        }

        sources.Add(source);
    }

    private static void RemoveBacklink(Dictionary<string, HashSet<string>> backlinks, string target, string source)
    {
        if (!backlinks.TryGetValue(target, out var sources))
        {
            return;
        }

        sources.Remove(source);

        // A target with zero remaining sources has no reason to keep
        // appearing as a dictionary entry: if it's a real note, it's
        // still a graph node via _knownNotePaths regardless; if it's a
        // missing/unresolved target, nothing links to it any more so it
        // shouldn't be a node at all. Pruning here keeps the index's
        // memory footprint proportional to *current* links, not every
        // link that has ever existed.
        if (sources.Count == 0)
        {
            backlinks.Remove(target);
        }
    }

    private static void ReplaceContents(HashSet<string> target, HashSet<string> replacement)
    {
        target.Clear();
        foreach (var item in replacement)
        {
            target.Add(item);
        }
    }

    private static void ReplaceContents(
        Dictionary<string, HashSet<string>> target,
        Dictionary<string, HashSet<string>> replacement)
    {
        target.Clear();
        foreach (var (key, value) in replacement)
        {
            target[key] = value;
        }
    }

    private static void CollectFilePaths(IReadOnlyList<NoteTreeEntry> entries, List<string> filePaths)
    {
        foreach (var entry in entries)
        {
            if (entry.Type == NoteEntryType.File)
            {
                filePaths.Add(entry.Path);
            }
            else if (entry.Children is not null)
            {
                CollectFilePaths(entry.Children, filePaths);
            }
        }
    }
}
