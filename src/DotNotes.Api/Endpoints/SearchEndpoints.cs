using DotNotes.Core.Search;

namespace DotNotes.Api.Endpoints;

/// <summary>
/// Wires up docs/04-API-SPEC.md's Search section:
/// <c>GET /api/search?q={query}&amp;limit={n}</c>. Endpoints here are
/// intentionally thin - all tokenizing/scoring/snippet logic lives in
/// <see cref="ISearchIndex"/> (DotNotes.Core); this file only translates
/// HTTP query-string in/out to/from that contract.
/// </summary>
public static class SearchEndpoints
{
    /// <summary>Default number of results when <c>limit</c> is omitted (or not a positive integer).</summary>
    internal const int DefaultLimit = 20;

    /// <summary>Upper bound on <c>limit</c>, regardless of what a caller requests.</summary>
    internal const int MaxLimit = 100;

    public static WebApplication MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", GetSearchResults);

        return app;
    }

    private static IResult GetSearchResults(string? q, int? limit, ISearchIndex searchIndex)
    {
        // Judgment call (docs/04-API-SPEC.md doesn't distinguish): a
        // missing/blank `q` returns an empty result array (200 OK) rather
        // than a 400. This keeps a live "search as you type" frontend
        // simple - clearing the search box just empties the results,
        // with no error state to special-case - and ISearchIndex.Search
        // already treats a query that tokenizes to nothing the same way.
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.Ok(Array.Empty<SearchResultResponse>());
        }

        // Judgment call: `limit` defaults to DefaultLimit when omitted or
        // not a positive integer, and is clamped to MaxLimit when a
        // caller asks for more - a search box has no legitimate reason to
        // request an unbounded number of results, and this keeps a single
        // request's cost bounded regardless of vault size.
        var effectiveLimit = limit is > 0 ? Math.Min(limit.Value, MaxLimit) : DefaultLimit;

        var results = searchIndex.Search(q, effectiveLimit);

        return Results.Ok(results
            .Select(r => new SearchResultResponse(r.Path, r.Title, r.Snippet, r.Score))
            .ToArray());
    }

    /// <summary>Backs <c>GET /api/search</c>'s <c>[{ path, title, snippet, score }]</c> response shape.</summary>
    private sealed record SearchResultResponse(string Path, string Title, string Snippet, double Score);
}
