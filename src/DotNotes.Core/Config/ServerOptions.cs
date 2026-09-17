namespace DotNotes.Core.Config;

/// <summary>
/// Configuration for the web host itself.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// The <c>appsettings.json</c> section name this binds to (also the
    /// environment-variable prefix, e.g. <c>Server__Port</c>).
    /// </summary>
    public const string SectionName = "Server";

    /// <summary>
    /// The port the app listens on. Note that Kestrel's actual bound
    /// port is normally controlled via <c>ASPNETCORE_URLS</c> / launch
    /// settings; this value is surfaced for features (e.g. building
    /// share-link URLs) that need to know the app's own port.
    /// </summary>
    public int Port { get; set; } = 5175;
}
