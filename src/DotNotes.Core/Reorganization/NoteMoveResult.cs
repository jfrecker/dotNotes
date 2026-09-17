namespace DotNotes.Core.Reorganization;

/// <summary>
/// The result of <see cref="IVaultReorganizationService.MoveNoteAsync"/>.
/// Backs <c>POST /api/notes/{**path}/move</c> and the <c>move_note</c> MCP
/// tool per docs/04-API-SPEC.md / docs/05-MCP-SPEC.md.
/// </summary>
public sealed class NoteMoveResult
{
    /// <summary>The moved note's final, normalized, <c>/</c>-separated vault-relative path.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The moved note's last-write timestamp after the move (and, if its
    /// own content was rewritten because of a self-link, after that
    /// rewrite too), UTC.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Vault-relative paths of every note (including the moved note
    /// itself, if it contained a self-link that needed rewriting) whose
    /// content was rewritten as a result of this move, per
    /// docs/06-DATA-MODEL.md's "Folder &amp; note move/rename" section.
    /// Empty if nothing needed rewriting.
    /// </summary>
    public required IReadOnlyList<string> RewrittenNotes { get; init; }
}
