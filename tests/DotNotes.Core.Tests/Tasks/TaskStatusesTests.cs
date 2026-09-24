using DotNotes.Core.Config;
using DotNotes.Core.Tasks;

namespace DotNotes.Core.Tests.Tasks;

public sealed class TaskStatusesTests
{
    [Fact]
    public void GetEffectiveStatuses_DefaultConfig_BacklogFirst()
    {
        var options = new TasksOptions();

        Assert.Equal(new[] { "Backlog", "To Do", "In Progress", "Done" }, TaskStatuses.GetEffectiveStatuses(options));
    }

    [Fact]
    public void GetEffectiveStatuses_ConfigOmitsBacklog_PrependsIt()
    {
        var options = new TasksOptions { Statuses = new List<string> { "To Do", "Done" } };

        Assert.Equal(new[] { "Backlog", "To Do", "Done" }, TaskStatuses.GetEffectiveStatuses(options));
    }

    [Fact]
    public void GetEffectiveStatuses_ConfigHasBacklogNotFirst_MovesItToFront_KeepingItsCasing()
    {
        var options = new TasksOptions { Statuses = new List<string> { "To Do", "backlog", "Done" } };

        Assert.Equal(new[] { "backlog", "To Do", "Done" }, TaskStatuses.GetEffectiveStatuses(options));
    }

    [Fact]
    public void GetEffectiveCompletedStatus_MatchesConfiguredCasing()
    {
        var options = new TasksOptions { Statuses = new List<string> { "Backlog", "To Do", "done" }, CompletedStatus = "Done" };

        Assert.Equal("done", TaskStatuses.GetEffectiveCompletedStatus(options));
    }

    [Fact]
    public void GetEffectiveCompletedStatus_NotConfigured_FallsBackToLastEffectiveStatus()
    {
        var options = new TasksOptions { Statuses = new List<string> { "Backlog", "To Do", "In Progress" }, CompletedStatus = "Nonexistent" };

        Assert.Equal("In Progress", TaskStatuses.GetEffectiveCompletedStatus(options));
    }

    [Theory]
    [InlineData("To Do", "To Do")]
    [InlineData("to do", "To Do")]
    [InlineData("", "Backlog")]
    [InlineData(null, "Backlog")]
    [InlineData("Blocked", "Backlog")]
    [InlineData("Backlog", "Backlog")]
    public void ClassifyColumn_MapsRawStatusToEffectiveColumn(string? rawStatus, string expectedColumn)
    {
        var options = new TasksOptions();

        Assert.Equal(expectedColumn, TaskStatuses.ClassifyColumn(options, rawStatus));
    }

    [Fact]
    public void IsBacklogColumnName_IsCaseInsensitive()
    {
        var options = new TasksOptions();

        Assert.True(TaskStatuses.IsBacklogColumnName(options, "backlog"));
        Assert.False(TaskStatuses.IsBacklogColumnName(options, "To Do"));
    }
}
