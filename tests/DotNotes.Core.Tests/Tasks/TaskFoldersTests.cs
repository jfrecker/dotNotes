using DotNotes.Core.Tasks;

namespace DotNotes.Core.Tests.Tasks;

public sealed class TaskFoldersTests
{
    [Theory]
    [InlineData("Task/Completed/TASK-1 - X.md")]
    [InlineData("Completed/TASK-1 - X.md")]
    [InlineData("Task/ProjectX/Completed/TASK-1 - X.md")]
    [InlineData("Task\\ProjectX\\Completed\\TASK-1 - X.md")]
    public void IsInCompletedFolder_TrueForAnyExactSegment(string path)
    {
        Assert.True(TaskFolders.IsInCompletedFolder(path));
    }

    [Theory]
    [InlineData("Task/completed/TASK-1 - X.md")]
    [InlineData("Task/COMPLETED/TASK-1 - X.md")]
    [InlineData("Task/Completed /TASK-1 - X.md")]
    [InlineData("Task/TASK-1 - Completed.md")]
    [InlineData("")]
    public void IsInCompletedFolder_FalseOtherwise(string path)
    {
        Assert.False(TaskFolders.IsInCompletedFolder(path));
    }
}
