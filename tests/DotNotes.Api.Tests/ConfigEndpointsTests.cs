using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNotes.Api;

namespace DotNotes.Api.Tests;

/// <summary>
/// Integration tests for docs/04-API-SPEC.md's Config section:
/// <c>GET /api/config</c>, exercised end-to-end through a real ASP.NET
/// Core test host (see <see cref="NotesApiFactory"/>). Also verifies that
/// this endpoint's field sourcing agrees with the MCP <c>get_config</c>
/// tool's own tests (<c>DotNotesMcpToolsTests.GetConfig_*</c>), since both
/// share <see cref="AppInfo.GetConfig"/>.
/// </summary>
public sealed class ConfigEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private NotesApiFactory _factory;
    private HttpClient _client;

    public ConfigEndpointsTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-config-api-tests-").FullName;
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
    public async Task GetConfig_ReturnsExpectedShapeWithFeaturesEnabled()
    {
        var response = await _client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = await response.Content.ReadFromJsonAsync<AppConfigDto>(ResponseJsonOptions);

        Assert.NotNull(config);
        Assert.Equal("dotNotes", config!.Name);
        // Directory.Build.props (repo root) sets <Version>0.2.2</Version>,
        // reported via AssemblyInformationalVersionAttribute - see AppInfo.GetConfig.
        Assert.Equal("0.2.2", config.Version);
        Assert.True(config.Features.Sharing);
        Assert.True(config.Features.Mcp);
        Assert.True(config.Features.Graph);
        Assert.True(config.Features.Tasks);
        // Keep in sync with wwwroot/js/app.js's AUTOSAVE_DEBOUNCE_MS constant.
        Assert.Equal(1500, config.AutosaveDelayMs);
    }

    [Fact]
    public async Task GetConfig_SharingDisabled_ReportsFalseWithoutAffecting404Behaviour()
    {
        // Recreate the factory with Sharing disabled - the config endpoint
        // itself is never gated (unlike the sharing endpoints), so this
        // should just flip one field rather than 404ing.
        _client.Dispose();
        _factory.Dispose();
        _factory = new NotesApiFactory(_vaultRootPath, sharingEnabled: false);
        _client = _factory.CreateClient();

        var response = await _client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = await response.Content.ReadFromJsonAsync<AppConfigDto>(ResponseJsonOptions);
        Assert.False(config!.Features.Sharing);
    }

    [Fact]
    public async Task GetConfig_McpDisabled_ReportsFalseAndMcpRouteIsNotMapped()
    {
        _client.Dispose();
        _factory.Dispose();
        _factory = new NotesApiFactory(_vaultRootPath, mcpEnabled: false);
        _client = _factory.CreateClient();

        var configResponse = await _client.GetAsync("/api/config");
        var config = await configResponse.Content.ReadFromJsonAsync<AppConfigDto>(ResponseJsonOptions);
        Assert.False(config!.Features.Mcp);

        // The /mcp endpoint itself must not be mapped at all when disabled.
        var mcpResponse = await _client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.NotFound, mcpResponse.StatusCode);
    }

    private sealed record AppConfigDto(string Name, string Version, AppConfigFeaturesDto Features, int AutosaveDelayMs);

    private sealed record AppConfigFeaturesDto(bool Sharing, bool Mcp, bool Graph, bool Tasks);
}
