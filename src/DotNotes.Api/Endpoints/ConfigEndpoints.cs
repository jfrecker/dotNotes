using DotNotes.Core.Config;
using Microsoft.Extensions.Options;

namespace DotNotes.Api.Endpoints;

/// <summary>
/// Wires up docs/04-API-SPEC.md's Config section: <c>GET /api/config</c>.
/// Deliberately thin - all field sourcing lives in <see cref="AppInfo"/> so
/// this endpoint and the MCP <c>get_config</c> tool
/// (<see cref="Mcp.DotNotesMcpTools.GetConfig"/>) can never report
/// different values for the same underlying configuration.
/// </summary>
public static class ConfigEndpoints
{
    public static WebApplication MapConfigEndpoints(this WebApplication app)
    {
        app.MapGet("/api/config", GetConfig);

        return app;
    }

    private static IResult GetConfig(IOptions<SharingOptions> sharingOptions, IOptions<McpOptions> mcpOptions) =>
        Results.Ok(AppInfo.GetConfig(sharingOptions, mcpOptions));
}
