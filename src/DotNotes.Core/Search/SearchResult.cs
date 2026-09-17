namespace DotNotes.Core.Search;

/// <summary>
/// One ranked hit returned by <see cref="ISearchIndex.Search"/>. Backs
/// <c>GET /api/search</c>'s <c>[{ path, title, snippet, score }]</c>
/// response shape per docs/04-API-SPEC.md.
/// </summary>
public sealed class SearchResult
{
    /// <summary>Vault-relative path, e.g. <c>projects/idea.md</c> - the note's identity per docs/06-DATA-MODEL.md.</summary>
    public required string Path { get; init; }

    /// <summary>Display title: the note's filename without its <c>.md</c> extension.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// A short excerpt from the note's body around a matched query term
    /// (not the whole note content) - see <see cref="SnippetGenerator"/>.
    /// </summary>
    public required string Snippet { get; init; }

    /// <summary>
    /// Relevance score for this result, higher is more relevant. Only
    /// meaningful relative to other results for the *same* query - see
    /// <see cref="InMemorySearchIndex"/>'s remarks for the current scoring
    /// formula. Not guaranteed to be stable across implementations (e.g.
    /// a future Lucene.NET-backed <see cref="ISearchIndex"/> would use its
    /// own scale).
    /// </summary>
    public required double Score { get; init; }
}
