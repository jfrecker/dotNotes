namespace DotNotes.Core.Vault;

/// <summary>
/// A derived-cache index that needs to be kept live as notes are created,
/// edited, or deleted on disk. Implemented by both
/// <see cref="Links.ILinkIndex"/> and <see cref="Search.ISearchIndex"/> so
/// that a single shared vault watcher can fan settled file-watcher events
/// out to every registered index, instead of each index owning its own
/// <see cref="System.IO.FileSystemWatcher"/> - see
/// docs/02-ARCHITECTURE.md's "a single FileSystemWatcher feeds both the
/// link index and the search index" request-flow note, and
/// <see cref="Links.VaultWatcherService"/> for the watcher that drives
/// this in practice.
/// </summary>
/// <remarks>
/// This interface intentionally mirrors the incremental-update methods
/// <see cref="Links.ILinkIndex"/> already had before this interface
/// existed (<c>NoteChanged</c>/<c>NoteDeleted</c>), so introducing it does
/// not change either method's signature - only which interface(s)
/// formally declare them. <see cref="Links.ILinkIndex"/> and
/// <see cref="Search.ISearchIndex"/> both extend this interface rather
/// than duplicating these two members themselves.
/// </remarks>
public interface IVaultChangeListener
{
    /// <summary>
    /// Notifies the listener that the note at <paramref name="path"/> was
    /// created, or that its content changed. Implementations should
    /// re-derive whatever they track from <paramref name="content"/> and
    /// replace only what changed for this one path - this must be cheap
    /// enough to run off the request path (e.g. from a debounced
    /// file-watcher callback) without introducing noticeable UI lag on
    /// save.
    /// </summary>
    void NoteChanged(string path, string content);

    /// <summary>
    /// Notifies the listener that the note at <paramref name="path"/> was
    /// deleted: implementations should remove whatever they had indexed
    /// for this path.
    /// </summary>
    void NoteDeleted(string path);
}
