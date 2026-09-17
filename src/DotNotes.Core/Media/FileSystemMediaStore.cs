using System.Text.RegularExpressions;
using DotNotes.Core.Config;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Media;

/// <summary>
/// Disk-backed <see cref="IMediaStore"/>. Uploaded binary assets live
/// under <c>_media/</c> in the vault root, named with a freshly-generated
/// GUID (never the caller's original file name). This type has no
/// mutable state beyond the resolved media root path, so a singleton
/// lifetime is safe - same shape as
/// <see cref="Notes.FileSystemNoteRepository"/>, whose path-safety
/// discipline this type deliberately mirrors.
/// </summary>
public sealed class FileSystemMediaStore : IMediaStore
{
    private const string MediaDirectoryName = "_media";

    // See FileSystemNoteRepository.DriveLetterPattern for the exact same
    // "C:foo" drive-relative-path rationale.
    private static readonly Regex DriveLetterPattern = new(@"^[a-zA-Z]:", RegexOptions.Compiled);

    // A short, plain alphanumeric extension (".png", ".jpg", ".mp3", ...).
    // Deliberately strict: this is the one piece of a generated file name
    // that ultimately comes from caller input (the declared Content-Type /
    // original file name), so it's validated independently of - and more
    // strictly than - the generated GUID stem around it.
    private static readonly Regex SafeExtensionPattern = new(@"^\.[a-zA-Z0-9]{1,10}$", RegexOptions.Compiled);

    private readonly string _mediaRootFullPath;
    private readonly string _mediaRootWithTrailingSeparator;
    private readonly StringComparison _pathComparison;

    public FileSystemMediaStore(IOptions<VaultOptions> vaultOptions)
    {
        var vaultRootFullPath = VaultPathValidator.EnsureVaultRootExists(vaultOptions.Value.RootPath);
        _mediaRootFullPath = Path.Combine(vaultRootFullPath, MediaDirectoryName);
        Directory.CreateDirectory(_mediaRootFullPath);

        _mediaRootWithTrailingSeparator = _mediaRootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? _mediaRootFullPath
            : _mediaRootFullPath + Path.DirectorySeparatorChar;

        // Same filesystem-case-sensitivity handling as FileSystemNoteRepository.
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public async Task<string> SaveAsync(Stream content, string fileExtension, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var sanitizedExtension = SanitizeExtension(fileExtension);

        // Never trust the caller's original filename: generate a fresh
        // GUID-based name and keep only the already-validated extension,
        // so an uploaded filename can never introduce path traversal,
        // collisions, or unexpected characters (docs/06-DATA-MODEL.md /
        // this phase's brief).
        var generatedFileName = $"{Guid.NewGuid():N}{sanitizedExtension}";
        var fullPath = Path.Combine(_mediaRootFullPath, generatedFileName);

        if (!IsWithinMediaRoot(fullPath))
        {
            // Should be unreachable given the GUID-based name above, but
            // kept as a final choke point, mirroring
            // FileSystemNoteRepository's equivalent defense-in-depth check.
            throw new InvalidMediaPathException(generatedFileName, "Generated media path resolved outside the media root.");
        }

        var tempFilePath = Path.Combine(_mediaRootFullPath, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fileStream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            // A brand-new GUID-based name should never collide with an
            // existing file, so overwrite is deliberately left false here
            // (unlike FileSystemNoteRepository.SaveAsync, which is an
            // intentional upsert) - a collision would indicate something
            // unexpected and is worth surfacing as an error.
            File.Move(tempFilePath, fullPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }

            throw;
        }

        return $"{MediaDirectoryName}/{generatedFileName}";
    }

    public string? ResolveExistingMediaFullPath(string relativePath)
    {
        var fullPath = ResolveMediaPath(relativePath);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static string SanitizeExtension(string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(fileExtension))
        {
            throw new ArgumentException("A file extension is required to save media.", nameof(fileExtension));
        }

        var normalized = (fileExtension.StartsWith('.') ? fileExtension : $".{fileExtension}").ToLowerInvariant();
        if (!SafeExtensionPattern.IsMatch(normalized))
        {
            throw new ArgumentException($"'{fileExtension}' is not a safe file extension.", nameof(fileExtension));
        }

        return normalized;
    }

    /// <summary>
    /// Resolves and validates a caller-supplied media-relative path (the
    /// suffix after <c>_media/</c>). Mirrors
    /// FileSystemNoteRepository.ResolveNotePath's checks, minus the
    /// ".md extension required" rule, which doesn't apply to binary media.
    /// </summary>
    private string ResolveMediaPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidMediaPathException(relativePath, "Media path must not be null or empty.");
        }

        if (relativePath.Contains('\0'))
        {
            throw new InvalidMediaPathException(relativePath, "Media path must not contain null characters.");
        }

        if (Path.IsPathRooted(relativePath) || DriveLetterPattern.IsMatch(relativePath))
        {
            throw new InvalidMediaPathException(relativePath, "Media path must be relative, not absolute.");
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidMediaPathException(relativePath, "Media path must not be empty.");
        }

        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidMediaPathException(relativePath, "Media path must not contain '.' or '..' segments.");
        }

        var platformRelativePath = string.Join(Path.DirectorySeparatorChar, segments);
        var fullPath = Path.GetFullPath(Path.Combine(_mediaRootFullPath, platformRelativePath));

        if (!IsWithinMediaRoot(fullPath))
        {
            throw new InvalidMediaPathException(relativePath, "Media path resolves outside the media root.");
        }

        return fullPath;
    }

    private bool IsWithinMediaRoot(string fullPath) =>
        fullPath.StartsWith(_mediaRootWithTrailingSeparator, _pathComparison);
}
