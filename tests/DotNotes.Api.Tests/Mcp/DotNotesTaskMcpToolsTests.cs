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
/// Unit tests for <see cref="DotNotesTaskMcpTools"/> and
/// <see cref="DotNotesTaskWorkflowResource"/>, exercised directly (no MCP
/// wire transport involved) against a real <see cref="FileSystemNoteRepository"/>/
/// <see cref="InMemoryTaskIndex"/>/<see cref="TaskService"/>/
/// <see cref="VaultReorganizationService"/> over an isolated temp vault -
/// same convention as <see cref="DotNotesMcpToolsTests"/> and
/// <c>TaskServiceTests</c>.
/// </summary>
public sealed class DotNotesTaskMcpToolsTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _repository;
    private readonly InMemoryTaskIndex _taskIndex;
    private readonly InMemoryLinkIndex _linkIndex;
    private readonly VaultReorganizationService _reorganizationService;
    private readonly TaskService _taskService;
    private readonly DotNotesTaskMcpTools _tools;

    public DotNotesTaskMcpToolsTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-task-mcp-tools-tests-");
        var vaultOptions = Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName });
        _repository = new FileSystemNoteRepository(vaultOptions);
        var tasksOptions = Options.Create(new TasksOptions());
        _taskIndex = new InMemoryTaskIndex(_repository, tasksOptions);
        _linkIndex = new InMemoryLinkIndex(_repository);
        _reorganizationService = new VaultReorganizationService(
            _repository,
            new IVaultChangeListener[] { _taskIndex, _linkIndex },
            NullLogger<VaultReorganizationService>.Instance);
        _taskService = new TaskService(_repository, _taskIndex, _reorganizationService, tasksOptions);
        _tools = new DotNotesTaskMcpTools(_taskService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultDirectory.FullName))
        {
            _vaultDirectory.Delete(recursive: true);
        }
    }

    // ---- create_task ----

    [Fact]
    public async Task CreateTask_MinimalRequest_UsesDefaultsAndReturnsFullDto()
    {
        var task = await _tools.CreateTask("My First Task");

        Assert.Equal("TASK-1", task.Id);
        Assert.Equal("My First Task", task.Title);
        Assert.Equal("Backlog", task.Status);
        Assert.Equal(string.Empty, task.Description);
        Assert.Empty(task.AcceptanceCriteria);
        Assert.Equal(0, task.AcTotal);
        Assert.Equal(0, task.AcChecked);
        Assert.False(task.Completed);
        Assert.Equal("Task/TASK-1 - My First Task.md", task.Path);
    }

    [Fact]
    public async Task CreateTask_FullRequest_RoundTripsEveryField()
    {
        var task = await _tools.CreateTask(
            title: "Fix login redirect",
            description: "Users land on / instead of the page they asked for.",
            status: "In Progress",
            priority: "high",
            assignee: ["@jonathan"],
            labels: ["auth"],
            milestone: "v0.3",
            dependencies: [],
            acceptanceCriteria: ["Redirect preserves the original path", "Covered by an integration test"]);

        Assert.Equal("Fix login redirect", task.Title);
        Assert.Equal("Users land on / instead of the page they asked for.", task.Description);
        Assert.Equal("In Progress", task.Status);
        Assert.Equal("high", task.Priority);
        Assert.Equal(["@jonathan"], task.Assignee);
        Assert.Equal(["auth"], task.Labels);
        Assert.Equal("v0.3", task.Milestone);
        Assert.Equal(2, task.AcTotal);
        Assert.Equal(0, task.AcChecked);
        Assert.Equal("Redirect preserves the original path", task.AcceptanceCriteria[0].Text);
        Assert.Equal(1, task.AcceptanceCriteria[0].Index);
    }

    [Fact]
    public async Task CreateTask_InvalidStatus_ThrowsMcpException()
    {
        await Assert.ThrowsAsync<McpException>(() => _tools.CreateTask("Bad status task", status: "Not A Status"));
    }

    // ---- get_task ----

    [Fact]
    public async Task GetTask_ExistingId_ReturnsFullDto()
    {
        var created = await _tools.CreateTask("Look me up");

        var fetched = _tools.GetTask(created.Id);

        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal("Look me up", fetched.Title);
    }

    [Fact]
    public void GetTask_UnknownId_ThrowsMcpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.GetTask("TASK-999"));
        Assert.Contains("TASK-999", ex.Message);
    }

    // ---- list_tasks ----

    [Fact]
    public async Task ListTasks_NoFilter_ReturnsAllNonArchivedSortedByBoardOrder()
    {
        await _tools.CreateTask("First", status: "To Do");
        await _tools.CreateTask("Second", status: "In Progress");
        await _tools.CreateTask("Third", status: "To Do");

        var tasks = _tools.ListTasks();

        Assert.Equal(3, tasks.Count);
        Assert.Equal(["First", "Third", "Second"], tasks.Select(t => t.Title).ToArray());
    }

    [Fact]
    public async Task ListTasks_FilterByStatus_ReturnsOnlyMatching()
    {
        await _tools.CreateTask("First", status: "To Do");
        await _tools.CreateTask("Second", status: "In Progress");

        var tasks = _tools.ListTasks(status: "In Progress");

        var task = Assert.Single(tasks);
        Assert.Equal("Second", task.Title);
    }

    [Fact]
    public async Task ListTasks_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _tools.CreateTask($"Task {i}");
        }

        var tasks = _tools.ListTasks(limit: 2);

        Assert.Equal(2, tasks.Count);
    }

    [Fact]
    public async Task ListTasks_ExcludesCompletedUnlessRequested()
    {
        var task = await _tools.CreateTask("To be completed");
        await _tools.CompleteTask(task.Id);

        Assert.Empty(_tools.ListTasks());
        Assert.Single(_tools.ListTasks(includeCompleted: true));
    }

    [Fact]
    public async Task ListTasks_IncludeArchivedAlias_StillIncludesCompleted()
    {
        var task = await _tools.CreateTask("To be completed via alias check");
        await _tools.CompleteTask(task.Id);

        Assert.Single(_tools.ListTasks(includeArchived: true));
    }

    [Fact]
    public async Task CreateTask_WithFolder_CreatesUnderThatFolder()
    {
        var task = await _tools.CreateTask("In a subfolder", folder: "Projects/Alpha");

        Assert.Equal("Projects/Alpha/TASK-1 - In a subfolder.md", task.Path);
    }

    // ---- update_task ----

    [Fact]
    public async Task UpdateTask_ScalarFields_OnlySuppliedFieldsChange()
    {
        var task = await _tools.CreateTask("Original title", priority: "low");

        var updated = await _tools.UpdateTask(task.Id, priority: "high");

        Assert.Equal("Original title", updated.Title);
        Assert.Equal("high", updated.Priority);
    }

    [Fact]
    public async Task UpdateTask_EmptyStringClearsScalarField()
    {
        var task = await _tools.CreateTask("Has a milestone", milestone: "v0.3");

        var updated = await _tools.UpdateTask(task.Id, milestone: "");

        Assert.Null(updated.Milestone);
    }

    [Fact]
    public async Task UpdateTask_ArrayFieldReplacesWholeList()
    {
        var task = await _tools.CreateTask("Labelled", labels: ["one", "two"]);

        var updated = await _tools.UpdateTask(task.Id, labels: ["three"]);

        Assert.Equal(["three"], updated.Labels);
    }

    [Fact]
    public async Task UpdateTask_AcceptanceCriteriaAddCheckUncheckRemove_AppliesInOrder()
    {
        var task = await _tools.CreateTask("AC task", acceptanceCriteria: ["First"]);

        var afterAdd = await _tools.UpdateTask(task.Id, acceptanceCriteriaAdd: ["Second", "Third"]);
        Assert.Equal(3, afterAdd.AcTotal);

        var afterCheck = await _tools.UpdateTask(task.Id, acceptanceCriteriaCheck: [1, 2]);
        Assert.True(afterCheck.AcceptanceCriteria[0].Checked);
        Assert.True(afterCheck.AcceptanceCriteria[1].Checked);
        Assert.False(afterCheck.AcceptanceCriteria[2].Checked);
        Assert.Equal(2, afterCheck.AcChecked);

        var afterUncheck = await _tools.UpdateTask(task.Id, acceptanceCriteriaUncheck: [1]);
        Assert.False(afterUncheck.AcceptanceCriteria[0].Checked);
        Assert.True(afterUncheck.AcceptanceCriteria[1].Checked);

        var afterRemove = await _tools.UpdateTask(task.Id, acceptanceCriteriaRemove: [1]);
        Assert.Equal(2, afterRemove.AcTotal);
        Assert.Equal("Second", afterRemove.AcceptanceCriteria[0].Text);
        Assert.Equal(1, afterRemove.AcceptanceCriteria[0].Index);
    }

    [Fact]
    public async Task UpdateTask_PlanAppendAndNotesAppend_AppendToExistingText()
    {
        var task = await _tools.CreateTask("Plan task");

        var afterPlanSet = await _tools.UpdateTask(task.Id, planSet: "Step one.");
        Assert.Equal("Step one.", afterPlanSet.ImplementationPlan);

        var afterPlanAppend = await _tools.UpdateTask(task.Id, planAppend: "Step two.");
        Assert.Contains("Step one.", afterPlanAppend.ImplementationPlan);
        Assert.Contains("Step two.", afterPlanAppend.ImplementationPlan);

        var afterNotesAppend = await _tools.UpdateTask(task.Id, notesAppend: "Did the thing.");
        Assert.Contains("Did the thing.", afterNotesAppend.ImplementationNotes);

        var afterMoreNotes = await _tools.UpdateTask(task.Id, notesAppend: "Did another thing.");
        Assert.Contains("Did the thing.", afterMoreNotes.ImplementationNotes);
        Assert.Contains("Did another thing.", afterMoreNotes.ImplementationNotes);
    }

    [Fact]
    public async Task UpdateTask_FinalSummary_SetsIt()
    {
        var task = await _tools.CreateTask("Summarized task");

        var updated = await _tools.UpdateTask(task.Id, finalSummary: "Shipped the fix.");

        Assert.Equal("Shipped the fix.", updated.FinalSummary);
    }

    [Fact]
    public async Task UpdateTask_UnknownId_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.UpdateTask("TASK-999", title: "x"));
        Assert.Contains("TASK-999", ex.Message);
    }

    [Fact]
    public async Task UpdateTask_InvalidStatus_ThrowsMcpException()
    {
        var task = await _tools.CreateTask("Bad status update");

        await Assert.ThrowsAsync<McpException>(() => _tools.UpdateTask(task.Id, status: "Not A Status"));
    }

    // ---- move_task ----

    [Fact]
    public async Task MoveTask_ChangesStatus()
    {
        var task = await _tools.CreateTask("Movable");

        var moved = await _tools.MoveTask(task.Id, "Done");

        Assert.Equal("Done", moved.Status);
    }

    [Fact]
    public async Task MoveTask_WithIndex_OrdersWithinColumn()
    {
        var first = await _tools.CreateTask("First", status: "Done");
        var second = await _tools.CreateTask("Second", status: "Done");
        var third = await _tools.CreateTask("Third");

        await _tools.MoveTask(third.Id, "Done", index: 0);

        var tasks = _tools.ListTasks(status: "Done");
        Assert.Equal(["Third", "First", "Second"], tasks.Select(t => t.Title).ToArray());
    }

    [Fact]
    public async Task MoveTask_UnknownId_ThrowsMcpException()
    {
        await Assert.ThrowsAsync<McpException>(() => _tools.MoveTask("TASK-999", "Done"));
    }

    [Fact]
    public async Task MoveTask_InvalidStatus_ThrowsMcpException()
    {
        var task = await _tools.CreateTask("Move to bad status");

        await Assert.ThrowsAsync<McpException>(() => _tools.MoveTask(task.Id, "Not A Status"));
    }

    /// <summary>
    /// Regression test for finding #5, exercised through the MCP surface:
    /// moving a task to its own current unrecognised raw status must
    /// succeed as a Backlog-column reorder (honouring beforeId) rather than
    /// fail to find any destination column, since the raw status value
    /// itself is not a board column name.
    /// </summary>
    [Fact]
    public async Task MoveTask_ToOwnUnrecognisedRawStatus_SucceedsAsBacklogReorder()
    {
        var content = "---\nid: TASK-1\nstatus: Blocked\n---\n";
        var writeResult = await _repository.SaveAsync("Task/TASK-1 - X.md", content);
        _taskIndex.NoteSaved(writeResult.Path, content, writeResult.UpdatedAt);

        var other = await _tools.CreateTask("Other"); // lands in Backlog

        var moved = await _tools.MoveTask("TASK-1", "Blocked", beforeId: other.Id);

        Assert.Equal("Blocked", moved.Status);
        var board = _tools.GetBoard();
        var backlog = board.Columns.Single(c => c.Status == "Backlog");
        Assert.Equal(new[] { "TASK-1", other.Id }, backlog.Tasks.Select(t => t.Id).ToArray());
    }

    // ---- complete_task / archive_task (deprecated alias) ----

    [Fact]
    public async Task CompleteTask_MovesUnderCompletedFolderAndMarksCompleted()
    {
        var task = await _tools.CreateTask("Complete me");

        var completed = await _tools.CompleteTask(task.Id);

        Assert.True(completed.Completed);
        Assert.Equal("Task/Completed/TASK-1 - Complete me.md", completed.Path);
        Assert.Equal("Done", completed.Status);
    }

    [Fact]
    public async Task CompleteTask_UnknownId_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.CompleteTask("TASK-999"));
        Assert.Contains("TASK-999", ex.Message);
    }

    [Fact]
    public async Task ArchiveTask_DeprecatedAlias_StillCompletesTheTask()
    {
        var task = await _tools.CreateTask("Archive alias");

        var archived = await _tools.ArchiveTask(task.Id);

        Assert.True(archived.Completed);
        Assert.Equal("Task/Completed/TASK-1 - Archive alias.md", archived.Path);
    }

    [Fact]
    public async Task ArchiveTask_UnknownId_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.ArchiveTask("TASK-999"));
        Assert.Contains("TASK-999", ex.Message);
    }

    // ---- get_board ----

    [Fact]
    public async Task GetBoard_ReturnsOneColumnPerConfiguredStatusInOrder()
    {
        await _tools.CreateTask("Todo task", status: "To Do");
        await _tools.CreateTask("Doing task", status: "In Progress");

        var board = _tools.GetBoard();

        Assert.Equal(["Backlog", "To Do", "In Progress", "Done"], board.Columns.Select(c => c.Status).ToArray());
        Assert.True(board.Columns[0].IsBacklog);
        Assert.All(board.Columns.Skip(1), c => Assert.False(c.IsBacklog));
        Assert.Empty(board.Columns[0].Tasks);
        Assert.Single(board.Columns[1].Tasks);
        Assert.Single(board.Columns[2].Tasks);
        Assert.Empty(board.Columns[3].Tasks);
    }

    [Fact]
    public async Task GetBoard_FiltersApply()
    {
        await _tools.CreateTask("High priority", priority: "high");
        await _tools.CreateTask("Low priority", priority: "low");

        var board = _tools.GetBoard(priority: "high");

        Assert.Single(board.Columns.SelectMany(c => c.Tasks));
    }

    // ---- search_tasks ----

    [Fact]
    public async Task SearchTasks_MatchesTitle()
    {
        await _tools.CreateTask("Fix login redirect");
        await _tools.CreateTask("Unrelated task");

        var results = _tools.SearchTasks("login");

        var result = Assert.Single(results);
        Assert.Equal("Fix login redirect", result.Title);
    }

    [Fact]
    public async Task SearchTasks_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _tools.CreateTask($"Shared keyword {i}");
        }

        var results = _tools.SearchTasks("keyword", limit: 2);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// Regression test for finding #4: search_tasks excludes completed
    /// tasks by default and only includes them when includeCompleted (or
    /// its deprecated includeArchived alias) is set.
    /// </summary>
    [Fact]
    public async Task SearchTasks_ExcludesCompletedUnlessIncludeCompletedIsSet()
    {
        var task = await _tools.CreateTask("Findable login fix");
        await _tools.CompleteTask(task.Id);

        Assert.Empty(_tools.SearchTasks("login"));
        Assert.Single(_tools.SearchTasks("login", includeCompleted: true));
        Assert.Single(_tools.SearchTasks("login", includeArchived: true));
    }

    // ---- get_task_workflow / resource ----

    [Fact]
    public void GetTaskWorkflow_ReturnsNonEmptyMarkdown()
    {
        var workflow = _tools.GetTaskWorkflow();

        Assert.False(string.IsNullOrWhiteSpace(workflow));
        Assert.Contains("#", workflow);
    }

    [Fact]
    public void TasksWorkflowResource_ReturnsSameTextAsTool()
    {
        var resource = new DotNotesTaskWorkflowResource();

        Assert.Equal(_tools.GetTaskWorkflow(), resource.TasksWorkflow());
    }
}
