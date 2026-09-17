using DotNotes.Core.Config;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Links;

/// <summary>
/// Covers <see cref="InMemoryLinkIndex"/>'s full-scan rebuild and its
/// incremental update logic (<see cref="InMemoryLinkIndex.NoteChanged"/> /
/// <see cref="InMemoryLinkIndex.NoteDeleted"/>) directly, with no
/// <see cref="System.IO.FileSystemWatcher"/> involved at all - the
/// incremental methods take a path/content directly, so they're testable
/// in complete isolation from the filesystem watcher that drives them in
/// production (see <c>VaultWatcherServiceTests</c> for that end-to-end
/// wiring).
/// </summary>
public sealed class InMemoryLinkIndexTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _noteRepository;
    private readonly InMemoryLinkIndex _index;

    public InMemoryLinkIndexTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-link-index-tests-");
        _noteRepository = new FileSystemNoteRepository(
            Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName }));
        _index = new InMemoryLinkIndex(_noteRepository);
    }

    public void Dispose() => _vaultDirectory.Delete(recursive: true);

    // ---- RebuildAsync (full scan from disk) ----

    [Fact]
    public async Task RebuildAsync_EmptyVault_ProducesEmptyGraph()
    {
        await _index.RebuildAsync();

        var graph = _index.GetGraph();
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task RebuildAsync_ScansExistingNotesAndBuildsBacklinks()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "# idea");
        await _noteRepository.SaveAsync("daily/2026-09-16.md", "See [[projects/idea]].");

        await _index.RebuildAsync();

        var source = Assert.Single(_index.GetBacklinks("projects/idea.md"));
        Assert.Equal("daily/2026-09-16.md", source);
    }

    [Fact]
    public async Task RebuildAsync_TracksUnresolvedTargetAsMissingNode()
    {
        await _noteRepository.SaveAsync("daily/2026-09-16.md", "See [[not-created-yet]].");

        await _index.RebuildAsync();

        var missing = Assert.Single(_index.GetGraph().Nodes, n => n.Id == "not-created-yet.md");
        Assert.False(missing.Exists);
    }

    [Fact]
    public async Task RebuildAsync_IsSafeToCallAgainAndFullyDiscardsPriorState()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "# idea");
        await _noteRepository.SaveAsync("daily/2026-09-16.md", "[[projects/idea]]");
        await _index.RebuildAsync();
        Assert.Single(_index.GetBacklinks("projects/idea.md"));

        // Delete the *linking* note on disk without going through
        // NoteDeleted - a rebuild must reflect whatever is on disk *now*,
        // discarding all prior in-memory state, per CLAUDE.md's "safe to
        // discard and rebuild entirely from files on disk at any time"
        // rule. "projects/idea.md" itself is untouched and still exists,
        // so it must still appear as a node (now isolated, no incoming
        // edges) - only the stale backlink from the deleted note should
        // be gone.
        await _noteRepository.DeleteAsync("daily/2026-09-16.md");
        await _index.RebuildAsync();

        var node = Assert.Single(_index.GetGraph().Nodes);
        Assert.Equal("projects/idea.md", node.Id);
        Assert.True(node.Exists);
        Assert.Empty(_index.GetGraph().Edges);
        Assert.Empty(_index.GetBacklinks("projects/idea.md"));
    }

    // ---- NoteChanged (create/update) ----

    [Fact]
    public void NoteChanged_NewNoteWithNoLinks_AddsItAsAKnownNoteWithNoEdges()
    {
        _index.NoteChanged("projects/idea.md", "# idea, no links");

        var graph = _index.GetGraph();
        var node = Assert.Single(graph.Nodes);
        Assert.Equal("projects/idea.md", node.Id);
        Assert.True(node.Exists);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void NoteChanged_AddsOutgoingLink_UpdatesBacklinksForTarget()
    {
        _index.NoteChanged("projects/idea.md", "# idea");
        _index.NoteChanged("daily/2026-09-16.md", "See [[projects/idea]].");

        Assert.Equal(["daily/2026-09-16.md"], _index.GetBacklinks("projects/idea.md"));
    }

    [Fact]
    public void NoteChanged_ReparsingWithFewerLinks_RemovesStaleBacklinkEntries()
    {
        _index.NoteChanged("projects/idea.md", "# idea");
        _index.NoteChanged("projects/other.md", "# other");
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]] and [[projects/other]]");

        // Edit the source note so it no longer links to "other".
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]] only now.");

        Assert.Equal(["daily/2026-09-16.md"], _index.GetBacklinks("projects/idea.md"));
        Assert.Empty(_index.GetBacklinks("projects/other.md"));
    }

    [Fact]
    public void NoteChanged_ReparsingWithSameLinkTwice_StillProducesOnlyOneBacklinkEntry()
    {
        _index.NoteChanged("projects/idea.md", "# idea");
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]]");
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]] and now also [[projects/idea]] again");

        Assert.Equal(["daily/2026-09-16.md"], _index.GetBacklinks("projects/idea.md"));
    }

    [Fact]
    public void NoteChanged_CreatingNoteThatWasPreviouslyAnUnresolvedTarget_FlipsExistsToTrue()
    {
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]]");
        Assert.False(_index.GetGraph().Nodes.Single(n => n.Id == "projects/idea.md").Exists);

        _index.NoteChanged("projects/idea.md", "# now it exists");

        Assert.True(_index.GetGraph().Nodes.Single(n => n.Id == "projects/idea.md").Exists);
    }

    [Fact]
    public void NoteChanged_SelfLink_IsTrackedAsABacklinkOfItself()
    {
        _index.NoteChanged("projects/idea.md", "See also [[projects/idea]] itself.");

        Assert.Equal(["projects/idea.md"], _index.GetBacklinks("projects/idea.md"));
    }

    // ---- NoteDeleted ----

    [Fact]
    public void NoteDeleted_RemovesOutgoingLinksOfTheDeletedNote()
    {
        _index.NoteChanged("projects/idea.md", "# idea");
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]]");

        _index.NoteDeleted("daily/2026-09-16.md");

        Assert.Empty(_index.GetBacklinks("projects/idea.md"));
    }

    [Fact]
    public void NoteDeleted_NoteStillLinkedFromElsewhere_RemainsAsMissingBacklinkTarget()
    {
        _index.NoteChanged("projects/idea.md", "# idea");
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]]");

        _index.NoteDeleted("projects/idea.md");

        // Still resolvable as a backlink target...
        Assert.Equal(["daily/2026-09-16.md"], _index.GetBacklinks("projects/idea.md"));

        // ...but flagged missing in the graph.
        var node = _index.GetGraph().Nodes.Single(n => n.Id == "projects/idea.md");
        Assert.False(node.Exists);
    }

    [Fact]
    public void NoteDeleted_NothingElseLinksToIt_RemovesItFromTheGraphEntirely()
    {
        _index.NoteChanged("projects/idea.md", "# idea, no incoming links");

        _index.NoteDeleted("projects/idea.md");

        Assert.Empty(_index.GetGraph().Nodes);
    }

    [Fact]
    public void NoteDeleted_SelfLinkedNoteWithNoOtherReferrers_LeavesNoGhostNode()
    {
        _index.NoteChanged("projects/idea.md", "[[projects/idea]] links to itself only.");

        _index.NoteDeleted("projects/idea.md");

        Assert.Empty(_index.GetGraph().Nodes);
        Assert.Empty(_index.GetBacklinks("projects/idea.md"));
    }

    [Fact]
    public void DeleteThenCreate_IsTreatedAsARename_WithNoAutomaticLinkRewriting()
    {
        // docs/06-DATA-MODEL.md: a rename is delete (old path) + create
        // (new path) - other notes' [[links]] are not rewritten.
        _index.NoteChanged("projects/idea.md", "# idea");
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]]");

        _index.NoteDeleted("projects/idea.md");
        _index.NoteChanged("projects/renamed-idea.md", "# idea, renamed");

        // The old path is still what the other note "points" at (no
        // rewriting), now flagged as missing...
        var oldNode = _index.GetGraph().Nodes.Single(n => n.Id == "projects/idea.md");
        Assert.False(oldNode.Exists);
        Assert.Equal(["daily/2026-09-16.md"], _index.GetBacklinks("projects/idea.md"));

        // ...and the new path exists as its own, currently-unlinked node.
        var newNode = _index.GetGraph().Nodes.Single(n => n.Id == "projects/renamed-idea.md");
        Assert.True(newNode.Exists);
        Assert.Empty(_index.GetBacklinks("projects/renamed-idea.md"));
    }

    // ---- Graph serialization shape ----

    [Fact]
    public void GetGraph_DeduplicatesRepeatedSourceTargetPairsIntoOneEdge()
    {
        _index.NoteChanged("projects/idea.md", "# idea");
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]] mentioned twice: [[projects/idea]]");

        var graph = _index.GetGraph();

        Assert.Single(graph.Edges, e => e.Source == "daily/2026-09-16.md" && e.Target == "projects/idea.md");
    }

    [Fact]
    public void GetGraph_IncludesIsolatedNoteWithNoLinksAsANode()
    {
        _index.NoteChanged("lonely.md", "Nothing links here, and this links nowhere.");

        var node = Assert.Single(_index.GetGraph().Nodes);
        Assert.Equal("lonely.md", node.Id);
        Assert.Equal("lonely", node.Label);
        Assert.True(node.Exists);
    }

    [Fact]
    public void GetGraph_UnresolvedTarget_IsFlaggedExistsFalseWithCorrectLabel()
    {
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/not-created-yet]]");

        var node = _index.GetGraph().Nodes.Single(n => n.Id == "projects/not-created-yet.md");
        Assert.Equal("not-created-yet", node.Label);
        Assert.False(node.Exists);
    }

    [Fact]
    public void GetBacklinks_UnknownTarget_ReturnsEmptyList()
    {
        Assert.Empty(_index.GetBacklinks("never-linked.md"));
    }

    [Fact]
    public void GetBacklinks_IsCaseInsensitiveAndToleratesMissingMdExtension()
    {
        _index.NoteChanged("projects/idea.md", "# idea");
        _index.NoteChanged("daily/2026-09-16.md", "[[projects/idea]]");

        Assert.Equal(["daily/2026-09-16.md"], _index.GetBacklinks("PROJECTS/IDEA"));
    }
}
