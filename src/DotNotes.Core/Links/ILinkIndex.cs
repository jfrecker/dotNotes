using DotNotes.Core.Vault;

namespace DotNotes.Core.Links;

/// <summary>
/// In-memory backlink/graph index over the vault's wikilinks - a derived
/// cache built from the notes on disk (via <see cref="Notes.INoteRepository"/>),
/// never a source of truth. Safe to discard and rebuild in full at any
/// time via <see cref="RebuildAsync"/>, per CLAUDE.md's "index is a
/// derived cache" hard rule.
/// </summary>
/// <remarks>
/// Implementations must be safe to call from multiple threads
/// concurrently: HTTP request threads read via <see cref="GetBacklinks"/>
/// / <see cref="GetGraph"/> while a background file-watcher thread calls
/// <see cref="IVaultChangeListener.NoteChanged"/> /
/// <see cref="IVaultChangeListener.NoteDeleted"/> at the same time. This
/// extends <see cref="IVaultChangeListener"/> (rather than declaring
/// <c>NoteChanged</c>/<c>NoteDeleted</c> itself) so that
/// <see cref="Links.VaultWatcherService"/> can fan out to this index and
/// <see cref="Search.ISearchIndex"/> alike from one shared watcher pass -
/// see that interface's remarks.
/// </remarks>
public interface ILinkIndex : IVaultChangeListener
{
    /// <summary>
    /// Performs a full scan of every note in the vault (via
    /// <see cref="Notes.INoteRepository"/>), discarding all current
    /// in-memory state and rebuilding it from scratch. Called once at
    /// startup; also safe to call again at any time (e.g. to recover
    /// from a missed or failed file-watcher event).
    /// </summary>
    Task RebuildAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The notes that link to <paramref name="targetPath"/> (every
    /// source path currently recorded against that target in the
    /// backlink index), or an empty list if nothing currently links to
    /// it. <paramref name="targetPath"/> is normalized and matched
    /// case-insensitively, so callers may pass it with or without a
    /// <c>.md</c> extension.
    /// </summary>
    IReadOnlyList<string> GetBacklinks(string targetPath);

    /// <summary>
    /// Builds the full node/edge graph backing <c>GET /api/graph</c>: one
    /// node per known note path plus one per still-referenced but
    /// missing (not-yet-created, or since-deleted) link target, and one
    /// deduplicated edge per <c>(sourcePath, targetPath)</c> pair.
    /// </summary>
    LinkGraph GetGraph();

    // NoteChanged(string path, string content) / NoteDeleted(string path)
    // are declared by the base IVaultChangeListener interface, not
    // re-declared here. For this index specifically: NoteChanged
    // re-parses `content`'s wikilinks and diffs them against whatever was
    // previously recorded for this path (nothing, for a brand-new note),
    // updating only what changed; NoteDeleted removes the deleted note's
    // outgoing links but leaves it resolvable as a "missing"
    // (`exists: false`) backlink target if other notes still link to it.
    // A rename is expected to be reported as a NoteDeleted call for the
    // old path plus a NoteChanged call for the new path - per
    // docs/06-DATA-MODEL.md, links in other notes are not rewritten. See
    // InMemoryLinkIndex's own NoteChanged/NoteDeleted doc comments for the
    // full behavioral contract.
}
