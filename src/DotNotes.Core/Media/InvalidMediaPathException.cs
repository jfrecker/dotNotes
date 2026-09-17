namespace DotNotes.Core.Media;

/// <summary>
/// Thrown when a vault-relative media path (route parameter of
/// <c>GET /media/{**path}</c>, or an internally-generated file name) is
/// not safe to resolve against the <c>_media/</c> directory - e.g. it is
/// absolute, contains a drive letter, contains a <c>..</c> traversal
/// segment, contains a null character, or resolves (after normalization)
/// to a location outside <c>_media/</c>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="Notes.InvalidNotePathException"/>'s role for
/// <see cref="Notes.INoteRepository"/>: the single exception type
/// <see cref="FileSystemMediaStore"/> throws for every path-safety
/// violation, so callers (the API layer) can catch exactly this type and
/// map it to a single, predictable HTTP status code (400 Bad Request).
/// </remarks>
public sealed class InvalidMediaPathException : Exception
{
    /// <summary>The raw, caller-supplied path that failed validation.</summary>
    public string AttemptedPath { get; }

    public InvalidMediaPathException(string? attemptedPath, string message)
        : base(message)
    {
        AttemptedPath = attemptedPath ?? string.Empty;
    }
}
