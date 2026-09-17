using DotNotes.Core.Config;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;
using DotNotes.Core.Search;
using DotNotes.Core.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Reorganization;

/// <summary>
/// Exercises <see cref="VaultReorganizationService"/> end to end over a
/// real <see cref="FileSystemNoteRepository"/>, <see cref="InMemoryLinkIndex"/>
/// and <see cref="InMemorySearchIndex"/> against a throwaway temp vault -
/// the same convention <see cref="Notes.FileSystemNoteRepositoryTests"/>
/// and <c>VaultWatcherServiceFanOutTests</c> use. No
/// <see cref="Links.VaultWatcherService"/> is involved: this type is
/// itself responsible for keeping both indexes live (see
/// docs/06-DATA-MODEL.md's "One orchestration point, index never stale"
/// bullet), so every assertion here checks the indexes immediately after
/// an <see langword="await"/>, with no delay or polling.
/// </summary>
public sealed class VaultReorganizationServiceTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _repository;
    private readonly InMemoryLinkIndex _linkIndex;
    private readonly InMemorySearchIndex _searchIndex;
    private readonly VaultReorganizationService _service;

    public VaultReorganizationServiceTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-reorg-tests-");
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        _repository = new FileSystemNoteRepository(vaultOptions);
        _linkIndex = new InMemoryLinkIndex(_repository);
        _searchIndex = new InMemorySearchIndex(_repository);
        _service = new VaultReorganizationService(
            _repository,
            new IVaultChangeListener[] { _linkIndex, _searchIndex },
            NullLogger<VaultReorganizationService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            // A couple of tests below strip write permission from a
            // subdirectory to simulate a rewrite failure; restore it here
            // too in case a test fails before its own cleanup runs, so
            // recursive delete never itself fails.
            RestoreWritePermissions(_vaultDirectory);
            _vaultDirectory.Delete(recursive: true);
        }
    }

    private static void RestoreWritePermissions(DirectoryInfo directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var subdirectory in directory.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetUnixFileMode(
                    subdirectory.FullName,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (IOException)
            {
                // Best-effort cleanup helper only.
            }
        }
    }

    /// <summary>Rebuilds both indexes from whatever is currently on disk - simulates a "settled" app state before a move is issued.</summary>
    private async Task RebuildIndexesAsync()
    {
        await _linkIndex.RebuildAsync();
        await _searchIndex.RebuildAsync();
    }

    // ---- MoveNoteAsync: rename in place ----

    [Fact]
    public async Task MoveNoteAsync_RenameInSameFolder_RewritesBareAndPathStyleLinksAndPreservesAlias()
    {
        await _repository.SaveAsync("projects/idea.md", "Idea content.");
        await _repository.SaveAsync(
            "projects/other.md",
            "See [[idea]] and [[projects/idea]] and [[projects/idea|Custom Alias]].");
        await RebuildIndexesAsync();

        var result = await _service.MoveNoteAsync("projects/idea.md", "projects/notion.md");

        Assert.Equal("projects/notion.md", result.Path);
        Assert.Equal(new[] { "projects/other.md" }, result.RewrittenNotes);

        var rewritten = (await _repository.GetAsync("projects/other.md"))!.Content;
        Assert.Equal(
            "See [[notion]] and [[projects/notion]] and [[projects/notion|Custom Alias]].",
            rewritten);

        // Index reflects the move and the rewrite immediately - no wait/poll.
        Assert.Contains("projects/other.md", _linkIndex.GetBacklinks("projects/notion.md"));
        Assert.DoesNotContain("projects/other.md", _linkIndex.GetBacklinks("projects/idea.md"));

        // The rewritten links actually resolve to the new path.
        var knownPaths = new[] { "projects/notion.md", "projects/other.md" };
        foreach (var occurrence in WikiLinkParser.Parse(rewritten))
        {
            Assert.Equal("projects/notion.md", WikiLinkResolver.Resolve(occurrence.RawTarget, knownPaths).TargetPath);
        }
    }

    [Fact]
    public async Task MoveNoteAsync_BareTitleLinkThatStaysUnique_IsLeftByteIdenticalAfterFolderChange()
    {
        await _repository.SaveAsync("idea.md", "Idea content.");
        await _repository.SaveAsync("ref.md", "See [[idea]] please.");
        await RebuildIndexesAsync();

        var result = await _service.MoveNoteAsync("idea.md", "projects/idea.md");

        Assert.Equal("projects/idea.md", result.Path);
        Assert.Empty(result.RewrittenNotes);

        var refContent = (await _repository.GetAsync("ref.md"))!.Content;
        Assert.Equal("See [[idea]] please.", refContent);

        Assert.Contains("ref.md", _linkIndex.GetBacklinks("projects/idea.md"));
    }

    [Fact]
    public async Task MoveNoteAsync_PathStyleLinkThatNoLongerResolvesToSameNote_IsRewrittenToFullPath()
    {
        // "beta" bare title is ambiguous between the root-level decoy
        // (0 path segments) and the moved note (>=1 segment) both before
        // and after the move - WikiLinkResolver's tie-break always prefers
        // fewer segments, so the decoy always wins the bare-title
        // fallback. The path-style link below resolves via the *exact
        // path* tier before the move (bypassing the ambiguity entirely),
        // but after the move that exact path no longer exists, so it
        // falls through to the (decoy-winning) bare-title tier - which no
        // longer lands on the moved note, forcing a real rewrite.
        await _repository.SaveAsync("beta.md", "Decoy beta, unrelated content.");
        await _repository.SaveAsync("team/sub/beta.md", "Real beta content.");
        await _repository.SaveAsync("outside.md", "Cross link: [[team/sub/beta]].");
        await RebuildIndexesAsync();

        var result = await _service.MoveNoteAsync("team/sub/beta.md", "squad/sub/beta.md");

        Assert.Equal("squad/sub/beta.md", result.Path);
        Assert.Equal(new[] { "outside.md" }, result.RewrittenNotes);

        var rewritten = (await _repository.GetAsync("outside.md"))!.Content;
        Assert.Equal("Cross link: [[squad/sub/beta]].", rewritten);
    }

    [Fact]
    public async Task MoveNoteAsync_BareTitleLinkThatBecomesAmbiguousAfterMove_IsRewrittenToFullPath()
    {
        // Pre-move, "idea" is the only note titled "idea", so the bare
        // link resolves uniquely to it. The move both changes its title to
        // "notion" *and* introduces a root-level decoy already titled
        // "notion" - post-move, "notion" is ambiguous between the decoy
        // (0 path segments) and the moved note (1 segment), and
        // WikiLinkResolver's tie-break always prefers fewer segments, so
        // the decoy wins. Style-preserving therefore requires falling back
        // to an explicit path rather than emitting a bare "[[notion]]"
        // that would silently point at the wrong note.
        await _repository.SaveAsync("idea.md", "Idea content.");
        await _repository.SaveAsync("notion.md", "Unrelated decoy already named notion.");
        await _repository.SaveAsync("ref.md", "See [[idea]] please.");
        await RebuildIndexesAsync();

        var result = await _service.MoveNoteAsync("idea.md", "team/notion.md");

        Assert.Equal("team/notion.md", result.Path);
        Assert.Equal(new[] { "ref.md" }, result.RewrittenNotes);

        var rewritten = (await _repository.GetAsync("ref.md"))!.Content;
        Assert.Equal("See [[team/notion]] please.", rewritten);

        // The unrelated decoy's own resolution/backlinks are untouched.
        Assert.DoesNotContain("ref.md", _linkIndex.GetBacklinks("notion.md"));
        Assert.Contains("ref.md", _linkIndex.GetBacklinks("team/notion.md"));
    }

    [Fact]
    public async Task MoveNoteAsync_MultipleOccurrencesOfSameLinkInOneNote_AllAreRewritten()
    {
        await _repository.SaveAsync("idea.md", "Idea content.");
        await _repository.SaveAsync(
            "ref.md",
            "First [[idea]] mention. Second [[idea]] mention. Third [[idea|With Alias]] mention.");
        await RebuildIndexesAsync();

        var result = await _service.MoveNoteAsync("idea.md", "notion.md");

        Assert.Equal(new[] { "ref.md" }, result.RewrittenNotes);
        var rewritten = (await _repository.GetAsync("ref.md"))!.Content;
        Assert.Equal(
            "First [[notion]] mention. Second [[notion]] mention. Third [[notion|With Alias]] mention.",
            rewritten);
    }

    [Fact]
    public async Task MoveNoteAsync_MovedNoteHasSelfLinkAndUnrelatedLink_OnlySelfLinkIsRewritten()
    {
        await _repository.SaveAsync("other.md", "Other note, unrelated to the move.");
        await _repository.SaveAsync(
            "idea.md",
            "This note, [[idea]], links to [[other]] as well.");
        await RebuildIndexesAsync();

        var result = await _service.MoveNoteAsync("idea.md", "notion.md");

        Assert.Equal("notion.md", result.Path);
        Assert.Equal(new[] { "notion.md" }, result.RewrittenNotes);

        var content = (await _repository.GetAsync("notion.md"))!.Content;
        Assert.Equal(
            "This note, [[notion]], links to [[other]] as well.",
            content);

        // The unrelated link's resolution is completely unaffected by the move.
        Assert.Contains("notion.md", _linkIndex.GetBacklinks("other.md"));
    }

    [Fact]
    public async Task MoveNoteAsync_SelfLinkInMovedNote_IsRewrittenAndReported()
    {
        await _repository.SaveAsync("idea.md", "This note is called [[idea]] itself.");
        await RebuildIndexesAsync();

        var result = await _service.MoveNoteAsync("idea.md", "notion.md");

        Assert.Equal("notion.md", result.Path);
        Assert.Equal(new[] { "notion.md" }, result.RewrittenNotes);

        var note = (await _repository.GetAsync("notion.md"))!;
        Assert.Equal("This note is called [[notion]] itself.", note.Content);

        // UpdatedAt must reflect the post-rewrite save, not the bare move.
        Assert.Equal(note.UpdatedAt, result.UpdatedAt);
    }

    [Fact]
    public async Task MoveNoteAsync_CodeBlockAndInlineCodeOccurrences_AreNeverRewritten()
    {
        const string content =
            "Real link: [[idea]].\n\n" +
            "```\n" +
            "Fenced [[idea]] should not change.\n" +
            "```\n\n" +
            "Inline `[[idea]]` should not change either.\n";

        await _repository.SaveAsync("idea.md", "Idea content.");
        await _repository.SaveAsync("code-note.md", content);
        await RebuildIndexesAsync();

        var result = await _service.MoveNoteAsync("idea.md", "notion.md");

        Assert.Equal(new[] { "code-note.md" }, result.RewrittenNotes);

        const string expected =
            "Real link: [[notion]].\n\n" +
            "```\n" +
            "Fenced [[idea]] should not change.\n" +
            "```\n\n" +
            "Inline `[[idea]]` should not change either.\n";

        var rewritten = (await _repository.GetAsync("code-note.md"))!.Content;
        Assert.Equal(expected, rewritten);
    }

    [Fact]
    public async Task MoveNoteAsync_StalenessRace_LinkWrittenToDiskWithoutNotifyingIndex_IsStillRewritten()
    {
        await _repository.SaveAsync("target.md", "Target note.");
        await _repository.SaveAsync("source.md", "No links yet.");
        await RebuildIndexesAsync();

        // Simulate the exact race docs/06-DATA-MODEL.md/this phase's spec
        // calls out: content lands on disk (e.g. a flushed autosave), but
        // nothing has told the in-memory index about it yet - there is no
        // VaultWatcherService running in this test, so this is exactly
        // that stale-index window.
        await _repository.SaveAsync("source.md", "Link to [[target]].");
        Assert.Empty(_linkIndex.GetBacklinks("target.md"));

        var result = await _service.MoveNoteAsync("target.md", "renamed-target.md");

        Assert.Equal(new[] { "source.md" }, result.RewrittenNotes);
        var rewritten = (await _repository.GetAsync("source.md"))!.Content;
        Assert.Equal("Link to [[renamed-target]].", rewritten);

        Assert.Contains("source.md", _linkIndex.GetBacklinks("renamed-target.md"));
    }

    [Fact]
    public async Task MoveNoteAsync_SearchIndexReflectsRewriteImmediately()
    {
        await _repository.SaveAsync("idea.md", "Some notes here.");
        await _repository.SaveAsync("ref.md", "A reference note mentioning gardeningunique term.");
        await RebuildIndexesAsync();

        await _service.MoveNoteAsync("idea.md", "notion.md");

        var results = _searchIndex.Search("gardeningunique", limit: 10);
        Assert.Single(results);
        Assert.Equal("ref.md", results[0].Path);

        // The old title ("idea") is no longer a known note title, and the
        // note's body never contained that word - the new title ("notion")
        // now matches by title instead.
        Assert.Empty(_searchIndex.Search("idea", limit: 10));
        Assert.Single(_searchIndex.Search("notion", limit: 10));
    }

    // ---- MoveNoteAsync: rejection leaves everything untouched ----

    [Fact]
    public async Task MoveNoteAsync_DestinationAlreadyExists_ThrowsAndLeavesEverythingUntouched()
    {
        await _repository.SaveAsync("a.md", "Links to [[b]].");
        await _repository.SaveAsync("b.md", "B content.");
        await RebuildIndexesAsync();
        var graphBefore = _linkIndex.GetGraph();

        await Assert.ThrowsAsync<DestinationAlreadyExistsException>(
            () => _service.MoveNoteAsync("a.md", "b.md"));

        Assert.Equal("Links to [[b]].", (await _repository.GetAsync("a.md"))!.Content);
        Assert.Equal("B content.", (await _repository.GetAsync("b.md"))!.Content);

        var graphAfter = _linkIndex.GetGraph();
        Assert.Equal(graphBefore.Nodes.Select(n => n.Id), graphAfter.Nodes.Select(n => n.Id));
        Assert.Equal(graphBefore.Edges.Select(e => (e.Source, e.Target)), graphAfter.Edges.Select(e => (e.Source, e.Target)));
    }

    [Fact]
    public async Task MoveNoteAsync_InvalidDestinationPath_ThrowsAndLeavesSourceUntouched()
    {
        await _repository.SaveAsync("a.md", "Content.");

        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _service.MoveNoteAsync("a.md", "../escape.md"));

        Assert.True(await _repository.ExistsAsync("a.md"));
    }

    [Fact]
    public async Task MoveNoteAsync_SourceDoesNotExist_ThrowsSourceNotFound()
    {
        await Assert.ThrowsAsync<SourceNotFoundException>(
            () => _service.MoveNoteAsync("missing.md", "destination.md"));
    }

    [Fact]
    public async Task MoveNoteAsync_RewriteFailureOnOneSource_IsSkippedWithoutFailingTheMove()
    {
        if (OperatingSystem.IsWindows())
        {
            // The write-permission-based failure injection below is
            // Unix-specific; this scenario is still exercised on Linux/CI.
            return;
        }

        var restrictedDirectory = Path.Combine(_vaultDirectory.FullName, "restricted");
        Directory.CreateDirectory(restrictedDirectory);
        await _repository.SaveAsync("restricted/source.md", "Link to [[target]].");
        await _repository.SaveAsync("target.md", "Target note.");
        await RebuildIndexesAsync();

        // Strip write permission from the directory so SaveAsync's atomic
        // temp-file write for "restricted/source.md" fails - simulating a
        // rewrite failure for exactly one candidate source, without
        // touching the note actually being moved.
        File.SetUnixFileMode(restrictedDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await _service.MoveNoteAsync("target.md", "renamed-target.md");

            Assert.Equal("renamed-target.md", result.Path);
            Assert.True(await _repository.ExistsAsync("renamed-target.md"));
            Assert.DoesNotContain("restricted/source.md", result.RewrittenNotes);
        }
        finally
        {
            File.SetUnixFileMode(
                restrictedDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // The failed source's content was left exactly as it was.
        Assert.Equal("Link to [[target]].", (await _repository.GetAsync("restricted/source.md"))!.Content);
    }

    // ---- MoveFolderAsync ----

    [Fact]
    public async Task MoveFolderAsync_NestedSubfolders_RewritesForcedLinksAndLeavesOthersResolvable()
    {
        // See MoveNoteAsync_PathStyleLinkThatNoLongerResolvesToSameNote_IsRewrittenToFullPath's
        // remarks for why this decoy/tie-break shape deterministically
        // forces a real content rewrite (rather than relying on the
        // bare-title fallback silently rescuing every occurrence).
        await _repository.SaveAsync("beta.md", "Decoy beta, unrelated content.");
        await _repository.SaveAsync("team/alpha.md", "Reaches [[team/sub/beta]] too.");
        await _repository.SaveAsync("team/sub/beta.md", "Real beta content.");
        await _repository.SaveAsync("team/gamma.md", "Gamma content.");
        await _repository.SaveAsync("outside.md", "Cross links: [[team/sub/beta]] and [[gamma]].");
        await RebuildIndexesAsync();

        var result = await _service.MoveFolderAsync("team", "squad");

        Assert.Equal("squad", result.Path);
        Assert.Equal(
            new[] { "outside.md", "squad/alpha.md" },
            result.RewrittenNotes.OrderBy(p => p, StringComparer.Ordinal).ToArray());

        Assert.Equal("Reaches [[squad/sub/beta]] too.", (await _repository.GetAsync("squad/alpha.md"))!.Content);
        Assert.Equal("Cross links: [[squad/sub/beta]] and [[gamma]].", (await _repository.GetAsync("outside.md"))!.Content);

        // Moved-but-not-rewritten notes kept their content, and their old
        // paths are gone.
        Assert.Equal("Real beta content.", (await _repository.GetAsync("squad/sub/beta.md"))!.Content);
        Assert.Equal("Gamma content.", (await _repository.GetAsync("squad/gamma.md"))!.Content);
        Assert.False(await _repository.ExistsAsync("team/alpha.md"));
        Assert.False(await _repository.ExistsAsync("team/sub/beta.md"));
        Assert.False(await _repository.ExistsAsync("team/gamma.md"));

        // Index reflects every moved path and every rewrite immediately.
        Assert.Contains("squad/alpha.md", _linkIndex.GetBacklinks("squad/sub/beta.md"));
        Assert.Contains("outside.md", _linkIndex.GetBacklinks("squad/sub/beta.md"));
        Assert.Contains("outside.md", _linkIndex.GetBacklinks("squad/gamma.md"));
        Assert.Empty(_linkIndex.GetBacklinks("team/sub/beta.md"));
    }

    [Fact]
    public async Task MoveFolderAsync_DestinationAlreadyExists_ThrowsAndLeavesEverythingUntouched()
    {
        await _repository.SaveAsync("team/note.md", "Content referencing [[other]].");
        await _repository.SaveAsync("other.md", "Other content.");
        await _repository.CreateFolderAsync("squad");
        await RebuildIndexesAsync();

        await Assert.ThrowsAsync<DestinationAlreadyExistsException>(
            () => _service.MoveFolderAsync("team", "squad"));

        Assert.True(await _repository.ExistsAsync("team/note.md"));
        Assert.Equal("Content referencing [[other]].", (await _repository.GetAsync("team/note.md"))!.Content);
    }

    [Fact]
    public async Task MoveFolderAsync_DestinationIsOwnDescendant_ThrowsAndLeavesEverythingUntouched()
    {
        await _repository.SaveAsync("team/note.md", "Content.");

        await Assert.ThrowsAsync<InvalidNotePathException>(
            () => _service.MoveFolderAsync("team", "team/nested/team"));

        Assert.True(await _repository.ExistsAsync("team/note.md"));
    }

    [Fact]
    public async Task MoveFolderAsync_SourceDoesNotExist_ThrowsSourceNotFound()
    {
        await Assert.ThrowsAsync<SourceNotFoundException>(
            () => _service.MoveFolderAsync("missing-folder", "destination"));
    }

    // ---- Coordinator-requested fixes: lost-edit race + incomplete re-indexing ----

    [Fact]
    public async Task MoveNoteAsync_ContentSavedBetweenScanAndRewrite_SurvivesAlongsideTheRewrittenLink()
    {
        await _repository.SaveAsync("target.md", "Target note.");
        await _repository.SaveAsync("source.md", "Link to [[target]].");
        await RebuildIndexesAsync();

        // Simulates a debounced autosave PUT (or a concurrent MCP
        // update_note call) landing on "source.md" after the service's
        // pre-move scan already captured its (now stale) content, but
        // before the service re-reads it to compute/save the rewrite.
        // The reorganization semaphore only serializes reorganizations
        // against each other, never against ordinary saves, so this is a
        // real race, not a hypothetical one.
        var concurrentEditApplied = false;
        var interleavingRepository = new InterleavingNoteRepository(_repository, "source.md", async () =>
        {
            concurrentEditApplied = true;
            await _repository.SaveAsync("source.md", "Link to [[target]]. Concurrently added sentence.");
        });

        var interleavingService = new VaultReorganizationService(
            interleavingRepository,
            new IVaultChangeListener[] { _linkIndex, _searchIndex },
            NullLogger<VaultReorganizationService>.Instance);

        var result = await interleavingService.MoveNoteAsync("target.md", "renamed-target.md");

        Assert.True(concurrentEditApplied, "The test's interleaving hook never fired - the test itself is broken.");
        Assert.Equal(new[] { "source.md" }, result.RewrittenNotes);

        // Both the concurrent edit AND the link rewrite must survive -
        // neither one clobbers the other.
        var finalContent = (await _repository.GetAsync("source.md"))!.Content;
        Assert.Equal("Link to [[renamed-target]]. Concurrently added sentence.", finalContent);
    }

    [Fact]
    public async Task MoveNoteAsync_RenameToPreviouslyUnresolvedBareTitle_BecomesResolvedImmediately()
    {
        await _repository.SaveAsync("ref.md", "See [[newname]] please.");
        await _repository.SaveAsync("old.md", "Old note content.");
        await RebuildIndexesAsync();

        // Before the rename, "newname.md" is a tracked-but-missing
        // backlink target - nothing on disk is titled "newname" yet.
        Assert.Contains("ref.md", _linkIndex.GetBacklinks("newname.md"));
        var missingNode = _linkIndex.GetGraph().Nodes.Single(n => n.Id == "newname.md");
        Assert.False(missingNode.Exists);

        await _service.MoveNoteAsync("old.md", "newname.md");

        Assert.Contains("ref.md", _linkIndex.GetBacklinks("newname.md"));
        var resolvedNode = _linkIndex.GetGraph().Nodes.Single(n => n.Id == "newname.md");
        Assert.True(resolvedNode.Exists);
    }

    [Fact]
    public async Task MoveNoteAsync_RenameCreatingBareTitleCollisionInUnrelatedNote_UpdatesItsResolutionImmediately()
    {
        await _repository.SaveAsync("a/target.md", "Existing target note.");
        await _repository.SaveAsync("ref.md", "See [[target]] please.");
        await _repository.SaveAsync("source.md", "Content to be renamed.");
        await RebuildIndexesAsync();

        // Sanity: before the rename, "[[target]]" in ref.md resolves
        // uniquely to the only note titled "target" - "ref.md" is *not* a
        // rewrite candidate for the upcoming move (its pre-move
        // resolution doesn't target the note about to move at all).
        Assert.Contains("ref.md", _linkIndex.GetBacklinks("a/target.md"));

        await _service.MoveNoteAsync("source.md", "target.md");

        // ref.md's link text never changed, but the rename introduced a
        // bare-title collision that the root-level "target.md" (fewer
        // path segments - see WikiLinkResolver's tie-break rule) now wins:
        // the exact same link text now resolves to a *different* note than
        // before, and the index must reflect that immediately, even though
        // ref.md itself was never touched or rewritten by this move.
        Assert.Contains("ref.md", _linkIndex.GetBacklinks("target.md"));
        Assert.DoesNotContain("ref.md", _linkIndex.GetBacklinks("a/target.md"));
    }

    /// <summary>
    /// A test-only <see cref="INoteRepository"/> decorator that runs a
    /// caller-supplied hook the first time <see cref="GetAsync"/> is
    /// called for one specific watched path, after the inner call
    /// returns - simulating a concurrent write landing on that note
    /// between two of <see cref="VaultReorganizationService"/>'s own reads
    /// of it (its pre-move scan read, and its post-move fresh re-read).
    /// Every other member delegates straight through.
    /// </summary>
    private sealed class InterleavingNoteRepository : INoteRepository
    {
        private readonly INoteRepository _inner;
        private readonly string _watchedPath;
        private readonly Func<Task> _onFirstReadOfWatchedPath;
        private int _watchedPathReadCount;

        public InterleavingNoteRepository(INoteRepository inner, string watchedPath, Func<Task> onFirstReadOfWatchedPath)
        {
            _inner = inner;
            _watchedPath = watchedPath;
            _onFirstReadOfWatchedPath = onFirstReadOfWatchedPath;
        }

        public async Task<NoteContent?> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            var result = await _inner.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (string.Equals(path, _watchedPath, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref _watchedPathReadCount) == 1)
            {
                await _onFirstReadOfWatchedPath().ConfigureAwait(false);
            }

            return result;
        }

        public Task<IReadOnlyList<NoteTreeEntry>> GetTreeAsync(CancellationToken cancellationToken = default) =>
            _inner.GetTreeAsync(cancellationToken);

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
            _inner.ExistsAsync(path, cancellationToken);

        public Task<NoteWriteResult> SaveAsync(string path, string content, CancellationToken cancellationToken = default) =>
            _inner.SaveAsync(path, content, cancellationToken);

        public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(path, cancellationToken);

        public Task<string> CreateFolderAsync(string path, CancellationToken cancellationToken = default) =>
            _inner.CreateFolderAsync(path, cancellationToken);

        public Task<NoteWriteResult> MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            _inner.MoveAsync(sourcePath, destinationPath, cancellationToken);

        public Task<string> MoveFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            _inner.MoveFolderAsync(sourcePath, destinationPath, cancellationToken);
    }
}
