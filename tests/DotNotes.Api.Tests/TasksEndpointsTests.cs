using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Integration tests for docs/04-API-SPEC.md's Tasks section
/// (docs/features/tasks-kanban/PLAN.md §4), exercised end-to-end through
/// a real ASP.NET Core test host (see <see cref="NotesApiFactory"/>).
/// Follows the same per-test isolated temp vault convention as
/// <c>NotesEndpointsTests</c>.
/// </summary>
public sealed class TasksEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public TasksEndpointsTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-tasks-api-tests-").FullName;
        _factory = new NotesApiFactory(_vaultRootPath);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Vault__RootPath", null);

        if (Directory.Exists(_vaultRootPath))
        {
            Directory.Delete(_vaultRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetConfig_ReturnsConfiguredValuesWithResolvedDefaultStatus()
    {
        var response = await _client.GetAsync("/api/tasks/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = await response.Content.ReadFromJsonAsync<TasksConfigDto>(ResponseJsonOptions);
        Assert.NotNull(config);
        Assert.Equal("Task", config!.Folder);
        Assert.Equal("TASK", config.IdPrefix);
        Assert.Equal(new[] { "Backlog", "To Do", "In Progress", "Done" }, config.Statuses);
        Assert.Equal("Backlog", config.DefaultStatus);
        Assert.Equal(new[] { "high", "medium", "low" }, config.Priorities);
        Assert.Equal("Backlog", config.BacklogStatus);
        Assert.Equal("Done", config.CompletedStatus);
        Assert.Equal("Completed", config.CompletedFolder);
    }

    [Fact]
    public async Task CreateTask_HappyPath_CreatesFileWithExpectedNameAndFrontmatter()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { title = "Fix login redirect" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/tasks/TASK-1", response.Headers.Location?.OriginalString);

        var task = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.NotNull(task);
        Assert.Equal("TASK-1", task!.Id);
        Assert.Equal("Fix login redirect", task.Title);
        Assert.Equal("Backlog", task.Status);
        Assert.Equal("Task/TASK-1 - Fix login redirect.md", task.Path);
        Assert.False(task.Completed);
        Assert.Equal(0, task.AcTotal);

        var fullPath = Path.Combine(_vaultRootPath, "Task", "TASK-1 - Fix login redirect.md");
        Assert.True(File.Exists(fullPath));
        var content = await File.ReadAllTextAsync(fullPath);
        Assert.Contains("id: TASK-1", content);
        Assert.Contains("status: Backlog", content);
    }

    [Fact]
    public async Task CreateTask_MissingTitle_ReturnsInvalidRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { status = "To Do" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task CreateTask_UnknownStatus_ReturnsInvalidRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { title = "Something", status = "Nope" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task GetTask_UnknownId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/tasks/TASK-999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.Equal("not_found", error!.Error);
    }

    [Fact]
    public async Task GetTask_ById_ReturnsFullTaskShape()
    {
        var created = await CreateTaskAsync("Write docs", description: "Some description");

        var response = await _client.GetAsync($"/api/tasks/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.NotNull(task);
        Assert.Equal("Some description", task!.Description);
        Assert.NotNull(task.AcceptanceCriteria);
    }

    [Fact]
    public async Task PatchTask_TitleChange_RenamesFileAndRewritesWikilink()
    {
        var created = await CreateTaskAsync("Original Title");
        await WriteNoteToDiskAsync("notes/reference.md", $"See [[Task/{created.Id} - Original Title]] for context.");

        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}", new { title = "Renamed Title" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.NotNull(updated);
        Assert.Equal("Renamed Title", updated!.Title);
        Assert.Equal($"Task/{created.Id} - Renamed Title.md", updated.Path);

        var oldFullPath = Path.Combine(_vaultRootPath, "Task", $"{created.Id} - Original Title.md");
        var newFullPath = Path.Combine(_vaultRootPath, "Task", $"{created.Id} - Renamed Title.md");
        Assert.False(File.Exists(oldFullPath));
        Assert.True(File.Exists(newFullPath));

        var referenceContent = await File.ReadAllTextAsync(Path.Combine(_vaultRootPath, "notes", "reference.md"));
        Assert.Contains($"[[Task/{created.Id} - Renamed Title", referenceContent);
    }

    [Fact]
    public async Task PatchTask_ClearPriorityWithEmptyString_ClearsField()
    {
        var created = await CreateTaskAsync("Has priority", priority: "high");

        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}", new { priority = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.Null(updated!.Priority);
    }

    [Fact]
    public async Task PatchTask_UnknownId_ReturnsNotFound()
    {
        var response = await _client.PatchAsJsonAsync("/api/tasks/TASK-999", new { title = "Whatever" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.Equal("not_found", error!.Error);
    }

    [Fact]
    public async Task MoveTask_BetweenColumns_PersistsStatusAndOrdinalReflectedOnBoard()
    {
        var created = await CreateTaskAsync("Move me");

        var response = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/move", new { status = "In Progress" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.Equal("In Progress", moved!.Status);

        var board = await GetBoardAsync();
        var inProgress = board.Columns.Single(c => c.Status == "In Progress");
        Assert.Contains(inProgress.Tasks, t => t.Id == created.Id);
        var toDo = board.Columns.Single(c => c.Status == "To Do");
        Assert.DoesNotContain(toDo.Tasks, t => t.Id == created.Id);
    }

    [Fact]
    public async Task MoveTask_WithinColumn_PersistsOrdinalOrderOnBoard()
    {
        var first = await CreateTaskAsync("First", status: "To Do");
        var second = await CreateTaskAsync("Second", status: "To Do");
        var third = await CreateTaskAsync("Third", status: "To Do");

        // Move "Third" to index 0 (top of the "To Do" column).
        var response = await _client.PostAsJsonAsync($"/api/tasks/{third.Id}/move", new { status = "To Do", index = 0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var board = await GetBoardAsync();
        var toDo = board.Columns.Single(c => c.Status == "To Do");
        var order = toDo.Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { third.Id, first.Id, second.Id }, order);
    }

    [Fact]
    public async Task MoveTask_NegativeIndex_ReturnsInvalidRequest()
    {
        var created = await CreateTaskAsync("Task");

        var response = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/move", new { status = "To Do", index = -1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task MoveTask_MissingStatus_ReturnsInvalidRequest()
    {
        var created = await CreateTaskAsync("Task");

        var response = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/move", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task CompleteTask_MovesUnderCompletedFolderAndDisappearsFromBoardUnlessIncluded()
    {
        var created = await CreateTaskAsync("Complete me");

        var response = await _client.PostAsync($"/api/tasks/{created.Id}/complete", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completed = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.True(completed!.Completed);
        Assert.Equal("Task/Completed/TASK-1 - Complete me.md", completed.Path);
        Assert.Equal("Done", completed.Status);

        var boardWithoutCompleted = await GetBoardAsync();
        Assert.All(boardWithoutCompleted.Columns, c => Assert.DoesNotContain(c.Tasks, t => t.Id == created.Id));

        var listResponse = await _client.GetAsync("/api/tasks?includeCompleted=true");
        var list = await listResponse.Content.ReadFromJsonAsync<TaskListDto>(ResponseJsonOptions);
        Assert.Contains(list!.Tasks, t => t.Id == created.Id && t.Completed);
    }

    [Fact]
    public async Task ArchiveAlias_StillCompletesTheTask()
    {
        var created = await CreateTaskAsync("Archive alias");

        var response = await _client.PostAsync($"/api/tasks/{created.Id}/archive", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completed = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.True(completed!.Completed);
        Assert.Equal("Task/Completed/TASK-1 - Archive alias.md", completed.Path);
    }

    [Fact]
    public async Task CompleteTask_NameCollision_AppendsNumericSuffix()
    {
        var created = await CreateTaskAsync("Dup");
        Directory.CreateDirectory(Path.Combine(_vaultRootPath, "Task", "Completed"));
        await File.WriteAllTextAsync(
            Path.Combine(_vaultRootPath, "Task", "Completed", $"{created.Id} - Dup.md"),
            "# already there");

        var response = await _client.PostAsync($"/api/tasks/{created.Id}/complete", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completed = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.Equal("Task/Completed/TASK-1 - Dup (2).md", completed!.Path);
    }

    [Fact]
    public async Task CreateTask_WithFolder_CreatesThereAndShowsOnBoard()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { title = "Alpha task", folder = "Projects/Alpha" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.NotNull(task);
        Assert.Equal("Projects/Alpha/TASK-1 - Alpha task.md", task!.Path);
        Assert.True(File.Exists(Path.Combine(_vaultRootPath, "Projects", "Alpha", "TASK-1 - Alpha task.md")));

        var board = await GetBoardAsync();
        Assert.Contains(board.Columns.SelectMany(c => c.Tasks), t => t.Id == task.Id);
    }

    [Fact]
    public async Task Task_UnderLowercaseCompletedFolder_IsNotConsideredCompleted()
    {
        await WriteNoteToDiskAsync(
            "Task/completed/TASK-42 - Lowercase.md",
            "---\nid: TASK-42\ntitle: Lowercase\nstatus: Backlog\n---\n\nBody.\n");

        var task = await GetTaskUntilAsync("TASK-42", t => t is not null);

        Assert.NotNull(task);
        Assert.False(task!.Completed);

        var board = await GetBoardAsync();
        Assert.Contains(board.Columns.SelectMany(c => c.Tasks), t => t.Id == "TASK-42");
    }

    [Fact]
    public async Task Board_BacklogColumnIsFirstAndCollectsUnknownAndEmptyStatuses()
    {
        await WriteNoteToDiskAsync(
            "Task/TASK-50 - No status.md",
            "---\nid: TASK-50\ntitle: No status\nstatus:\n---\n\nBody.\n");
        await WriteNoteToDiskAsync(
            "Task/TASK-51 - Unknown status.md",
            "---\nid: TASK-51\ntitle: Unknown status\nstatus: Blocked\n---\n\nBody.\n");
        await GetTaskUntilAsync("TASK-51", t => t is not null);

        var board = await GetBoardAsync();

        Assert.Equal("Backlog", board.Columns[0].Status);
        Assert.True(board.Columns[0].IsBacklog);
        Assert.All(board.Columns.Skip(1), c => Assert.False(c.IsBacklog));
        var backlogIds = board.Columns[0].Tasks.Select(t => t.Id).ToArray();
        Assert.Contains("TASK-50", backlogIds);
        Assert.Contains("TASK-51", backlogIds);
    }

    [Fact]
    public async Task MoveTask_BacklogToToDoAndBackToBacklog_Persists()
    {
        var created = await CreateTaskAsync("Round trip");

        var toToDo = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/move", new { status = "To Do" });
        Assert.Equal(HttpStatusCode.OK, toToDo.StatusCode);
        var afterToDo = await toToDo.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.Equal("To Do", afterToDo!.Status);

        var backToBacklog = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/move", new { status = "Backlog" });
        Assert.Equal(HttpStatusCode.OK, backToBacklog.StatusCode);
        var afterBacklog = await backToBacklog.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.Equal("Backlog", afterBacklog!.Status);

        var board = await GetBoardAsync();
        Assert.Contains(board.Columns[0].Tasks, t => t.Id == created.Id);
    }

    [Fact]
    public async Task ConvertNote_PlainNote_BecomesTaskAndRenames()
    {
        await WriteNoteToDiskAsync("ideas/cool idea.md", "This is a great idea for later.");

        var response = await _client.PostAsJsonAsync("/api/tasks/convert", new { path = "ideas/cool idea.md", status = "in progress" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        Assert.NotNull(task);
        Assert.Equal("In Progress", task!.Status);
        Assert.Equal("TASK-1", task.Id);
        Assert.StartsWith("ideas/", task.Path);
        Assert.Contains("This is a great idea for later.", task.Description);

        Assert.False(File.Exists(Path.Combine(_vaultRootPath, "ideas", "cool idea.md")));
    }

    [Fact]
    public async Task ConvertNote_MissingPath_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks/convert", new { path = "does/not/exist.md" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.Equal("not_found", error!.Error);
    }

    [Fact]
    public async Task ConvertNote_AlreadyATask_ReturnsInvalidRequest()
    {
        var created = await CreateTaskAsync("Already a task");

        var response = await _client.PostAsJsonAsync("/api/tasks/convert", new { path = created.Path });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task NoteWrittenDirectlyToDisk_WithTaskFrontmatter_ShowsUpViaWatcher()
    {
        var content = """
            ---
            id: TASK-42
            title: Written directly
            status: To Do
            ---

            Body text.
            """;
        await WriteNoteToDiskAsync("tasks/TASK-42 - Written directly.md", content);

        var task = await GetTaskUntilAsync("TASK-42", t => t is not null);

        Assert.NotNull(task);
        Assert.Equal("Written directly", task!.Title);
    }

    [Fact]
    public async Task PlainNotes_NeverAppearInTaskList()
    {
        await WriteNoteToDiskAsync("plain/note.md", "Just a regular note.");
        await CreateTaskAsync("A real task");

        // Give the watcher a moment to have processed the plain note too
        // (it must NOT show up as a task even after settling).
        await Task.Delay(300);

        var response = await _client.GetAsync("/api/tasks?includeCompleted=true");
        var list = await response.Content.ReadFromJsonAsync<TaskListDto>(ResponseJsonOptions);
        Assert.NotNull(list);
        Assert.DoesNotContain(list!.Tasks, t => t.Path == "plain/note.md");
    }

    [Fact]
    public async Task Revision_IncreasesAfterMutation()
    {
        var before = await GetRevisionAsync();
        await CreateTaskAsync("Bump revision");
        var after = await GetRevisionAsync();

        Assert.True(after > before);
    }

    [Fact]
    public async Task PutNote_StaleExpectedUpdatedAt_Returns409WithCurrentUpdatedAt()
    {
        var putResponse = await _client.PutAsJsonAsync("/api/notes/idea.md", new { content = "v1" });
        var written = await putResponse.Content.ReadFromJsonAsync<NoteWriteDto>(ResponseJsonOptions);

        // Overwrite again so the on-disk timestamp moves past what the
        // "editor" below still thinks is current.
        await Task.Delay(10);
        await _client.PutAsJsonAsync("/api/notes/idea.md", new { content = "v2" });

        var response = await _client.PutAsJsonAsync(
            "/api/notes/idea.md",
            new { content = "v3-from-stale-editor", expectedUpdatedAt = written!.UpdatedAt });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ConflictErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("conflict", error!.Error);
        Assert.NotNull(error.CurrentUpdatedAt);
    }

    [Fact]
    public async Task PutNote_FreshExpectedUpdatedAt_Returns200()
    {
        var putResponse = await _client.PutAsJsonAsync("/api/notes/idea.md", new { content = "v1" });
        var written = await putResponse.Content.ReadFromJsonAsync<NoteWriteDto>(ResponseJsonOptions);

        var response = await _client.PutAsJsonAsync(
            "/api/notes/idea.md",
            new { content = "v2", expectedUpdatedAt = written!.UpdatedAt });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PutNote_NoExpectedUpdatedAt_LastWriteWinsUnchanged()
    {
        await _client.PutAsJsonAsync("/api/notes/idea.md", new { content = "v1" });
        await _client.PutAsJsonAsync("/api/notes/idea.md", new { content = "v2" });

        var response = await _client.PutAsJsonAsync("/api/notes/idea.md", new { content = "v3" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fullPath = Path.Combine(_vaultRootPath, "idea.md");
        Assert.Equal("v3", await File.ReadAllTextAsync(fullPath));
    }

    private async Task<TaskDto> CreateTaskAsync(string title, string? status = null, string? priority = null, string? description = null)
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { title, status, priority, description });
        response.EnsureSuccessStatusCode();
        var task = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
        return task ?? throw new InvalidOperationException("Task creation returned no body.");
    }

    private async Task<TaskBoardDto> GetBoardAsync()
    {
        var response = await _client.GetAsync("/api/tasks/board");
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<TaskBoardDto>(ResponseJsonOptions);
        return board ?? throw new InvalidOperationException("Board fetch returned no body.");
    }

    private async Task<long> GetRevisionAsync()
    {
        var response = await _client.GetAsync("/api/tasks/revision");
        response.EnsureSuccessStatusCode();
        var revision = await response.Content.ReadFromJsonAsync<RevisionDto>(ResponseJsonOptions);
        return revision!.Revision;
    }

    private async Task<TaskDto?> GetTaskUntilAsync(string id, Func<TaskDto?, bool> isReady)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        TaskDto? task = null;

        while (DateTime.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/tasks/{id}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                task = await response.Content.ReadFromJsonAsync<TaskDto>(ResponseJsonOptions);
                if (isReady(task))
                {
                    return task;
                }
            }

            await System.Threading.Tasks.Task.Delay(100);
        }

        return task;
    }

    private async Task WriteNoteToDiskAsync(string relativePath, string content)
    {
        var fullPath = Path.Combine(_vaultRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, content);
    }

    private sealed record TasksConfigDto(
        string Folder,
        string IdPrefix,
        List<string> Statuses,
        string? DefaultStatus,
        List<string> Priorities,
        string BacklogStatus,
        string CompletedStatus,
        string CompletedFolder);

    private sealed record RevisionDto(long Revision);

    private sealed record TaskListDto(long Revision, List<TaskSummaryDto> Tasks);

    private sealed record TaskBoardDto(long Revision, List<TaskColumnDto> Columns);

    private sealed record TaskColumnDto(string Status, List<TaskSummaryDto> Tasks, bool IsBacklog);

    private record TaskSummaryDto(
        string Id,
        string Title,
        string Status,
        List<string> Assignee,
        List<string> Labels,
        string? Priority,
        string? Milestone,
        List<string> Dependencies,
        DateTimeOffset? CreatedDate,
        DateTimeOffset? UpdatedDate,
        double? Ordinal,
        string Path,
        bool Completed,
        string Excerpt,
        int AcTotal,
        int AcChecked);

    private sealed record TaskDto(
        string Id,
        string Title,
        string Status,
        List<string> Assignee,
        List<string> Labels,
        string? Priority,
        string? Milestone,
        List<string> Dependencies,
        DateTimeOffset? CreatedDate,
        DateTimeOffset? UpdatedDate,
        double? Ordinal,
        string Path,
        bool Completed,
        string Excerpt,
        int AcTotal,
        int AcChecked,
        string Description,
        List<AcceptanceCriterionDto> AcceptanceCriteria,
        string? ImplementationPlan,
        string? ImplementationNotes,
        string? FinalSummary,
        DateTimeOffset UpdatedAt);

    private sealed record AcceptanceCriterionDto(int Index, string Text, bool Checked);

    private sealed record ErrorDto(string Error, string? Detail);

    private sealed record NoteWriteDto(string Path, DateTimeOffset UpdatedAt);

    private sealed record ConflictErrorDto(string Error, string? Detail, DateTimeOffset? CurrentUpdatedAt);
}
