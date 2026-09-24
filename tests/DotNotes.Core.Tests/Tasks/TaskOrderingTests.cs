using DotNotes.Core.Tasks;

namespace DotNotes.Core.Tests.Tasks;

public sealed class TaskOrderingTests
{
    private static TaskItem MakeTask(string id, double? ordinal = null, DateTimeOffset? created = null) => new()
    {
        Id = id,
        Title = id,
        Status = "To Do",
        Ordinal = ordinal,
        CreatedDate = created,
        Path = $"tasks/{id}.md",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Comparer_SortsByOrdinalAscending_MissingLast()
    {
        var a = MakeTask("TASK-1", ordinal: 2000);
        var b = MakeTask("TASK-2", ordinal: 1000);
        var c = MakeTask("TASK-3", ordinal: null);

        var sorted = new[] { a, c, b }.OrderBy(t => t, TaskOrdering.Comparer).ToArray();

        Assert.Equal(new[] { b, a, c }, sorted);
    }

    [Fact]
    public void Comparer_FallsBackToCreatedDate_WhenOrdinalsMissing()
    {
        var older = MakeTask("TASK-1", created: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = MakeTask("TASK-2", created: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var sorted = new[] { newer, older }.OrderBy(t => t, TaskOrdering.Comparer).ToArray();

        Assert.Equal(new[] { older, newer }, sorted);
    }

    [Fact]
    public void Comparer_FallsBackToNumericId_WhenOrdinalAndDateMissing()
    {
        var nine = MakeTask("TASK-9");
        var ten = MakeTask("TASK-10");

        var sorted = new[] { ten, nine }.OrderBy(t => t, TaskOrdering.Comparer).ToArray();

        Assert.Equal(new[] { nine, ten }, sorted);
    }

    [Fact]
    public void NextOrdinalForNewTask_EmptyColumn_ReturnsStep()
    {
        Assert.Equal(1000d, TaskOrdering.NextOrdinalForNewTask(Array.Empty<TaskItem>()));
    }

    [Fact]
    public void NextOrdinalForNewTask_ReturnsMaxPlusStep()
    {
        var tasks = new[] { MakeTask("TASK-1", 1000), MakeTask("TASK-2", 3500) };
        Assert.Equal(4500d, TaskOrdering.NextOrdinalForNewTask(tasks));
    }

    [Fact]
    public void ComputeMove_EmptyColumn_AssignsStep()
    {
        var result = TaskOrdering.ComputeMove(Array.Empty<TaskItem>(), 0, "TASK-1");
        Assert.Equal(1000d, result["TASK-1"]);
    }

    [Fact]
    public void ComputeMove_InsertAtEnd_AddsStepToLast()
    {
        var others = new[] { MakeTask("TASK-1", 1000), MakeTask("TASK-2", 2000) };
        var result = TaskOrdering.ComputeMove(others, null, "TASK-3");
        Assert.Equal(3000d, result["TASK-3"]);
        Assert.Single(result);
    }

    [Fact]
    public void ComputeMove_InsertAtStart_HalvesNext()
    {
        var others = new[] { MakeTask("TASK-1", 2000) };
        var result = TaskOrdering.ComputeMove(others, 0, "TASK-2");
        Assert.Equal(1000d, result["TASK-2"]);
        Assert.Single(result);
    }

    [Fact]
    public void ComputeMove_InsertBetween_UsesMidpoint()
    {
        var others = new[] { MakeTask("TASK-1", 1000), MakeTask("TASK-2", 2000) };
        var result = TaskOrdering.ComputeMove(others, 1, "TASK-3");
        Assert.Equal(1500d, result["TASK-3"]);
        Assert.Single(result);
    }

    [Fact]
    public void ComputeMove_TooSmallGap_TriggersFullRebalance()
    {
        var others = new[] { MakeTask("TASK-1", 1000), MakeTask("TASK-2", 1000.0000001) };
        var result = TaskOrdering.ComputeMove(others, 1, "TASK-3");

        // Full rebalance recomputes every task's ordinal (1000, 2000, ...
        // in final order), but only *changed* ones are returned - TASK-1
        // already sits at 1000, so it's correctly omitted.
        Assert.Equal(2, result.Count);
        Assert.Equal(2000d, result["TASK-3"]);
        Assert.Equal(3000d, result["TASK-2"]);
        Assert.False(result.ContainsKey("TASK-1"));
    }

    [Fact]
    public void ComputeMove_MissingOrdinalsOnBothSides_TriggersRebalance()
    {
        var others = new[] { MakeTask("TASK-1", null), MakeTask("TASK-2", null) };
        var result = TaskOrdering.ComputeMove(others, 1, "TASK-3");

        Assert.Equal(1000d, result["TASK-1"]);
        Assert.Equal(2000d, result["TASK-3"]);
        Assert.Equal(3000d, result["TASK-2"]);
    }

    [Fact]
    public void FormatOrdinal_WholeNumber_HasNoDecimal()
    {
        Assert.Equal("1000", TaskOrdering.FormatOrdinal(1000d));
    }

    [Fact]
    public void FormatOrdinal_FractionalNumber_KeepsDecimal()
    {
        Assert.Equal("1500.5", TaskOrdering.FormatOrdinal(1500.5d));
    }
}
