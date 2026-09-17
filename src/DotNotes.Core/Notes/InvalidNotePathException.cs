namespace DotNotes.Core.Notes;

/// <summary>
/// Thrown when a vault-relative note *or folder* path supplied by a caller
/// (a REST endpoint, an MCP tool, etc.) is not safe to resolve against the
/// vault root — e.g. it is absolute, contains a drive letter, contains a
/// <c>..</c> traversal segment, contains a null character, or resolves
/// (after normalization) to a location outside the vault root. Despite the
/// name, this type is not note-specific: folder-path validation (used by
/// <see cref="INoteRepository.CreateFolderAsync"/> and
/// <see cref="INoteRepository.MoveFolderAsync"/>) shares the same
/// escaping/traversal checks as note-path validation and throws this same
/// type — only the <c>.md</c>-extension requirement is note-specific. It
/// is also reused for a folder move whose destination is the source
/// folder itself or one of its own descendants, which is a path-shape
/// problem relative to the move's source, not a legitimate-but-occupied
/// destination (see <see cref="DestinationAlreadyExistsException"/> for
/// that latter case).
/// </summary>
/// <remarks>
/// This is the single exception type <see cref="FileSystemNoteRepository"/>
/// throws for every path-safety violation, so callers (e.g. the API layer)
/// can catch exactly this type and map it to a single, predictable HTTP
/// status code (400 Bad Request) without needing to distinguish between
/// the many ways a path can be unsafe.
/// </remarks>
public sealed class InvalidNotePathException : Exception
{
    /// <summary>
    /// The raw, caller-supplied path that failed validation. May be an
    /// empty string if the caller passed <see langword="null"/> or an
    /// empty/whitespace path.
    /// </summary>
    public string AttemptedPath { get; }

    public InvalidNotePathException(string? attemptedPath, string message)
        : base(message)
    {
        AttemptedPath = attemptedPath ?? string.Empty;
    }

    public InvalidNotePathException(string? attemptedPath, string message, Exception innerException)
        : base(message, innerException)
    {
        AttemptedPath = attemptedPath ?? string.Empty;
    }
}
