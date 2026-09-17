namespace DotNotes.Core.Notes;

/// <summary>
/// The result of creating or updating a note, as returned by
/// <see cref="INoteRepository.SaveAsync"/>. Backs
/// <c>PUT /api/notes/{**path}</c> per docs/04-API-SPEC.md.
/// </summary>
public sealed class NoteWriteResult
{
    /// <summary>Vault-relative path, <c>/</c>-separated (e.g. <c>projects/idea.md</c>).</summary>
    public required string Path { get; init; }

    /// <summary>The file's last-write timestamp after the write completed, UTC.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
