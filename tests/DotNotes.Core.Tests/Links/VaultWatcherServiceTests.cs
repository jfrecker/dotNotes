using DotNotes.Core.Config;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using DotNotes.Core.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Links;

/// <summary>
/// Drives the real <see cref="System.IO.FileSystemWatcher"/>-backed
/// <see cref="VaultWatcherService"/> against a real temp directory on
/// disk, end-to-end. This covers the actual watcher wiring, which is not
/// exercised by <c>InMemoryLinkIndexTests</c> (drives the index update
/// logic directly, no filesystem) or <c>FileChangeDebouncerTests</c>
/// (drives the debounce mechanism in isolation, no filesystem). Uses
/// generous polling timeouts rather than fixed sleeps to stay fast on
/// quick machines and non-flaky on slow/loaded ones.
/// </summary>
public sealed class VaultWatcherServiceTests : IAsyncLifetime
{
    private DirectoryInfo _vaultDirectory = null!;
    private InMemoryLinkIndex _index = null!;
    private VaultWatcherService _watcherService = null!;

    public async Task InitializeAsync()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-vault-watcher-tests-");
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        var noteRepository = new FileSystemNoteRepository(vaultOptions);
        _index = new InMemoryLinkIndex(noteRepository);
        _watcherService = new VaultWatcherService(
            vaultOptions,
            new IVaultChangeListener[] { _index },
            noteRepository,
            NullLogger<VaultWatcherService>.Instance);

        await _watcherService.StartAsync(CancellationToken.None);

        // Settle delay: FileSystemWatcher.EnableRaisingEvents=true issues
        // an async OS-level watch (ReadDirectoryChangesW) that is
        // effectively immediate once the type has JIT-warmed up - but the
        // very first FileSystemWatcher ever constructed in a process can
        // have enough one-time JIT/interop warm-up cost that a write
        // performed with zero delay immediately afterwards can race
        // ahead of the watch actually becoming active, causing that one
        // write's Created event to be missed entirely (not delayed -
        // genuinely never raised). Real editors never write to the vault
        // microseconds after the app starts, so this is purely a test
        // artifact of "start the watcher, then write instantly" - a
        // short delay here avoids it without touching production timing.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
    }

    public async Task DisposeAsync()
    {
        await _watcherService.StopAsync(CancellationToken.None);
        _watcherService.Dispose();
        _vaultDirectory.Delete(recursive: true);
    }

    [Fact]
    public async Task CreatingANoteOnDisk_IsPickedUpLiveAndAddedToTheGraph()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");

        await WaitUntilAsync(() => _index.GetGraph().Nodes.Any(n => n.Id == "projects/idea.md" && n.Exists));
    }

    [Fact]
    public async Task EditingANoteOnDisk_UpdatesItsOutgoingLinksLive()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");
        WriteNoteToDisk("daily/2026-09-16.md", "no links yet");
        await WaitUntilAsync(() => _index.GetGraph().Nodes.Count(n => n.Exists) == 2);

        WriteNoteToDisk("daily/2026-09-16.md", "now linking to [[projects/idea]]");

        await WaitUntilAsync(() => _index.GetBacklinks("projects/idea.md").Contains("daily/2026-09-16.md"));
    }

    [Fact]
    public async Task DeletingANoteOnDisk_RemovesOutgoingLinksButKeepsItAsAMissingBacklinkTarget()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");
        WriteNoteToDisk("daily/2026-09-16.md", "[[projects/idea]]");
        await WaitUntilAsync(() => _index.GetBacklinks("projects/idea.md").Contains("daily/2026-09-16.md"));

        File.Delete(Path.Combine(_vaultDirectory.FullName, "projects", "idea.md"));

        await WaitUntilAsync(() =>
        {
            var node = _index.GetGraph().Nodes.FirstOrDefault(n => n.Id == "projects/idea.md");
            return node is { Exists: false };
        });
        Assert.Contains("daily/2026-09-16.md", _index.GetBacklinks("projects/idea.md"));
    }

    [Fact]
    public async Task RenamingANoteOnDisk_IsTreatedAsDeletePlusCreateWithNoLinkRewriting()
    {
        WriteNoteToDisk("projects/idea.md", "# idea");
        WriteNoteToDisk("daily/2026-09-16.md", "[[projects/idea]]");
        await WaitUntilAsync(() => _index.GetBacklinks("projects/idea.md").Contains("daily/2026-09-16.md"));

        var oldFullPath = Path.Combine(_vaultDirectory.FullName, "projects", "idea.md");
        var newFullPath = Path.Combine(_vaultDirectory.FullName, "projects", "renamed-idea.md");
        File.Move(oldFullPath, newFullPath);

        await WaitUntilAsync(() =>
        {
            var oldNode = _index.GetGraph().Nodes.FirstOrDefault(n => n.Id == "projects/idea.md");
            var newNode = _index.GetGraph().Nodes.FirstOrDefault(n => n.Id == "projects/renamed-idea.md");
            return oldNode is { Exists: false } && newNode is { Exists: true };
        });

        // No automatic link rewriting: the other note's link still
        // (correctly, per docs/06-DATA-MODEL.md) points at the old,
        // now-missing path.
        Assert.Contains("daily/2026-09-16.md", _index.GetBacklinks("projects/idea.md"));
        Assert.Empty(_index.GetBacklinks("projects/renamed-idea.md"));
    }

    private void WriteNoteToDisk(string relativePath, string content)
    {
        var fullPath = Path.Combine(_vaultDirectory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        // Generous default: each test spins up a *real* FileSystemWatcher
        // plus the FileChangeDebouncer's real Timer-based debounce
        // window (a few hundred ms by design), and CI machines can be
        // slower/busier than a local dev box. A larger ceiling only costs
        // time on the (rare, load-dependent) slow path - every passing
        // run above still returns as soon as the condition is actually
        // met, typically well under a second.
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
