namespace DotNotes.Core.Media;

/// <summary>
/// The content-type allowlist for uploaded/served media (docs/04-API-SPEC.md's
/// Media section / Phase 5's brief): images, audio, video, and PDF.
/// Framework-free so both <c>DotNotes.Api</c>'s upload endpoint
/// (validating an incoming Content-Type) and its media-serving endpoint
/// (choosing a Content-Type header for a file already on disk) - and unit
/// tests - share exactly one definition of "which extensions are
/// acceptable for which MIME type".
/// </summary>
public static class MediaContentTypes
{
    /// <summary>
    /// Every accepted MIME type mapped to the file extension(s) (including
    /// the leading '.', lowercase) considered a match for it. The first
    /// entry in each array is the canonical extension used to name a
    /// newly-saved file (see <see cref="GetCanonicalExtension"/>); later
    /// entries are accepted synonyms only (e.g. both ".jpg" and ".jpeg"
    /// are accepted for "image/jpeg", but new uploads are always saved as
    /// ".jpg").
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> AcceptedExtensionsByContentType =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = new[] { ".png" },
            ["image/jpeg"] = new[] { ".jpg", ".jpeg" },
            ["image/gif"] = new[] { ".gif" },
            ["image/webp"] = new[] { ".webp" },
            ["audio/mpeg"] = new[] { ".mp3" },
            ["audio/wav"] = new[] { ".wav" },
            ["audio/ogg"] = new[] { ".ogg" },
            ["video/mp4"] = new[] { ".mp4" },
            ["video/webm"] = new[] { ".webm" },
            ["application/pdf"] = new[] { ".pdf" },
        };

    /// <summary>
    /// Reverse of <see cref="AcceptedExtensionsByContentType"/>, keyed by
    /// extension, for serving an existing file back with the right
    /// Content-Type header.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ContentTypeByExtension =
        AcceptedExtensionsByContentType
            .SelectMany(kv => kv.Value.Select(extension => (extension, contentType: kv.Key)))
            .ToDictionary(t => t.extension, t => t.contentType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="contentType"/> is one of the accepted MIME types.</summary>
    public static bool IsAcceptedContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && AcceptedExtensionsByContentType.ContainsKey(contentType);

    /// <summary>The canonical (storage) extension for an accepted content type, or <see langword="null"/> if not accepted.</summary>
    public static string? GetCanonicalExtension(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && AcceptedExtensionsByContentType.TryGetValue(contentType, out var extensions)
            ? extensions[0]
            : null;

    /// <summary>Whether <paramref name="extension"/> (with or without a leading '.') is an accepted synonym for <paramref name="contentType"/>.</summary>
    public static bool ExtensionMatchesContentType(string? extension, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var normalized = extension.StartsWith('.') ? extension : $".{extension}";
        return AcceptedExtensionsByContentType.TryGetValue(contentType, out var extensions)
            && extensions.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Looks up the Content-Type for a file extension already on disk
    /// under <c>_media/</c> (with or without a leading '.'). Falls back to
    /// <c>application/octet-stream</c> for an unrecognized extension
    /// rather than failing - a file could in principle land under
    /// <c>_media/</c> by some means other than <c>POST /api/upload</c>
    /// (e.g. manual copy into a bind-mounted vault), and serving it back
    /// generically is safer than refusing outright for a personal tool.
    /// </summary>
    public static bool TryGetContentTypeForExtension(string? extension, out string contentType)
    {
        if (!string.IsNullOrWhiteSpace(extension) && ContentTypeByExtension.TryGetValue(extension, out var found))
        {
            contentType = found;
            return true;
        }

        contentType = "application/octet-stream";
        return false;
    }
}
