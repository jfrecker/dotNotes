using DotNotes.Core.Config;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using DotNotes.Core.Search;
using DotNotes.Core.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Links;

/// <summary>
/// Covers the Phase 4 refactor at the heart of this phase:
/// <see cref="VaultWatcherService"/> now depends on
/// <see cref="IEnumerable{T}"/> of <see cref="IVaultChangeListener"/>
/// rather than <see cref="ILinkIndex"/> directly, so a single shared
/// watcher pass (both the initial full-vault scan and every subsequent
/// file-watcher event) must reach *every* registered listener - here, one
/// <see cref="InMemoryLinkIndex"/> and one <see cref="InMemorySearchIndex"/>
/// registered side by side - not just the first one historically wired
/// up. See <c>VaultWatcherServiceTests</c> for the single-listener
/// (link-index-only) coverage this complements.
/// </summary>
public sealed class VaultWatcherServiceFanOutTests : IAsyncLifetime
{
    private DirectoryInfo _vaultDirectory = null!;
    private InMemoryLinkIndex _linkIndex = null!;
    private InMemorySearchIndex _searchIndex = null!;
    private VaultWatcherService _watcherService = null!;

    public async Task InitializeAsync()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-vault-watcher-fanout-tests-");
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        var noteRepository = new FileSystemNoteRepository(vaultOptions);
        _linkIndex = new InMemoryLinkIndex(noteRepository);
        _searchIndex = new InMemorySearchIndex(noteRepository);

        _watcherService = new VaultWatcherService(
            vaultOptions,
            new IVaultChangeListener[] { _linkIndex, _searchIndex },
            noteRepository,
            NullLogger<VaultWatcherService>.Instance);

        await _watcherService.StartAsync(CancellationToken.None);

        // See VaultWatcherServiceTests.InitializeAsync's remarks on why a
        // short settle delay is needed here (first-FileSystemWatcher JIT
        // warm-up), purely a test artifact.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
    }

    public async Task DisposeAsync()
    {
        await _watcherService.StopAsync(CancellationToken.None);
        _watcherService.Dispose();
        _vaultDirectory.Delete(recursive: true);
    }

    [Fact]
    public async Task InitialStartupScan_PopulatesBothIndexesFromNotesAlreadyOnDisk()
    {
        // Uses its own scratch vault (rather than the shared _vaultDirectory
        // fixture, whose watcher is already running) so notes can be
        // written *before* a watcher service ever starts, exercising the
        // synchronous StartAsync initial-scan path cleanly - the same
        // technique GraphEndpointsTests uses for "notes present at
        // startup" coverage.
        var scratchVaultDirectory = Directory.CreateTempSubdirectory("dotnotes-vault-watcher-fanout-initial-scan-");
        try
        {
            WriteNoteToDisk(scratchVaultDirectory.FullName, "projects/idea.md", "See [[projects/other]] for gardening tips.");
            WriteNoteToDisk(scratchVaultDirectory.FullName, "projects/other.md", "Some other note.");

            var vaultOptions = Options.Create(new VaultOptions { RootPath = scratchVaultDirectory.FullName });
            var noteRepository = new FileSystemNoteRepository(vaultOptions);
            var linkIndex = new InMemoryLinkIndex(noteRepository);
            var searchIndex = new InMemorySearchIndex(noteRepository);
            var freshWatcher = new VaultWatcherService(
                vaultOptions,
                new IVaultChangeListener[] { linkIndex, searchIndex },
                noteRepository,
                NullLogger<VaultWatcherService>.Instance);

            await freshWatcher.StartAsync(CancellationToken.None);
            try
            {
                // Both indexes must already reflect the pre-existing notes
                // as soon as StartAsync returns - no waiting on the file
                // watcher needed, since this is the synchronous
                // initial-scan path.
                Assert.Contains("projects/idea.md", linkIndex.GetBacklinks("projects/other.md"));
                Assert.Single(searchIndex.Search("gardening", limit: 10));
            }
            finally
            {
                await freshWatcher.StopAsync(CancellationToken.None);
                freshWatcher.Dispose();
            }
        }
        finally
        {
            scratchVaultDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CreatingANoteOnDisk_IsPickedUpLiveByBothIndexes()
    {
        WriteNoteToDisk("projects/idea.md", "See [[projects/other]] for gardening tips.");

        await WaitUntilAsync(() => _linkIndex.GetGraph().Nodes.Any(n => n.Id == "projects/idea.md" && n.Exists));
        await WaitUntilAsync(() => _searchIndex.Search("gardening", limit: 10).Count == 1);
    }

    [Fact]
    public async Task EditingANoteOnDisk_UpdatesBothIndexesLive()
    {
        WriteNoteToDisk("projects/idea.md", "about gardening");
        await WaitUntilAsync(() => _searchIndex.Search("gardening", limit: 10).Count == 1);

        WriteNoteToDisk("projects/idea.md", "about astronomy now, linking to [[projects/other]]");

        await WaitUntilAsync(() => _searchIndex.Search("gardening", limit: 10).Count == 0);
        await WaitUntilAsync(() => _searchIndex.Search("astronomy", limit: 10).Count == 1);
        await WaitUntilAsync(() => _linkIndex.GetBacklinks("projects/other.md").Contains("projects/idea.md"));
    }

    [Fact]
    public async Task DeletingANoteOnDisk_RemovesItFromBothIndexes()
    {
        WriteNoteToDisk("projects/idea.md", "about gardening, linking to [[projects/other]]");
        await WaitUntilAsync(() => _searchIndex.Search("gardening", limit: 10).Count == 1);
        await WaitUntilAsync(() => _linkIndex.GetBacklinks("projects/other.md").Contains("projects/idea.md"));

        File.Delete(Path.Combine(_vaultDirectory.FullName, "projects", "idea.md"));

        await WaitUntilAsync(() => _searchIndex.Search("gardening", limit: 10).Count == 0);

        // Nothing else links to "projects/idea.md" itself, so per
        // InMemoryLinkIndex's documented behavior it is pruned from the
        // graph entirely once deleted (not left behind as an
        // exists:false "missing" node) - see
        // InMemoryLinkIndexTests.NoteDeleted_NothingElseLinksToIt_RemovesItFromTheGraphEntirely.
        await WaitUntilAsync(() => !_linkIndex.GetGraph().Nodes.Any(n => n.Id == "projects/idea.md"));
    }

    private void WriteNoteToDisk(string relativePath, string content) =>
        WriteNoteToDisk(_vaultDirectory.FullName, relativePath, content);

    private static void WriteNoteToDisk(string vaultRootFullPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(vaultRootFullPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), "Condition was not met within the timeout - the file watcher may not have settled in time.");
    }
}
