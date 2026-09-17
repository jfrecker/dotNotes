using DotNotes.Core.Config;
using Microsoft.Extensions.Options;

namespace DotNotes.Api;

/// <summary>
/// Computes the app's self-reported configuration/capabilities, shared
/// verbatim by both <c>GET /api/config</c> (docs/04-API-SPEC.md's Config
/// section) and the MCP <c>get_config</c> tool (docs/05-MCP-SPEC.md), so
/// the two surfaces can never drift apart - see <see cref="Endpoints.ConfigEndpoints"/>
/// and <see cref="Mcp.DotNotesMcpTools.GetConfig"/>.
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// Debounce delay (ms) the frontend waits after the user stops typing
    /// before autosaving a note. Reported here so a calling AI assistant
    /// can infer roughly how "live" the on-disk content is.
    /// </summary>
    /// <remarks>
    /// Keep in sync with <c>wwwroot/js/app.js</c>'s <c>AUTOSAVE_DEBOUNCE_MS</c>
    /// constant - the frontend does not fetch this value dynamically
    /// (out of scope for Phase 6), so the two must be updated together by
    /// hand if either ever changes.
    /// </remarks>
    public const int AutosaveDelayMs = 1500;

    /// <summary>Display name reported by <c>GET /api/config</c> and the <c>get_config</c> MCP tool.</summary>
    public const string Name = "dotNotes";

    public static AppConfigResponse GetConfig(IOptions<SharingOptions> sharingOptions, IOptions<McpOptions> mcpOptions)
    {
        // No <Version> is set in DotNotes.Api.csproj, so this falls back to
        // the SDK's implicit default AssemblyVersion (1.0.0.0) until the
        // csproj is given an explicit <Version> down the line.
        var version = typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        return new AppConfigResponse(
            Name,
            version,
            new AppConfigFeatures(
                Sharing: sharingOptions.Value.Enabled,
                Mcp: mcpOptions.Value.Enabled,
                // Always on since Phase 3 - no feature flag exists for the
                // graph/backlinks functionality itself (only sharing and
                // MCP are independently toggleable), per this phase's brief.
                Graph: true),
            AutosaveDelayMs);
    }
}

/// <summary>Backs <c>GET /api/config</c> and the <c>get_config</c> MCP tool's <c>{ name, version, features, autosaveDelayMs }</c> response.</summary>
public sealed record AppConfigResponse(string Name, string Version, AppConfigFeatures Features, int AutosaveDelayMs);

/// <summary>The <c>features</c> object of <see cref="AppConfigResponse"/>.</summary>
public sealed record AppConfigFeatures(bool Sharing, bool Mcp, bool Graph);
