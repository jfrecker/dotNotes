using DotNotes.Core.Config;
using DotNotes.Core.Notes;
using DotNotes.Core.Vault;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Links;

/// <summary>
/// Keeps every registered <see cref="IVaultChangeListener"/> (currently
/// <see cref="ILinkIndex"/> and <see cref="Search.ISearchIndex"/>) live
/// for the app's lifetime: performs one initial full-vault scan at
/// startup, then watches the vault root with a single
/// <see cref="FileSystemWatcher"/> (per docs/02-ARCHITECTURE.md - one
/// watcher shared by every index, not one per feature), debouncing
/// rapid-fire events per path via <see cref="FileChangeDebouncer"/> so one
/// logical edit never causes redundant re-parses, and fanning out each
/// settled event to every listener.
/// </summary>
/// <remarks>
/// <para>
/// Lives in DotNotes.Core (not DotNotes.Api) so it is constructible and
/// testable without a web host, per CLAUDE.md.
/// <c>Microsoft.Extensions.Hosting.Abstractions</c> is a lean,
/// framework-agnostic package (used by console apps and worker services,
/// not just ASP.NET Core), so depending on it here does not violate
/// DotNotes.Core's "zero ASP.NET Core dependencies" rule; only
/// <c>Program.cs</c> registers this as a hosted service.
/// </para>
/// <para>
/// Originally (Phase 3) this type depended on <see cref="ILinkIndex"/>
/// directly and called its <c>RebuildAsync</c>/<c>NoteChanged</c>/
/// <c>NoteDeleted</c> members by name. Phase 4 added
/// <see cref="Search.ISearchIndex"/> as a second index that needs to stay
/// live from the exact same watcher pass; rather than this type knowing
/// about two concrete index types (and growing a third dependency for
/// every future index), it now depends only on
/// <see cref="IEnumerable{T}"/> of <see cref="IVaultChangeListener"/>,
/// resolved via DI as one entry per registered index (see
/// <c>Program.cs</c>). Neither <see cref="ILinkIndex"/>'s nor
/// <see cref="Search.ISearchIndex"/>'s own public members changed shape
/// as part of this - see each interface's remarks.
/// </para>
/// </remarks>
public sealed class VaultWatcherService : BackgroundService
{
    // A few hundred ms is enough to coalesce a burst of editor-save
    // events (delete+recreate, or several back-to-back Changed events)
    // into one settled outcome, without making live updates feel laggy.
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly IReadOnlyList<IVaultChangeListener> _changeListeners;
    private readonly INoteRepository _noteRepository;
    private readonly string _vaultRootFullPath;
    private readonly ILogger<VaultWatcherService> _logger;

    private FileSystemWatcher? _watcher;
    private FileChangeDebouncer? _debouncer;

    public VaultWatcherService(
        IOptions<VaultOptions> vaultOptions,
        IEnumerable<IVaultChangeListener> changeListeners,
        INoteRepository noteRepository,
        ILogger<VaultWatcherService> logger)
    {
        _changeListeners = changeListeners.ToArray();
        _noteRepository = noteRepository;
        _vaultRootFullPath = Path.GetFullPath(vaultOptions.Value.RootPath);
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Build every listener's index once, synchronously, before the
        // host reports "started" - so the very first request can never
        // observe a transiently-empty index. This deliberately does NOT
        // call each listener's own RebuildAsync (ILinkIndex.RebuildAsync,
        // ISearchIndex.RebuildAsync): each of those independently walks
        // the vault tree *and* re-reads every file's content from disk,
        // so calling both back-to-back would read every note twice for
        // no benefit. Every listener starts from empty state at process
        // startup, so a single shared scan - one tree walk, one content
        // read per file - that hands each file's content to every
        // listener via NoteChanged is exactly equivalent to a full
        // rebuild here, at half the disk I/O. RebuildAsync remains on
        // each index's own interface for direct/isolated use (unit tests,
        // or a possible future manual "rebuild index" recovery action)
        // where no such shared scan is available.
        await PerformInitialScanAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PerformInitialScanAsync(CancellationToken cancellationToken)
    {
        var tree = await _noteRepository.GetTreeAsync(cancellationToken).ConfigureAwait(false);
        var filePaths = new List<string>();
        CollectFilePaths(tree, filePaths);

        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NoteContent? note;
            try
            {
                note = await _noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidNotePathException ex)
            {
                _logger.LogWarning(ex, "Skipping unsafe path '{Path}' during the initial vault scan.", path);
                continue;
            }

            // The note could have been deleted between GetTreeAsync's
            // snapshot and this read; skip it rather than fail the whole
            // scan - a subsequent file-watcher event (or a later manual
            // RebuildAsync) reconciles it.
            if (note is null)
            {
                continue;
            }

            foreach (var listener in _changeListeners)
            {
                listener.NoteChanged(path, note.Content);
            }
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _debouncer = new FileChangeDebouncer(DebounceWindow, OnSettled);

        // No `Filter` is set (default "*.*"): FileSystemWatcher's Filter
        // matching has platform-specific case-sensitivity quirks, so
        // instead every event is filtered down to ".md" files ourselves
        // in TouchIfMarkdownFile. This means edits to _media/ assets or
        // .nd-shares.json also raise (harmless, immediately-discarded)
        // events, which is a fine trade-off for a personal vault's event
        // volume.
        _watcher = new FileSystemWatcher(_vaultRootFullPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
        };

        _watcher.Changed += (_, e) => TouchIfMarkdownFile(e.FullPath);
        _watcher.Created += (_, e) => OnCreated(e.FullPath);
        _watcher.Deleted += (_, e) => TouchIfMarkdownFile(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            // Per docs/06-DATA-MODEL.md: treat a rename as delete (old
            // path) + create (new path) - no automatic link rewriting in
            // other notes for the MVP. Touching both paths independently
            // reuses the same "re-check what's actually on disk once
            // things settle" logic as every other event type below.
            //
            // Known limitation (out of scope for this phase): a *folder*
            // rename/move only raises a Renamed event for the directory
            // itself, not for each note file underneath it, so notes
            // nested inside a renamed folder are not individually
            // reconciled here - the index becomes stale for that
            // subtree's old paths until the next RebuildAsync. Flagged
            // for follow-up, not silently handled.
            TouchIfMarkdownFile(e.OldFullPath);
            TouchIfMarkdownFile(e.FullPath);
        };
        _watcher.Error += (_, e) =>
            _logger.LogWarning(
                e.GetException(),
                "Vault file-watcher error; the link index may be stale until the next rebuild.");

        _watcher.EnableRaisingEvents = true;

        var stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stoppingToken.Register(() => stoppedTcs.TrySetResult());
        return stoppedTcs.Task;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void TouchIfMarkdownFile(string fullPath)
    {
        if (fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            _debouncer?.Notify(fullPath);
        }
    }

    /// <summary>
    /// Handles a raw <see cref="FileSystemWatcher.Created"/> event, which
    /// on Linux can fire for a brand-new subdirectory. .NET's recursive
    /// watch support (<see cref="FileSystemWatcher.IncludeSubdirectories"/>)
    /// only registers the OS-level (inotify) watch for a new subdirectory
    /// after processing that directory's own Created event - so a file
    /// written into it in the same instant (e.g. "create folder, then
    /// save the first note into it", which is exactly what creating a
    /// note in a new folder does) can have its own Created event missed
    /// entirely, not just delayed. Proactively scanning a newly created
    /// directory for markdown files already on disk closes that race
    /// without depending on watch-registration timing; it is a no-op
    /// (redundant but harmless re-touch) on every other platform/case
    /// where the nested event already arrived on its own.
    /// </summary>
    private void OnCreated(string fullPath)
    {
        TouchIfMarkdownFile(fullPath);

        if (!Directory.Exists(fullPath))
        {
            return;
        }

        try
        {
            foreach (var markdownFile in Directory.EnumerateFiles(fullPath, "*.md", SearchOption.AllDirectories))
            {
                _debouncer?.Notify(markdownFile);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not scan newly created directory '{Path}' for markdown files.", fullPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not scan newly created directory '{Path}' for markdown files.", fullPath);
        }
    }

    /// <summary>
    /// Called at most once per debounce window, once a path's events
    /// have settled.
    /// </summary>
    private void OnSettled(string fullPath)
    {
        // FileChangeDebouncer invokes this synchronously from a Timer
        // callback thread; hand off to an async method (with its own
        // try/catch) rather than blocking that thread on the note
        // repository's async I/O.
        _ = SettleAsync(fullPath);
    }

    private async Task SettleAsync(string fullPath)
    {
        try
        {
            var relativePath = ToVaultRelativePath(fullPath);
            if (relativePath is null)
            {
                return;
            }

            NoteContent? note;
            try
            {
                // Deliberately re-check "does this file exist right now,
                // and what does it contain" rather than trusting which
                // specific WatcherChangeTypes fired - that's what lets a
                // delete-then-recreate save sequence collapse into a
                // single "changed" outcome instead of a spurious
                // delete-then-create pair through the index.
                note = await _noteRepository.GetAsync(relativePath).ConfigureAwait(false);
            }
            catch (InvalidNotePathException ex)
            {
                _logger.LogWarning(ex, "Ignoring vault file-watcher event for unsafe path '{Path}'.", relativePath);
                return;
            }

            foreach (var listener in _changeListeners)
            {
                if (note is null)
                {
                    listener.NoteDeleted(relativePath);
                }
                else
                {
                    listener.NoteChanged(relativePath, note.Content);
                }
            }
        }
        catch (Exception ex)
        {
            // Reconciliation is best-effort background work; an
            // unexpected failure here must not crash the process. Every
            // index may be briefly stale for this one path until the
            // next event (or a full RebuildAsync) reconciles it.
            _logger.LogWarning(
                ex,
                "Unexpected error reconciling the vault indexes after a file-watcher event for '{Path}'.",
                fullPath);
        }
    }

    private string? ToVaultRelativePath(string fullPath)
    {
        var relative = Path.GetRelativePath(_vaultRootFullPath, fullPath).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _debouncer?.Dispose();
        base.Dispose();
    }
}
