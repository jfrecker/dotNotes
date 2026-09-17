using System.Net;
using System.Net.Http.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Confirms every Sharing endpoint (docs/04-API-SPEC.md's Sharing
/// section, plus the token-scoped content endpoint) returns the same
/// generic 404 when <c>Sharing:Enabled</c> is <c>false</c>, per this
/// phase's brief - a separate test class (rather than folding into
/// <see cref="SharingEndpointsTests"/>) because it needs its own
/// <see cref="NotesApiFactory"/> constructed with sharing disabled.
/// </summary>
public sealed class SharingDisabledEndpointsTests : IDisposable
{
    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public SharingDisabledEndpointsTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-sharing-disabled-api-tests-").FullName;
        _factory = new NotesApiFactory(_vaultRootPath, sharingEnabled: false);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Vault__RootPath", null);
        Environment.SetEnvironmentVariable("Sharing__Enabled", null);

        if (Directory.Exists(_vaultRootPath))
        {
            Directory.Delete(_vaultRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task PostShare_SharingDisabled_Returns404()
    {
        WriteNoteToDisk("projects/idea.md", "content");

        var response = await _client.PostAsJsonAsync("/api/share/projects/idea.md", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteShare_SharingDisabled_Returns404()
    {
        var response = await _client.DeleteAsync("/api/share/some-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetShareContent_SharingDisabled_Returns404()
    {
        var response = await _client.GetAsync("/api/share/some-token/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSharedPage_SharingDisabled_Returns404()
    {
        var response = await _client.GetAsync("/shared/some-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
}
