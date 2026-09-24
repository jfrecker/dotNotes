using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Endpoint-level regression tests for the review findings (move by
/// <c>beforeId</c>, hostile field values never producing a 500 or a vanished
/// task, unknown-status columns). Own file so it doesn't clash with
/// <see cref="TasksEndpointsTests"/>.
/// </summary>
public sealed class TasksReviewRegressionTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public TasksReviewRegressionTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-tasks-review-api-tests-").FullName;
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

    private async Task<string> CreateAsync(string title, string? status = null)
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { title, status });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<IdDto>(Json);
        return task!.Id;
    }

    private async Task<string[]> ColumnAsync(string status)
    {
        var board = await _client.GetFromJsonAsync<BoardDto>("/api/tasks/board", Json);
        return board!.Columns.Single(c => c.Status == status).Tasks.Select(t => t.Id).ToArray();
    }

    [Fact]
    public async Task Move_WithBeforeId_InsertsBeforeThatCard()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");

        var response = await _client.PostAsJsonAsync($"/api/tasks/{c}/move", new { status = "To Do", beforeId = b });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { a, c, b }, await ColumnAsync("To Do"));
    }

    [Fact]
    public async Task Move_BeforeIdWinsOverIndex()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");

        var response = await _client.PostAsJsonAsync($"/api/tasks/{c}/move", new { status = "To Do", index = 0, beforeId = b });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { a, c, b }, await ColumnAsync("To Do"));
    }

    [Fact]
    public async Task Move_BeforeIdNotInTargetColumn_Returns400()
    {
        var a = await CreateAsync("A", "Done");
        var b = await CreateAsync("B");

        var response = await _client.PostAsJsonAsync($"/api/tasks/{b}/move", new { status = "To Do", beforeId = a });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(Json);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task Move_ToWhereItAlreadyIs_DoesNotTouchTheFile()
    {
        var a = await CreateAsync("A");
        await CreateAsync("B");
        var file = Path.Combine(_vaultRootPath, "tasks", "TASK-1 - A.md");
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var response = await _client.PostAsJsonAsync($"/api/tasks/{a}/move", new { status = "To Do", index = 0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(file));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\n---\r\nb")]
    [InlineData("a\tb")]
    [InlineData("a\u0085b")]
    [InlineData("a\u2028b")]
    public async Task CreateAndPatch_HostileValues_NeverProduceA500OrAVanishedTask(string value)
    {
        var create = await _client.PostAsJsonAsync("/api/tasks", new { title = value, labels = new[] { value, "" }, assignee = new[] { value }, milestone = value });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<IdDto>(Json))!.Id;

        var patch = await _client.PatchAsJsonAsync($"/api/tasks/{id}", new { title = value + "!", labels = new[] { value }, dependencies = new[] { value } });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var get = await _client.GetAsync($"/api/tasks/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var listed = await _client.GetFromJsonAsync<BoardDto>("/api/tasks/board", Json);
        Assert.Contains(listed!.Columns.SelectMany(c => c.Tasks), t => t.Id == id);
    }

    [Fact]
    public async Task Move_WithinUnknownStatusColumn_Works()
    {
        foreach (var n in new[] { 1, 2 })
        {
            await File.WriteAllTextAsync(
                Path.Combine(_vaultRootPath, $"held{n}.md"),
                $"---\nid: TASK-{n}\ntitle: Held {n}\nstatus: Blocked\nordinal: {n * 1000}\n---\nBody\n");
        }

        // Let the watcher pick the files up.
        for (var i = 0; i < 50 && (await _client.GetAsync("/api/tasks/TASK-2")).StatusCode != HttpStatusCode.OK; i++)
        {
            await Task.Delay(100);
        }

        var response = await _client.PostAsJsonAsync("/api/tasks/TASK-2/move", new { status = "Blocked", index = 0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "TASK-2", "TASK-1" }, await ColumnAsync("Blocked"));
    }

    private sealed record IdDto(string Id);

    private sealed record SummaryDto(string Id);

    private sealed record ColumnDto(string Status, List<SummaryDto> Tasks);

    private sealed record BoardDto(List<ColumnDto> Columns);

    private sealed record ErrorDto(string Error, string? Detail);
}
