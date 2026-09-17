namespace DotNotes.Core.Reorganization;

/// <summary>
/// The result of <see cref="IVaultReorganizationService.MoveFolderAsync"/>.
/// Backs <c>POST /api/folders/{**path}/move</c> and the <c>move_folder</c>
/// MCP tool per docs/04-API-SPEC.md / docs/05-MCP-SPEC.md.
/// </summary>
public sealed class FolderMoveResult
{
    /// <summary>The moved folder's final, normalized, <c>/</c>-separated vault-relative path.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Vault-relative paths (at their new, post-move location, for any
    /// note that was itself inside the moved subtree) of every note whose
    /// content was rewritten as a result of this move, per
    /// docs/06-DATA-MODEL.md's "Folder &amp; note move/rename" section.
    /// Empty if nothing needed rewriting.
    /// </summary>
    public required IReadOnlyList<string> RewrittenNotes { get; init; }
}
