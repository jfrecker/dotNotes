using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Integration tests for docs/04-API-SPEC.md's Sharing section
/// (<c>POST/DELETE /api/share/...</c>, <c>GET /shared/{token}</c>) plus
/// the token-scoped <c>GET /api/share/{token}/content</c> endpoint
/// introduced to back the read-only shared view, exercised end-to-end
/// through a real ASP.NET Core test host (see <see cref="NotesApiFactory"/>).
/// </summary>
public sealed class SharingEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public SharingEndpointsTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-sharing-api-tests-").FullName;
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
    public async Task PostShare_ExistingNote_ReturnsTokenUrlAndWellFormedQrCode()
    {
        WriteNoteToDisk("projects/idea.md", "# idea\n\nsome content");

        var response = await _client.PostAsJsonAsync("/api/share/projects/idea.md", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var share = await response.Content.ReadFromJsonAsync<ShareCreatedDto>(ResponseJsonOptions);
        Assert.NotNull(share);
        Assert.False(string.IsNullOrWhiteSpace(share!.Token));
        Assert.Contains($"/shared/{share.Token}", share.Url);
        Assert.EndsWith(share.Token, share.Url);

        // A well-formed base64 string decodes cleanly and (being a PNG)
        // starts with the PNG magic bytes.
        var pngBytes = Convert.FromBase64String(share.QrCodePngBase64);
        Assert.True(pngBytes.Length > 8);
        Assert.Equal(0x89, pngBytes[0]);
        Assert.Equal((byte)'P', pngBytes[1]);
        Assert.Equal((byte)'N', pngBytes[2]);
        Assert.Equal((byte)'G', pngBytes[3]);
    }

    [Fact]
    public async Task PostShare_WithNoBody_StillSucceeds()
    {
        WriteNoteToDisk("projects/idea.md", "content");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/share/projects/idea.md");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostShare_MissingNote_Returns404WithErrorShape()
    {
        var response = await _client.PostAsJsonAsync("/api/share/does/not/exist.md", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("not_found", error!.Error);
    }

    [Fact]
    public async Task PostShare_PathTraversalAttempt_Returns400WithErrorShape()
    {
        var response = await _client.PostAsJsonAsync("/api/share/..%5coutside.md", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
    }

    [Fact]
    public async Task SharedFlow_CreateThenGetContentAndPage_ThenRevoke_TokenStopsResolving()
    {
        WriteNoteToDisk("projects/idea.md", "# idea\n\nSome shared content.");

        var createResponse = await _client.PostAsJsonAsync("/api/share/projects/idea.md", new { });
        var share = await createResponse.Content.ReadFromJsonAsync<ShareCreatedDto>(ResponseJsonOptions);
        Assert.NotNull(share);

        // Token-scoped content endpoint - the shared view's *only* data
        // source (see js/shared.js), not the main /api/notes/* surface.
        var contentResponse = await _client.GetAsync($"/api/share/{share!.Token}/content");
        Assert.Equal(HttpStatusCode.OK, contentResponse.StatusCode);
        var sharedContent = await contentResponse.Content.ReadFromJsonAsync<SharedContentDto>(ResponseJsonOptions);
        Assert.NotNull(sharedContent);
        Assert.Equal("projects/idea.md", sharedContent!.Path);
        Assert.Equal("# idea\n\nSome shared content.", sharedContent.Content);

        // The rendered page itself: HTML, not JSON.
        var pageResponse = await _client.GetAsync($"/shared/{share.Token}");
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.StartsWith("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
        var html = await pageResponse.Content.ReadAsStringAsync();
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);

        // Revoke, then confirm every token-scoped endpoint 404s identically.
        var deleteResponse = await _client.DeleteAsync($"/api/share/{share.Token}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var contentAfterRevoke = await _client.GetAsync($"/api/share/{share.Token}/content");
        Assert.Equal(HttpStatusCode.NotFound, contentAfterRevoke.StatusCode);

        var pageAfterRevoke = await _client.GetAsync($"/shared/{share.Token}");
        Assert.Equal(HttpStatusCode.NotFound, pageAfterRevoke.StatusCode);
    }

    [Fact]
    public async Task DeleteShare_UnknownToken_Returns204Idempotently()
    {
        var response = await _client.DeleteAsync("/api/share/never-issued-token");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetShareContent_UnknownToken_Returns404WithGenericErrorShape()
    {
        var response = await _client.GetAsync("/api/share/never-issued-token/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("not_found", error!.Error);
    }

    [Fact]
    public async Task GetSharedPage_UnknownToken_Returns404NotThePageShell()
    {
        var response = await _client.GetAsync("/shared/never-issued-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Phase 8 QA follow-up: PostShareAsync calls INoteRepository.ExistsAsync
    // directly (see SharingEndpoints.cs), so it needs the same
    // vault-root-vanished guard/503 mapping as the Notes endpoints - see
    // NotesEndpointsTests's matching vault-outage tests for the full scenario.
    [Fact]
    public async Task PostShare_WhenVaultRootHasVanished_Returns503WithAppErrorShape()
    {
        Directory.Delete(_vaultRootPath, recursive: true);

        var response = await _client.PostAsJsonAsync("/api/share/projects/idea.md", new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("vault_unavailable", error!.Error);
        Assert.False(string.IsNullOrWhiteSpace(error.Detail));
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

    private sealed record ShareCreatedDto(string Token, string Url, string QrCodePngBase64);

    private sealed record SharedContentDto(string Path, string Content, DateTimeOffset UpdatedAt);

    private sealed record ErrorDto(string Error, string? Detail);
}
