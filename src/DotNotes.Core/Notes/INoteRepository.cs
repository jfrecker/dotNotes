namespace DotNotes.Core.Notes;

/// <summary>
/// Path-based CRUD over the vault directory. A note's identity is its
/// vault-relative path (e.g. <c>projects/idea.md</c>) — there is no
/// separate ID/GUID, per docs/06-DATA-MODEL.md.
/// </summary>
/// <remarks>
/// This is the contract other code (REST endpoints, later MCP tools)
/// depends on; implementations must never let a caller-supplied path
/// escape the vault root (see <see cref="InvalidNotePathException"/>).
/// </remarks>
public interface INoteRepository
{
    /// <summary>
    /// Lists the entire vault as a tree of files and folders, rooted at
    /// the vault root itself (the returned list is the vault root's
    /// direct children, not a single wrapper node for the root). Backs
    /// <c>GET /api/notes</c>.
    /// </summary>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk (see
    /// remarks on that type).
    /// </exception>
    Task<IReadOnlyList<NoteTreeEntry>> GetTreeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a note's raw content and last-modified timestamp by
    /// vault-relative path. Returns <see langword="null"/> if no note
    /// exists at that path (callers map this to 404). Backs
    /// <c>GET /api/notes/{**path}</c>.
    /// </summary>
    /// <exception cref="InvalidNotePathException">
    /// <paramref name="path"/> is unsafe (see remarks on the interface).
    /// </exception>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk (see
    /// remarks on that type) - deliberately distinct from a
    /// <see langword="null"/> return, which means only this specific note
    /// doesn't exist.
    /// </exception>
    Task<NoteContent?> GetAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a note exists at <paramref name="path"/>, without
    /// reading its content. Useful for 404-vs-create-on-PUT semantics.
    /// </summary>
    /// <exception cref="InvalidNotePathException">
    /// <paramref name="path"/> is unsafe (see remarks on the interface).
    /// </exception>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk (see
    /// remarks on that type) - deliberately distinct from a
    /// <see langword="false"/> return, which means only this specific note
    /// doesn't exist.
    /// </exception>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or overwrites a note's content at vault-relative
    /// <paramref name="path"/>, creating any missing parent folders.
    /// Writes are atomic (temp file + move) so a crash mid-write can
    /// never corrupt an existing note. Backs <c>PUT /api/notes/{**path}</c>.
    /// </summary>
    /// <exception cref="InvalidNotePathException">
    /// <paramref name="path"/> is unsafe (see remarks on the interface), or
    /// - only when this call is *creating* a brand-new note, never when
    /// updating an existing one - a newly-created path segment violates
    /// docs/06-DATA-MODEL.md's "Names" rule (empty/whitespace-only,
    /// leading/trailing whitespace, a control character, a character
    /// invalid for a file name on the host OS, or <c>[</c>/<c>]</c>/<c>|</c>).
    /// </exception>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk (see
    /// remarks on that type). Never falls back to fabricating a new vault
    /// root directory - see that type's remarks for why.
    /// </exception>
    Task<NoteWriteResult> SaveAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the note at vault-relative <paramref name="path"/>, if it
    /// exists. Never deletes now-empty parent folders, per
    /// docs/06-DATA-MODEL.md. Returns <see langword="false"/> (rather
    /// than throwing) if no note existed at that path. Backs
    /// <c>DELETE /api/notes/{**path}</c>.
    /// </summary>
    /// <exception cref="InvalidNotePathException">
    /// <paramref name="path"/> is unsafe (see remarks on the interface).
    /// </exception>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk (see
    /// remarks on that type) - deliberately distinct from a
    /// <see langword="false"/> return, which means only this specific note
    /// doesn't exist.
    /// </exception>
    Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a folder at vault-relative <paramref name="path"/>,
    /// creating any missing intermediate folders too (<c>mkdir -p</c>
    /// semantics). A no-op success (not an error) if the folder already
    /// exists. Backs <c>POST /api/folders/{**path}</c>.
    /// </summary>
    /// <returns>The normalized, <c>/</c>-separated vault-relative path of the created (or already-existing) folder.</returns>
    /// <exception cref="InvalidNotePathException">
    /// <paramref name="path"/> is unsafe (see remarks on the interface and
    /// on <see cref="InvalidNotePathException"/> - folder paths are
    /// validated identically to note paths, minus the <c>.md</c>-extension
    /// requirement), or a newly-created segment (one not already present
    /// on disk - <c>mkdir -p</c> semantics mean some prefix of
    /// <paramref name="path"/> may already exist and is never re-checked)
    /// violates docs/06-DATA-MODEL.md's "Names" rule.
    /// </exception>
    /// <exception cref="DestinationAlreadyExistsException">
    /// A note (a file) already exists at <paramref name="path"/> - a file
    /// and a folder cannot share the same vault-relative path.
    /// </exception>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk (see
    /// remarks on that type). Never falls back to fabricating a new vault
    /// root directory - see that type's remarks for why.
    /// </exception>
    Task<string> CreateFolderAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the folder at vault-relative <paramref name="path"/> and
    /// everything inside it (nested notes, media, and subfolders at any
    /// depth). Returns <see langword="false"/> (rather than throwing) if
    /// no folder exists at that path - including when a *note* (a file)
    /// sits there instead, which is <c>DELETE /api/notes/{**path}</c>'s
    /// job and is never deleted through here. Backs
    /// <c>DELETE /api/folders/{**path}</c>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="MoveFolderAsync"/>, this needs no
    /// <see cref="Reorganization.IVaultReorganizationService"/>
    /// orchestration: a recursive directory delete removes each nested
    /// file individually on disk, so <c>VaultWatcherService</c> sees a
    /// per-file <c>Deleted</c> event for every note in the subtree and
    /// reconciles the link/search indexes on its own - exactly as it does
    /// for <see cref="DeleteAsync"/>. Nothing is rewritten in other notes:
    /// per docs/06-DATA-MODEL.md a wikilink to a deleted note simply
    /// becomes an unresolved link, same as deleting a single note.
    /// <para>
    /// The vault root itself can never be deleted through this method -
    /// an empty/whitespace-only path is rejected as invalid, and every
    /// resolved path is required to sit strictly *inside* the vault root
    /// (see <see cref="MoveFolderAsync"/>'s remarks on path safety).
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidNotePathException">
    /// <paramref name="path"/> is unsafe (see remarks on the interface),
    /// or names the vault root itself.
    /// </exception>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk (see
    /// remarks on that type) - deliberately distinct from a
    /// <see langword="false"/> return, which means only this specific
    /// folder doesn't exist.
    /// </exception>
    Task<bool> DeleteFolderAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves or renames the single note at <paramref name="sourcePath"/>
    /// to <paramref name="destinationPath"/>, auto-creating any missing
    /// destination parent folders (mirroring <see cref="SaveAsync"/>'s
    /// existing convention). Never overwrites an existing destination.
    /// Backs <c>POST /api/notes/{**path}/move</c>.
    /// </summary>
    /// <remarks>
    /// Does not itself rewrite <c>[[wikilinks]]</c> in other notes that
    /// pointed at the old path, and is unaware of
    /// <c>ILinkIndex</c>/<c>ISearchIndex</c> entirely - calling this method
    /// directly leaves those "missing", exactly like a deleted note, and
    /// leaves both indexes to catch up via the file-watcher only. As of
    /// Phase 10, <see cref="Reorganization.IVaultReorganizationService"/>
    /// is the orchestration point that wraps this method with wikilink
    /// rewriting and synchronous index updates per docs/06-DATA-MODEL.md's
    /// "Folder &amp; note move/rename" section; both REST and MCP move a
    /// note through that service, not by calling this method directly.
    /// </remarks>
    /// <exception cref="InvalidNotePathException">
    /// <paramref name="sourcePath"/> or <paramref name="destinationPath"/>
    /// is unsafe (see remarks on the interface), or a newly-created
    /// segment of <paramref name="destinationPath"/> violates
    /// docs/06-DATA-MODEL.md's "Names" rule (the source is never checked,
    /// so an existing note with an otherwise-disallowed name can still be
    /// moved away to a valid one).
    /// </exception>
    /// <exception cref="SourceNotFoundException">
    /// No note exists at <paramref name="sourcePath"/>.
    /// </exception>
    /// <exception cref="DestinationAlreadyExistsException">
    /// A note or folder already exists at <paramref name="destinationPath"/>.
    /// </exception>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk (see
    /// remarks on that type).
    /// </exception>
    Task<NoteWriteResult> MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves or renames the folder at <paramref name="sourcePath"/> - and
    /// everything inside it - to <paramref name="destinationPath"/>,
    /// auto-creating any missing destination parent folders. Never
    /// overwrites an existing destination. Backs
    /// <c>POST /api/folders/{**path}/move</c>.
    /// </summary>
    /// <remarks>
    /// Does not itself rewrite <c>[[wikilinks]]</c> in other notes, and is
    /// deliberately unaware of <c>ILinkIndex</c>/<c>ISearchIndex</c>:
    /// calling this method directly would leave both indexes stale for the
    /// whole subtree, since <c>VaultWatcherService</c>'s <c>Renamed</c>
    /// handler only fires once for the moved directory itself, never per
    /// nested note. As of Phase 10,
    /// <see cref="Reorganization.IVaultReorganizationService"/> is the
    /// orchestration point that wraps this method with wikilink rewriting
    /// and synchronous index updates per docs/06-DATA-MODEL.md's "Folder
    /// &amp; note move/rename" section; both REST and MCP move a folder
    /// through that service, not by calling this method directly.
    /// </remarks>
    /// <exception cref="InvalidNotePathException">
    /// <paramref name="sourcePath"/> or <paramref name="destinationPath"/>
    /// is unsafe (see remarks on the interface), a newly-created segment
    /// of <paramref name="destinationPath"/> violates
    /// docs/06-DATA-MODEL.md's "Names" rule (the source is never checked),
    /// or <paramref name="destinationPath"/> is <paramref name="sourcePath"/>
    /// itself or one of its own descendants (e.g. moving
    /// <c>projects</c> to <c>projects/archive/projects</c>).
    /// </exception>
    /// <exception cref="SourceNotFoundException">
    /// No folder exists at <paramref name="sourcePath"/>.
    /// </exception>
    /// <exception cref="DestinationAlreadyExistsException">
    /// A note or folder already exists at <paramref name="destinationPath"/>.
    /// </exception>
    /// <exception cref="VaultUnavailableException">
    /// The vault root directory does not currently exist on disk (see
    /// remarks on that type).
    /// </exception>
    /// <returns>The normalized, <c>/</c>-separated vault-relative destination path.</returns>
    Task<string> MoveFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
}
