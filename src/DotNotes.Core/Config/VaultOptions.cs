namespace DotNotes.Core.Config;

/// <summary>
/// Configuration for the vault: the directory on disk that holds every
/// note as a plain file. This is the single source of truth for the
/// whole app — every index (search, links, graph) is a derived cache
/// computed from this directory, never the other way around.
/// </summary>
public sealed class VaultOptions
{
    /// <summary>
    /// The <c>appsettings.json</c> section name this binds to (also the
    /// environment-variable prefix, e.g. <c>Vault__RootPath</c>).
    /// </summary>
    public const string SectionName = "Vault";

    /// <summary>
    /// Path to the vault root directory. May be relative (resolved
    /// against the application's content root) or absolute (e.g. a
    /// Docker bind-mount path such as <c>/data/vault</c>).
    /// </summary>
    public string RootPath { get; set; } = string.Empty;
}
