using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Integration tests for docs/04-API-SPEC.md's "Links &amp; graph"
/// section's <c>GET /api/graph</c> endpoint, exercised end-to-end
/// through a real ASP.NET Core test host (see <see cref="NotesApiFactory"/>).
/// </summary>
public sealed class GraphEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public GraphEndpointsTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-graph-api-tests-").FullName;
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
    public async Task GetGraph_EmptyVault_ReturnsEmptyNodesAndEdges()
    {
        var response = await _client.GetAsync("/api/graph");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var graph = await response.Content.ReadFromJsonAsync<GraphDto>(ResponseJsonOptions);
        Assert.NotNull(graph);
        Assert.Empty(graph!.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task GetGraph_ReflectsNotesWrittenBeforeStartup_IncludingUnresolvedTargets()
    {
        // Written before this instance's factory/client are created below,
        // so this note is present for VaultWatcherService's initial
        // RebuildAsync scan - no need to wait on the file watcher here.
        WriteNoteToDisk("projects/idea.md", "Links to [[projects/idea]] (self) and [[not-created-yet]].");

        using var factory = new NotesApiFactory(_vaultRootPath);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/graph");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var graph = await response.Content.ReadFromJsonAsync<GraphDto>(ResponseJsonOptions);
        Assert.NotNull(graph);

        var ideaNode = Assert.Single(graph!.Nodes, n => n.Id == "projects/idea.md");
        Assert.Equal("idea", ideaNode.Label);
        Assert.True(ideaNode.Exists);

        var missingNode = Assert.Single(graph.Nodes, n => n.Id == "not-created-yet.md");
        Assert.Equal("not-created-yet", missingNode.Label);
        Assert.False(missingNode.Exists);

        Assert.Contains(graph.Edges, e => e.Source == "projects/idea.md" && e.Target == "projects/idea.md");
        Assert.Contains(graph.Edges, e => e.Source == "projects/idea.md" && e.Target == "not-created-yet.md");
    }

    private void WriteNoteToDisk(string relativePath, string content)
    {
        var fullPath = Path.Combine(_vaultRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    private sealed record GraphDto(List<GraphNodeDto> Nodes, List<GraphEdgeDto> Edges);

    private sealed record GraphNodeDto(string Id, string Label, bool Exists);

    private sealed record GraphEdgeDto(string Source, string Target);
}
