namespace DotNotes.Core.Notes;

/// <summary>
/// A note's raw content plus metadata, as returned by
/// <see cref="INoteRepository.GetAsync"/>. Backs
/// <c>GET /api/notes/{**path}</c> per docs/04-API-SPEC.md.
/// </summary>
public sealed class NoteContent
{
    /// <summary>Vault-relative path, <c>/</c>-separated (e.g. <c>projects/idea.md</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Raw markdown, UTF-8 decoded.</summary>
    public required string Content { get; init; }

    /// <summary>The file's last-write timestamp, UTC.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
