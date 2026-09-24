using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Phase 12 QA gap-fill: failure cases and query filters that
/// <see cref="TasksEndpointsTests"/> does not exercise (per endpoint: at
/// least one failure case), against a real temp vault.
/// </summary>
public sealed class TasksEndpointsGapTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public TasksEndpointsGapTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-tasks-gap-tests-").FullName;
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

    private async Task<JsonElement> CreateAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(error, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PatchTask_InvalidStatusOrPriorityOrEmptyTitle_Return400_AndFileUnchanged()
    {
        var task = await CreateAsync(new { title = "Stay put", priority = "high" });
        var path = Path.Combine(_vaultRootPath, task.GetProperty("path").GetString()!);
        var before = await File.ReadAllTextAsync(path);

        await AssertErrorAsync(await _client.PatchAsJsonAsync("/api/tasks/TASK-1", new { status = "Nowhere" }), HttpStatusCode.BadRequest, "invalid_request");
        await AssertErrorAsync(await _client.PatchAsJsonAsync("/api/tasks/TASK-1", new { priority = "urgent" }), HttpStatusCode.BadRequest, "invalid_request");
        await AssertErrorAsync(await _client.PatchAsJsonAsync("/api/tasks/TASK-1", new { title = "   " }), HttpStatusCode.BadRequest, "invalid_request");

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PatchTask_MalformedJson_Returns400()
    {
        await CreateAsync(new { title = "Bad body" });
        var response = await _client.PatchAsync("/api/tasks/TASK-1", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_body");
    }

    [Fact]
    public async Task MoveAndArchive_UnknownTask_Return404_UnknownStatusReturns400()
    {
        await AssertErrorAsync(await _client.PostAsJsonAsync("/api/tasks/TASK-99/move", new { status = "Done" }), HttpStatusCode.NotFound, "not_found");
        await AssertErrorAsync(await _client.PostAsync("/api/tasks/TASK-99/archive", null), HttpStatusCode.NotFound, "not_found");

        await CreateAsync(new { title = "Movable" });
        await AssertErrorAsync(await _client.PostAsJsonAsync("/api/tasks/TASK-1/move", new { status = "Nowhere" }), HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task ArchiveTask_Twice_IsIdempotent()
    {
        await CreateAsync(new { title = "Archive me" });
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/tasks/TASK-1/archive", null)).StatusCode);
        var second = await _client.PostAsync("/api/tasks/TASK-1/archive", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("tasks/archive/TASK-1 - Archive me.md", body.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ListAndBoard_FiltersNarrowResults_CaseInsensitively()
    {
        await CreateAsync(new { title = "Alpha login bug", labels = new[] { "Auth" }, assignee = new[] { "@ann" }, priority = "high", status = "In Progress", milestone = "v1" });
        await CreateAsync(new { title = "Beta docs", labels = new[] { "docs" }, assignee = new[] { "@bob" }, priority = "low" });

        async Task<string[]> Ids(string url)
        {
            var body = await _client.GetFromJsonAsync<JsonElement>(url, Json);
            return body.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToArray();
        }

        Assert.Equal(new[] { "TASK-1" }, await Ids("/api/tasks?label=auth"));
        Assert.Equal(new[] { "TASK-2" }, await Ids("/api/tasks?assignee=@BOB"));
        Assert.Equal(new[] { "TASK-1" }, await Ids("/api/tasks?priority=HIGH"));
        Assert.Equal(new[] { "TASK-1" }, await Ids("/api/tasks?status=in%20progress"));
        Assert.Equal(new[] { "TASK-1" }, await Ids("/api/tasks?milestone=V1"));
        Assert.Equal(new[] { "TASK-2" }, await Ids("/api/tasks?q=docs"));
        Assert.Empty(await Ids("/api/tasks?label=auth&priority=low"));

        // The board applies the same filters but always keeps every column.
        var board = await _client.GetFromJsonAsync<JsonElement>("/api/tasks/board?label=auth", Json);
        var columns = board.GetProperty("columns").EnumerateArray().ToArray();
        Assert.Equal(3, columns.Length);
        Assert.Equal(1, columns.Sum(c => c.GetProperty("tasks").GetArrayLength()));
    }

    [Fact]
    public async Task PatchTask_AcceptanceCriteriaReplace_AndClearWithEmptyList()
    {
        await CreateAsync(new { title = "AC task", acceptanceCriteria = new[] { "a", "b" } });

        var replaced = await _client.PatchAsJsonAsync("/api/tasks/TASK-1", new
        {
            acceptanceCriteria = new[] { new { text = "only", @checked = true } },
        });
        var body = await replaced.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(1, body.GetProperty("acTotal").GetInt32());
        Assert.Equal(1, body.GetProperty("acChecked").GetInt32());

        var cleared = await _client.PatchAsJsonAsync("/api/tasks/TASK-1", new { acceptanceCriteria = Array.Empty<object>() });
        var clearedBody = await cleared.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0, clearedBody.GetProperty("acTotal").GetInt32());
    }

    [Fact]
    public async Task WritesWhileVaultRootIsGone_Return503_AndNothingIsFabricated()
    {
        await CreateAsync(new { title = "Before the outage" });

        // Simulate an unmounted bind mount: the vault root disappears.
        var moved = _vaultRootPath + "-gone";
        Directory.Move(_vaultRootPath, moved);
        try
        {
            await AssertErrorAsync(await _client.PostAsJsonAsync("/api/tasks", new { title = "During the outage" }), HttpStatusCode.ServiceUnavailable, "vault_unavailable");
            await AssertErrorAsync(await _client.PatchAsJsonAsync("/api/tasks/TASK-1", new { priority = "high" }), HttpStatusCode.ServiceUnavailable, "vault_unavailable");
            await AssertErrorAsync(await _client.PostAsJsonAsync("/api/tasks/TASK-1/move", new { status = "Done" }), HttpStatusCode.ServiceUnavailable, "vault_unavailable");

            // Crucially the app must not have re-created an empty vault directory.
            Assert.False(Directory.Exists(_vaultRootPath));
        }
        finally
        {
            Directory.Move(moved, _vaultRootPath);
        }

        // Back online: the original task is intact and writes work again.
        var task = await _client.GetFromJsonAsync<JsonElement>("/api/tasks/TASK-1", Json);
        Assert.Equal("Before the outage", task.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _client.PatchAsJsonAsync("/api/tasks/TASK-1", new { priority = "high" })).StatusCode);
    }
}
