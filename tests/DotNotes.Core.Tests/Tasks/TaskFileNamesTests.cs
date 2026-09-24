using DotNotes.Core.Tasks;

namespace DotNotes.Core.Tests.Tasks;

public sealed class TaskFileNamesTests
{
    [Theory]
    [InlineData("Simple Title", "Simple Title")]
    [InlineData("  Trim me  ", "Trim me")]
    [InlineData("Weird [brackets] | pipe / slash \\ back : colon * star ? q \" quote < > # hash", "Weird brackets pipe slash back colon star q quote hash")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("", "Untitled")]
    [InlineData("   ", "Untitled")]
    [InlineData("[]|\\/:*?\"<>#", "Untitled")]
    public void Sanitize_ProducesSafeFileNameSegment(string input, string expected)
    {
        Assert.Equal(expected, TaskFileNames.Sanitize(input));
    }

    [Fact]
    public void Sanitize_CapsAt80Characters()
    {
        var longTitle = new string('a', 200);
        var sanitized = TaskFileNames.Sanitize(longTitle);
        Assert.True(sanitized.Length <= 80);
    }

    [Fact]
    public void Sanitize_CollapsesInternalWhitespace()
    {
        Assert.Equal("A B C", TaskFileNames.Sanitize("A   B\tC"));
    }

    [Fact]
    public void BuildFileName_CombinesIdAndSanitizedTitle()
    {
        Assert.Equal("TASK-12 - Fix login.md", TaskFileNames.BuildFileName("TASK-12", "Fix login"));
    }

    [Theory]
    [InlineData("tasks/TASK-12 - Fix login.md", "TASK-12", true)]
    [InlineData("TASK-12 - Fix login.md", "TASK-12", true)]
    [InlineData("tasks/task-12 - fix login.md", "TASK-12", true)]
    [InlineData("tasks/Renamed by user.md", "TASK-12", false)]
    [InlineData("tasks/TASK-120 - Not this one.md", "TASK-12", false)]
    public void IsIdDerivedFileName_DetectsConvention(string path, string id, bool expected)
    {
        Assert.Equal(expected, TaskFileNames.IsIdDerivedFileName(path, id));
    }
}
