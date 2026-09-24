using System.ComponentModel;
using System.Reflection;
using DotNotes.Api.Mcp;
using DotNotes.Core.Config;
using DotNotes.Core.Links;
using DotNotes.Core.Notes;
using DotNotes.Core.Reorganization;
using DotNotes.Core.Tasks;
using DotNotes.Core.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace DotNotes.Api.Tests.Mcp;

/// <summary>
/// Regression tests for the review findings on the task MCP tools: every
/// note-layer failure must surface as an <see cref="McpException"/>, the
/// <c>update_task</c> description must not claim <c>title</c> can be
/// cleared, and <c>move_task</c> accepts <c>beforeId</c>.
/// </summary>
public sealed class DotNotesTaskMcpReviewRegressionTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _repository;
    private readonly TaskService _taskService;
    private readonly DotNotesTaskMcpTools _tools;

    public DotNotesTaskMcpReviewRegressionTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-task-mcp-review-tests-");
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        _repository = new FileSystemNoteRepository(vaultOptions);
        var tasksOptions = Options.Create(new TasksOptions());
        var taskIndex = new InMemoryTaskIndex(_repository, tasksOptions);
        var linkIndex = new InMemoryLinkIndex(_repository);
        var reorganization = new VaultReorganizationService(
            _repository,
            new IVaultChangeListener[] { taskIndex, linkIndex },
            NullLogger<VaultReorganizationService>.Instance);
        _taskService = new TaskService(_repository, taskIndex, reorganization, tasksOptions);
        _tools = new DotNotesTaskMcpTools(_taskService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            _vaultDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UpdateTask_EmptyTitle_IsAnMcpException_AndDescriptionNoLongerClaimsTitleCanBeCleared()
    {
        var task = await _tools.CreateTask("Keep me");

        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.UpdateTask(task.Id, title: ""));
        Assert.Contains("Title", ex.Message);

        var method = typeof(DotNotesTaskMcpTools).GetMethod(nameof(DotNotesTaskMcpTools.UpdateTask))!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.DoesNotContain("(title, priority", description);
        Assert.Contains("title cannot be cleared", description);
    }

    [Fact]
    public async Task UpdateTask_RenameOntoAnExistingNote_IsAnMcpException()
    {
        var task = await _tools.CreateTask("Original");
        await _repository.SaveAsync("Task/TASK-1 - Taken.md", "# a plain note already there");

        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.UpdateTask(task.Id, title: "Taken"));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task CompleteTask_WhenCompletedDestinationIsTakenByAFile_AppendsSuffixRatherThanThrowing()
    {
        var task = await _tools.CreateTask("Complete me");
        await _repository.SaveAsync("Task/Completed/TASK-1 - Complete me.md", "# squatting on the completed path");

        var completed = await _tools.CompleteTask(task.Id);

        Assert.Equal("Task/Completed/TASK-1 - Complete me (2).md", completed.Path);
    }

    [Fact]
    public async Task ArchiveTask_DeprecatedAlias_WhenCompletedDestinationIsTaken_AppendsSuffix()
    {
        var task = await _tools.CreateTask("Archive me");
        await _repository.SaveAsync("Task/Completed/TASK-1 - Archive me.md", "# squatting on the completed path");

        var archived = await _tools.ArchiveTask(task.Id);

        Assert.Equal("Task/Completed/TASK-1 - Archive me (2).md", archived.Path);
    }

    [Fact]
    public async Task Tools_WhenVaultHasVanished_ThrowMcpExceptionNotRawExceptions()
    {
        var task = await _tools.CreateTask("Doomed");
        var other = await _tools.CreateTask("Other");
        _vaultDirectory.Delete(recursive: true);

        await Assert.ThrowsAsync<McpException>(() => _tools.UpdateTask(task.Id, priority: "high"));
        await Assert.ThrowsAsync<McpException>(() => _tools.ArchiveTask(task.Id));
        await Assert.ThrowsAsync<McpException>(() => _tools.MoveTask(other.Id, "Done"));
        await Assert.ThrowsAsync<McpException>(() => _tools.CreateTask("After the crash"));
    }

    [Fact]
    public async Task MoveTask_WithBeforeId_InsertsBeforeIt()
    {
        var a = await _tools.CreateTask("A", status: "To Do");
        var b = await _tools.CreateTask("B", status: "To Do");
        var c = await _tools.CreateTask("C", status: "To Do");

        await _tools.MoveTask(c.Id, "To Do", beforeId: b.Id);

        var ids = _tools.ListTasks(status: "To Do").Select(t => t.Id).ToArray();
        Assert.Equal(new[] { a.Id, c.Id, b.Id }, ids);
    }

    [Fact]
    public async Task MoveTask_BeforeIdInAnotherColumn_IsAnMcpException()
    {
        var done = await _tools.CreateTask("Done one", status: "Done");
        var todo = await _tools.CreateTask("Todo one");

        await Assert.ThrowsAsync<McpException>(() => _tools.MoveTask(todo.Id, "To Do", beforeId: done.Id));
    }
}
