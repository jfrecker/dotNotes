namespace DotNotes.Core.Config;

/// <summary>
/// Configuration for the in-process MCP server endpoint (Phase 6).
/// Present from Phase 0 so the config contract is stable for later
/// phases to bind against without another options-class rename.
/// </summary>
public sealed class McpOptions
{
    /// <summary>
    /// The <c>appsettings.json</c> section name this binds to (also the
    /// environment-variable prefix, e.g. <c>Mcp__Enabled</c>).
    /// </summary>
    public const string SectionName = "Mcp";

    /// <summary>
    /// Whether the <c>/mcp</c> endpoint is enabled at all.
    /// </summary>
    public bool Enabled { get; set; }
}
