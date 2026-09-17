using System.Collections.Concurrent;
using DotNotes.Core.Links;

namespace DotNotes.Core.Tests.Links;

public sealed class FileChangeDebouncerTests
{
    private static readonly TimeSpan ShortWindow = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Notify_SingleCall_FiresExactlyOnceAfterTheWindow()
    {
        var callCount = 0;
        using var debouncer = new FileChangeDebouncer(ShortWindow, _ => Interlocked.Increment(ref callCount));

        debouncer.Notify("a.md");

        await WaitUntilAsync(() => Volatile.Read(ref callCount) == 1);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Notify_RapidRepeatedCallsForSamePath_CoalesceIntoASingleCallback()
    {
        var callCount = 0;
        using var debouncer = new FileChangeDebouncer(ShortWindow, _ => Interlocked.Increment(ref callCount));

        // Simulate a burst: many touches to the same path in quick
        // succession, each re-starting the quiet-period timer - exactly
        // like an editor's delete+recreate save, or several rapid
        // FileSystemWatcher "Changed" events for one logical edit.
        for (var i = 0; i < 10; i++)
        {
            debouncer.Notify("a.md");
            await Task.Delay(5);
        }

        await WaitUntilAsync(() => Volatile.Read(ref callCount) >= 1, TimeSpan.FromSeconds(2));

        // Give a little extra time to be sure no *second* callback sneaks
        // in after the first.
        await Task.Delay(ShortWindow * 3);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Notify_DifferentPaths_EachGetsItsOwnIndependentCallback()
    {
        var seenPaths = new ConcurrentBag<string>();
        using var debouncer = new FileChangeDebouncer(ShortWindow, path => seenPaths.Add(path));

        debouncer.Notify("a.md");
        debouncer.Notify("b.md");

        await WaitUntilAsync(() => seenPaths.Count == 2);
        Assert.Contains("a.md", seenPaths);
        Assert.Contains("b.md", seenPaths);
    }

    [Fact]
    public async Task Notify_AfterPreviousCallbackAlreadyFired_FiresAgainForANewBurst()
    {
        var callCount = 0;
        using var debouncer = new FileChangeDebouncer(ShortWindow, _ => Interlocked.Increment(ref callCount));

        debouncer.Notify("a.md");
        await WaitUntilAsync(() => Volatile.Read(ref callCount) == 1);

        debouncer.Notify("a.md");
        await WaitUntilAsync(() => Volatile.Read(ref callCount) == 2);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public void Constructor_NonPositiveWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileChangeDebouncer(TimeSpan.Zero, _ => { }));
    }

    [Fact]
    public void Constructor_NullCallback_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FileChangeDebouncer(ShortWindow, null!));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition was not met within the timeout.");
    }
}
