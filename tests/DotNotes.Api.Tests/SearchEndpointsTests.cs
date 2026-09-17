using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Integration tests for docs/04-API-SPEC.md's Search section's
/// <c>GET /api/search</c> endpoint, exercised end-to-end through a real
/// ASP.NET Core test host (see <see cref="NotesApiFactory"/>).
/// </summary>
public sealed class SearchEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public SearchEndpointsTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-search-api-tests-").FullName;
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
    public async Task GetSearch_EmptyVault_ReturnsEmptyArray()
    {
        var response = await _client.GetAsync("/api/search?q=idea");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<SearchResultDto>>(ResponseJsonOptions);
        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    [Fact]
    public async Task GetSearch_MissingQuery_ReturnsEmptyArrayNot400()
    {
        // Judgment call documented in SearchEndpoints.cs: a missing/blank
        // `q` returns 200 with an empty array, not a 400.
        var response = await _client.GetAsync("/api/search");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<SearchResultDto>>(ResponseJsonOptions);
        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    [Fact]
    public async Task GetSearch_ReflectsNotesWrittenBeforeStartup()
    {
        // Written before this instance's factory/client are created, so
        // present for VaultWatcherService's initial scan - no need to
        // wait on the file watcher here (mirrors GraphEndpointsTests'
        // equivalent test).
        WriteNoteToDisk("projects/idea.md", "Some notes about gardening in spring.");

        using var factory = new NotesApiFactory(_vaultRootPath);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/search?q=gardening");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<SearchResultDto>>(ResponseJsonOptions);
        Assert.NotNull(results);
        var result = Assert.Single(results!);
        Assert.Equal("projects/idea.md", result.Path);
        Assert.Equal("idea", result.Title);
        Assert.Contains("gardening", result.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSearch_TitleMatchRanksAboveBodyOnlyMatch()
    {
        WriteNoteToDisk("projects/idea.md", "This body has no relevant terms at all.");
        WriteNoteToDisk("projects/other.md", "idea idea idea mentioned only in the body here.");

        var results = await SearchUntilAsync("idea", expectedCount: 2);

        Assert.Equal("projects/idea.md", results[0].Path);
        Assert.Equal("projects/other.md", results[1].Path);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task GetSearch_RespectsLimitParameter()
    {
        for (var i = 0; i < 5; i++)
        {
            WriteNoteToDisk($"notes/note{i}.md", "gardening tips and tricks");
        }

        var results = await SearchUntilAsync("gardening", expectedCount: 2, limit: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetSearch_EditingANote_UpdatesResultsLiveWithoutRestart()
    {
        WriteNoteToDisk("projects/idea.md", "about gardening");
        await SearchUntilAsync("gardening", expectedCount: 1);

        WriteNoteToDisk("projects/idea.md", "about astronomy now");

        await SearchUntilAsync("astronomy", expectedCount: 1);
        var stale = await SearchOnceAsync("gardening");
        Assert.Empty(stale);
    }

    [Fact]
    public async Task GetSearch_DeletingANote_RemovesItFromResultsLiveWithoutRestart()
    {
        WriteNoteToDisk("projects/idea.md", "about gardening");
        await SearchUntilAsync("gardening", expectedCount: 1);

        File.Delete(Path.Combine(_vaultRootPath, "projects", "idea.md"));

        await SearchUntilAsync("gardening", expectedCount: 0);
    }

    private async Task<List<SearchResultDto>> SearchOnceAsync(string query, int? limit = null)
    {
        var uri = limit is null ? $"/api/search?q={query}" : $"/api/search?q={query}&limit={limit}";
        var response = await _client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<SearchResultDto>>(ResponseJsonOptions);
        return results ?? [];
    }

    /// <summary>
    /// Polls <c>GET /api/search</c> until it returns exactly
    /// <paramref name="expectedCount"/> results or a generous timeout
    /// elapses - used for assertions that depend on the debounced
    /// file-watcher having settled (see <c>NotesEndpointsTests</c>'s
    /// equivalent helper).
    /// </summary>
    private async Task<List<SearchResultDto>> SearchUntilAsync(string query, int expectedCount, int? limit = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        List<SearchResultDto> results = [];

        while (DateTime.UtcNow < deadline)
        {
            results = await SearchOnceAsync(query, limit);
            if (results.Count == expectedCount)
            {
                return results;
            }

            await Task.Delay(100);
        }

        return results;
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

    private sealed record SearchResultDto(string Path, string Title, string Snippet, double Score);
}
