using System.Collections.Concurrent;

namespace DotNotes.Core.Links;

/// <summary>
/// Coalesces rapid-fire "something happened to this path" notifications
/// into a single callback per path, fired only after a quiet period with
/// no further notifications for that same path. Framework-free and
/// independent of <see cref="System.IO.FileSystemWatcher"/>, so it can be
/// unit-tested in isolation - see <see cref="VaultWatcherService"/> for
/// the watcher that drives it in practice.
/// </summary>
/// <remarks>
/// Many editors save a file via delete-then-recreate, or emit several
/// back-to-back "changed" events for a single keystroke-triggered save;
/// without this, each of those would trigger its own redundant re-parse
/// of the same note. Debouncing per path (rather than with one global
/// timer) means an edit to note A never delays reacting to an unrelated,
/// concurrent edit to note B.
/// </remarks>
public sealed class FileChangeDebouncer : IDisposable
{
    private readonly TimeSpan _window;
    private readonly Action<string> _onSettled;
    private readonly ConcurrentDictionary<string, Timer> _pendingTimers;

    public FileChangeDebouncer(TimeSpan window, Action<string> onSettled)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Debounce window must be positive.");
        }

        _window = window;
        _onSettled = onSettled ?? throw new ArgumentNullException(nameof(onSettled));
        _pendingTimers = new ConcurrentDictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records a notification for <paramref name="path"/>: (re)starts
    /// that path's quiet-period timer. If the timer fires with no
    /// intervening call to <see cref="Notify"/> for the same path, the
    /// callback given to the constructor is invoked exactly once with
    /// <paramref name="path"/>.
    /// </summary>
    public void Notify(string path)
    {
        _pendingTimers.AddOrUpdate(
            path,
            addValueFactory: p => new Timer(Fire, p, _window, Timeout.InfiniteTimeSpan),
            updateValueFactory: (_, existingTimer) =>
            {
                existingTimer.Change(_window, Timeout.InfiniteTimeSpan);
                return existingTimer;
            });
    }

    private void Fire(object? state)
    {
        var path = (string)state!;
        if (_pendingTimers.TryRemove(path, out var timer))
        {
            timer.Dispose();
        }

        _onSettled(path);
    }

    public void Dispose()
    {
        foreach (var timer in _pendingTimers.Values)
        {
            timer.Dispose();
        }

        _pendingTimers.Clear();
    }
}
