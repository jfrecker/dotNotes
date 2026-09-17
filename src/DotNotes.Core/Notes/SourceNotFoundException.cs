namespace DotNotes.Core.Notes;

/// <summary>
/// Thrown by <see cref="INoteRepository.MoveAsync"/> and
/// <see cref="INoteRepository.MoveFolderAsync"/> when the source note or
/// folder does not exist on disk.
/// </summary>
/// <remarks>
/// Distinct from <see cref="InvalidNotePathException"/> (a malformed/unsafe
/// path shape) and from <see cref="DestinationAlreadyExistsException"/> (a
/// well-formed destination that's already occupied): the source path here
/// is perfectly valid and safe, there's simply nothing on disk to move.
/// This mirrors the "not found" cases elsewhere in
/// <see cref="INoteRepository"/> (e.g. <c>GetAsync</c> returning
/// <see langword="null"/>), but a return value can't cleanly express "the
/// move didn't happen" the way it can for a read, so a move throws
/// instead. Callers (REST endpoints, MCP tools) should catch exactly this
/// type and map it to 404 Not Found, per docs/04-API-SPEC.md.
/// </remarks>
public sealed class SourceNotFoundException : Exception
{
    /// <summary>The vault-relative source path that does not exist.</summary>
    public string SourcePath { get; }

    public SourceNotFoundException(string sourcePath, string message)
        : base(message)
    {
        SourcePath = sourcePath;
    }

    public SourceNotFoundException(string sourcePath, string message, Exception innerException)
        : base(message, innerException)
    {
        SourcePath = sourcePath;
    }
}
