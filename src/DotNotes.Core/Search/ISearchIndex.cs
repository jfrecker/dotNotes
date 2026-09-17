using DotNotes.Core.Vault;

namespace DotNotes.Core.Search;

/// <summary>
/// Full-text search index over the vault's notes - a derived cache built
/// from the notes on disk (via <see cref="Notes.INoteRepository"/>), never
/// a source of truth, per CLAUDE.md's "index is a derived cache" hard
/// rule. Mirrors <see cref="Links.ILinkIndex"/>'s shape for consistency:
/// a full-rebuild operation, plus incremental update-on-change /
/// remove-on-delete (via the shared <see cref="IVaultChangeListener"/>
/// base interface) so both indexes can be kept live from the same vault
/// watcher pass.
/// </summary>
/// <remarks>
/// <para>
/// This interface deliberately does not expose the underlying token/
/// posting-list data structure to callers (docs/06-DATA-MODEL.md's
/// "Search index" section) - <see cref="Search"/> returns fully-formed
/// <see cref="SearchResult"/> values. That boundary is what lets
/// <see cref="InMemorySearchIndex"/> be swapped for a `Lucene.NET`-backed
/// implementation later without changing `SearchEndpoints.cs` or the MCP
/// `search_notes` tool at all.
/// </para>
/// <para>
/// Implementations must be safe to call from multiple threads
/// concurrently: HTTP request threads call <see cref="Search"/> while a
/// background file-watcher thread calls
/// <see cref="IVaultChangeListener.NoteChanged"/> /
/// <see cref="IVaultChangeListener.NoteDeleted"/> at the same time.
/// </para>
/// </remarks>
public interface ISearchIndex : IVaultChangeListener
{
    /// <summary>
    /// Performs a full scan of every note in the vault (via
    /// <see cref="Notes.INoteRepository"/>), discarding all current
    /// in-memory state and re-tokenizing everything from scratch. Safe to
    /// call at any time (e.g. to recover from a missed or failed
    /// file-watcher event) - see also <see cref="Links.VaultWatcherService"/>'s
    /// remarks on how the *initial* startup scan is actually performed
    /// without each index independently re-reading every file.
    /// </summary>
    Task RebuildAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the index for <paramref name="query"/>, returning up to
    /// <paramref name="limit"/> results ordered by descending relevance
    /// (see <see cref="SearchResult.Score"/>), ties broken by path for a
    /// stable order. Tokenizes <paramref name="query"/> the same way note
    /// content is tokenized at index time (whitespace/punctuation split,
    /// lowercased, stopwords removed); a query that tokenizes to nothing
    /// (empty, whitespace-only, or entirely stopwords) returns an empty
    /// list rather than throwing.
    /// </summary>
    /// <param name="query">Raw, un-tokenized search text.</param>
    /// <param name="limit">
    /// Maximum number of results to return. Must be positive - callers
    /// (e.g. <c>SearchEndpoints.cs</c>) are responsible for defaulting/
    /// clamping whatever a caller-supplied value was, per
    /// docs/04-API-SPEC.md's Search section.
    /// </param>
    IReadOnlyList<SearchResult> Search(string query, int limit);
}
