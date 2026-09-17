namespace DotNotes.Core.Config;

/// <summary>
/// Validates and prepares the vault root directory at startup. This is
/// deliberately framework-free (no ASP.NET Core, no DI container
/// dependency) so it can be exercised directly from plain xUnit tests.
/// </summary>
public static class VaultPathValidator
{
    /// <summary>
    /// Resolves <paramref name="rootPath"/> to a full, normalized path
    /// and ensures the directory exists on disk, creating it (and any
    /// missing parent directories) if necessary.
    /// </summary>
    /// <param name="rootPath">
    /// An absolute or relative path to the vault root. Callers that need
    /// relative paths resolved against a specific base directory (e.g.
    /// the ASP.NET Core content root) must combine them before calling
    /// this method — this method itself only calls
    /// <see cref="Path.GetFullPath(string)"/>, which resolves relative
    /// paths against the current working directory.
    /// </param>
    /// <returns>The full, normalized path to the (now-existing) vault root.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="rootPath"/> is null, empty, or whitespace.
    /// </exception>
    public static string EnsureVaultRootExists(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Vault root path must not be null or empty.", nameof(rootPath));
        }

        var fullPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }
}
