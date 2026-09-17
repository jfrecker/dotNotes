namespace DotNotes.Core.Reorganization;

/// <summary>
/// The single <c>DotNotes.Core</c> orchestration point for moving/renaming
/// a note or a folder: performs the move via <see cref="Notes.INoteRepository"/>,
/// rewrites incoming <c>[[wikilinks]]</c> that would otherwise break, and
/// synchronously updates every registered <see cref="Vault.IVaultChangeListener"/>
/// (the link index and the search index) before returning - so both REST
/// endpoints and MCP tools get identical behavior and neither can drift
/// from the other. See docs/06-DATA-MODEL.md's "Folder &amp; note
/// move/rename" section for the full rewrite/consistency contract this
/// type implements.
/// </summary>
/// <remarks>
/// Both operations propagate <see cref="Notes.InvalidNotePathException"/>,
/// <see cref="Notes.SourceNotFoundException"/>,
/// <see cref="Notes.DestinationAlreadyExistsException"/> and
/// <see cref="Notes.VaultUnavailableException"/> unchanged from the
/// underlying <see cref="Notes.INoteRepository"/> call - callers (REST
/// endpoints, MCP tools) keep mapping exactly these types to their
/// existing HTTP/MCP error shapes, with no new exception types to handle.
/// When one of these is thrown, nothing has been moved or rewritten - the
/// vault is left exactly as it was before the call (the wikilink-scanning
/// pass that runs first is read-only).
/// <para>
/// Reorganization operations are serialized (internally, via a
/// <see cref="System.Threading.SemaphoreSlim"/>) so that two moves - e.g. a
/// double-click in the UI and a concurrent MCP call - can never interleave
/// their scan-then-rewrite passes against each other.
/// </para>
/// </remarks>
public interface IVaultReorganizationService
{
    /// <summary>
    /// Moves or renames the single note at <paramref name="sourcePath"/>
    /// to <paramref name="destinationPath"/> (a rename is a move within
    /// the same folder - there is no separate code path). Mirrors
    /// <c>POST /api/notes/{**path}/move</c> and the <c>move_note</c> MCP
    /// tool.
    /// </summary>
    Task<NoteMoveResult> MoveNoteAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves or renames the folder at <paramref name="sourcePath"/> - and
    /// every note nested inside it, at any depth - to
    /// <paramref name="destinationPath"/>. Mirrors
    /// <c>POST /api/folders/{**path}/move</c> and the <c>move_folder</c>
    /// MCP tool.
    /// </summary>
    Task<FolderMoveResult> MoveFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
}
