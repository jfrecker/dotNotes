using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotNotes.Api.Tests;

/// <summary>
/// Phase 12 QA: (1) Tasks:Statuses / Tasks:Priorities supplied through
/// configuration (environment variables, exactly as a Docker user would)
/// must REPLACE the built-in defaults rather than be appended to them, and
/// (2) the task tools and workflow resource must be reachable over the real
/// MCP Streamable-HTTP wire protocol at <c>/mcp</c>, not just callable as C#
/// methods.
/// </summary>
public sealed class TasksConfigurationAndMcpWireTests : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] EnvKeys =
    {
        "Tasks__Statuses__0", "Tasks__Statuses__1", "Tasks__Statuses__2", "Tasks__Statuses__3",
        "Tasks__Priorities__0", "Tasks__Priorities__1",
        "Tasks__DefaultStatus", "Tasks__IdPrefix", "Tasks__Folder",
    };

    private readonly string _vaultRootPath;
    private NotesApiFactory? _factory;

    public TasksConfigurationAndMcpWireTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-tasks-config-tests-").FullName;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        Environment.SetEnvironmentVariable("Vault__RootPath", null);
        foreach (var key in EnvKeys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        if (Directory.Exists(_vaultRootPath))
        {
            Directory.Delete(_vaultRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnvConfiguredStatusesAndPriorities_ReplaceDefaults_InBoardConfigAndValidation()
    {
        Environment.SetEnvironmentVariable("Tasks__Statuses__0", "Backlog");
        Environment.SetEnvironmentVariable("Tasks__Statuses__1", "Doing");
        Environment.SetEnvironmentVariable("Tasks__Statuses__2", "Review");
        Environment.SetEnvironmentVariable("Tasks__Statuses__3", "Done");
        Environment.SetEnvironmentVariable("Tasks__Priorities__0", "urgent");
        Environment.SetEnvironmentVariable("Tasks__Priorities__1", "normal");
        _factory = new NotesApiFactory(_vaultRootPath);
        using var client = _factory.CreateClient();

        // /api/tasks/config reflects exactly the configured lists (no leaked defaults).
        var config = await client.GetFromJsonAsync<JsonElement>("/api/tasks/config", ResponseJsonOptions);
        Assert.Equal(new[] { "Backlog", "Doing", "Review", "Done" },
            config.GetProperty("statuses").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("Backlog", config.GetProperty("defaultStatus").GetString());
        Assert.Equal(new[] { "urgent", "normal" },
            config.GetProperty("priorities").EnumerateArray().Select(e => e.GetString()).ToArray());

        // The board has exactly those four columns, in order, even when empty.
        var board = await client.GetFromJsonAsync<JsonElement>("/api/tasks/board", ResponseJsonOptions);
        Assert.Equal(new[] { "Backlog", "Doing", "Review", "Done" },
            board.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("status").GetString()).ToArray());

        // A new task with no status lands in the first configured column.
        var created = await client.PostAsJsonAsync("/api/tasks", new { title = "Configured", priority = "URGENT" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var task = await created.Content.ReadFromJsonAsync<JsonElement>(ResponseJsonOptions);
        Assert.Equal("Backlog", task.GetProperty("status").GetString());
        Assert.Equal("urgent", task.GetProperty("priority").GetString());

        // The built-in defaults are no longer valid.
        var oldStatus = await client.PostAsJsonAsync("/api/tasks", new { title = "Nope", status = "To Do" });
        Assert.Equal(HttpStatusCode.BadRequest, oldStatus.StatusCode);
        var oldPriority = await client.PostAsJsonAsync("/api/tasks", new { title = "Nope", priority = "high" });
        Assert.Equal(HttpStatusCode.BadRequest, oldPriority.StatusCode);

        // And a move into a configured, previously-empty column works.
        var moved = await client.PostAsJsonAsync($"/api/tasks/{task.GetProperty("id").GetString()}/move", new { status = "review" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var afterMove = await client.GetFromJsonAsync<JsonElement>("/api/tasks/board", ResponseJsonOptions);
        var review = afterMove.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("status").GetString() == "Review");
        Assert.Single(review.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task EnvConfiguredIdPrefixAndFolder_AreUsedForNewTasks()
    {
        Environment.SetEnvironmentVariable("Tasks__IdPrefix", "PROJ");
        Environment.SetEnvironmentVariable("Tasks__Folder", "work/items");
        _factory = new NotesApiFactory(_vaultRootPath);
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/tasks", new { title = "Prefixed" });
        var task = await response.Content.ReadFromJsonAsync<JsonElement>(ResponseJsonOptions);

        Assert.Equal("PROJ-1", task.GetProperty("id").GetString());
        Assert.Equal("work/items/PROJ-1 - Prefixed.md", task.GetProperty("path").GetString());
        Assert.True(File.Exists(Path.Combine(_vaultRootPath, "work", "items", "PROJ-1 - Prefixed.md")));

        // Defaults for the lists are untouched when only other keys are configured.
        var config = await client.GetFromJsonAsync<JsonElement>("/api/tasks/config", ResponseJsonOptions);
        Assert.Equal(3, config.GetProperty("statuses").GetArrayLength());
    }

    [Fact]
    public async Task McpOverHttp_ListsAllTaskToolsAndWorkflowResource_AndKeepsExistingTools()
    {
        _factory = new NotesApiFactory(_vaultRootPath);
        await using var client = await ConnectMcpAsync(_factory);

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();

        var taskTools = new[]
        {
            "list_tasks", "get_task", "create_task", "update_task", "move_task",
            "archive_task", "get_board", "search_tasks", "get_task_workflow",
        };
        foreach (var name in taskTools)
        {
            Assert.Contains(name, tools);
        }

        // Existing (Phase 6) tools must be unchanged.
        var existing = new[]
        {
            "search_notes", "get_note", "create_note", "update_note", "create_folder",
            "move_note", "move_folder", "get_backlinks", "get_recent_notes", "get_config",
        };
        foreach (var name in existing)
        {
            Assert.Contains(name, tools);
        }

        Assert.Equal(taskTools.Length + existing.Length, tools.Count);

        var resources = await client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == "dotnotes://workflow/tasks");

        var read = await client.ReadResourceAsync("dotnotes://workflow/tasks");
        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Contains("task", text.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(text.Text));
    }

    [Fact]
    public async Task McpOverHttp_CreateUpdateMoveTask_IsVisibleThroughRestAndOnDisk()
    {
        _factory = new NotesApiFactory(_vaultRootPath);
        await using var mcp = await ConnectMcpAsync(_factory);
        using var rest = _factory.CreateClient();

        var created = await mcp.CallToolAsync("create_task", new Dictionary<string, object?>
        {
            ["title"] = "Made over MCP",
            ["description"] = "From an AI assistant",
            ["acceptanceCriteria"] = new[] { "works", "is tested" },
        });
        Assert.NotEqual(true, created.IsError);

        await mcp.CallToolAsync("update_task", new Dictionary<string, object?>
        {
            ["id"] = "TASK-1",
            ["acceptanceCriteriaCheck"] = new[] { 1 },
            ["notesAppend"] = "progress note",
        });
        await mcp.CallToolAsync("move_task", new Dictionary<string, object?> { ["id"] = "TASK-1", ["status"] = "In Progress" });

        var viaRest = await rest.GetFromJsonAsync<JsonElement>("/api/tasks/TASK-1", ResponseJsonOptions);
        Assert.Equal("In Progress", viaRest.GetProperty("status").GetString());
        Assert.Equal(1, viaRest.GetProperty("acChecked").GetInt32());
        Assert.Equal("progress note", viaRest.GetProperty("implementationNotes").GetString());

        var onDisk = await File.ReadAllTextAsync(Path.Combine(_vaultRootPath, "tasks", "TASK-1 - Made over MCP.md"));
        Assert.Contains("status: In Progress", onDisk);
        Assert.Contains("- [x] #1 works", onDisk);

        // A bad status surfaces as a tool error, not a transport failure.
        var bad = await mcp.CallToolAsync("move_task", new Dictionary<string, object?> { ["id"] = "TASK-1", ["status"] = "Nowhere" });
        Assert.True(bad.IsError);
    }

    private static async Task<McpClient> ConnectMcpAsync(NotesApiFactory factory)
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            http,
            loggerFactory: null,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }
}
