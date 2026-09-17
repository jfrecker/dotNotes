namespace DotNotes.Core.Notes;

/// <summary>
/// One node in the vault's file/folder tree, as returned by
/// <see cref="INoteRepository.GetTreeAsync"/>. Backs
/// <c>GET /api/notes</c> per docs/04-API-SPEC.md.
/// </summary>
public sealed class NoteTreeEntry
{
    /// <summary>
    /// Vault-relative path using <c>/</c> as the separator regardless of
    /// host OS (e.g. <c>projects/idea.md</c>). This is the note's identity
    /// per docs/06-DATA-MODEL.md — there is no separate ID/GUID.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>File or directory name only, e.g. <c>idea.md</c>.</summary>
    public required string Name { get; init; }

    public required NoteEntryType Type { get; init; }

    /// <summary>
    /// Child entries when <see cref="Type"/> is <see cref="NoteEntryType.Folder"/>;
    /// always populated (possibly empty) for folders, and always
    /// <see langword="null"/> for files.
    /// </summary>
    public IReadOnlyList<NoteTreeEntry>? Children { get; init; }
}
