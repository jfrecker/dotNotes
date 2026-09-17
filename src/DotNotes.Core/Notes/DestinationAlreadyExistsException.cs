namespace DotNotes.Core.Notes;

/// <summary>
/// Thrown when a note move, folder move, or folder creation would land on
/// a vault-relative path that is already occupied by something else on
/// disk — an existing note, an existing folder, or (for
/// <see cref="INoteRepository.CreateFolderAsync"/>) a note file where a
/// folder is being created.
/// </summary>
/// <remarks>
/// This is deliberately distinct from <see cref="InvalidNotePathException"/>:
/// the path itself is well-formed and safe (it doesn't escape the vault
/// root, has no traversal segments, etc.) — the problem is that something
/// already legitimately exists there. Every one of dotNotes's move/create
/// operations refuses to silently overwrite existing content, per
/// CLAUDE.md's "never let data disappear silently" bar (e.g.
/// <see cref="FileSystemNoteRepository.MoveAsync"/> uses
/// <c>File.Move(source, dest, overwrite: false)</c> and translates the
/// resulting <see cref="IOException"/> into this type rather than letting
/// it leak out, or silently overwriting via <c>overwrite: true</c>).
/// Callers (REST endpoints, MCP tools) should catch exactly this type and
/// map it to 409 Conflict, per docs/04-API-SPEC.md.
/// </remarks>
public sealed class DestinationAlreadyExistsException : Exception
{
    /// <summary>The vault-relative path that is already occupied.</summary>
    public string Path { get; }

    public DestinationAlreadyExistsException(string path, string message)
        : base(message)
    {
        Path = path;
    }

    public DestinationAlreadyExistsException(string path, string message, Exception innerException)
        : base(message, innerException)
    {
        Path = path;
    }
}
