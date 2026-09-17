namespace DotNotes.Core.Config;

/// <summary>
/// Configuration for the public, token-protected share-link feature
/// (Phase 5). Present from Phase 0 so the config contract is stable for
/// later phases to bind against without another options-class rename.
/// </summary>
public sealed class SharingOptions
{
    /// <summary>
    /// The <c>appsettings.json</c> section name this binds to (also the
    /// environment-variable prefix, e.g. <c>Sharing__Enabled</c>).
    /// </summary>
    public const string SectionName = "Sharing";

    /// <summary>
    /// Whether share-link endpoints are enabled at all.
    /// </summary>
    public bool Enabled { get; set; }
}
