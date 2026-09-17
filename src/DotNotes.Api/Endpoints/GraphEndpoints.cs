using DotNotes.Core.Links;

namespace DotNotes.Api.Endpoints;

/// <summary>
/// Wires up the graph half of docs/04-API-SPEC.md's "Links &amp; graph"
/// section: <c>GET /api/graph</c>. Endpoints here are intentionally thin
/// - all index maintenance/graph-building logic lives in
/// <see cref="ILinkIndex"/> (DotNotes.Core); this file only translates
/// that model to/from HTTP.
/// </summary>
public static class GraphEndpoints
{
    public static WebApplication MapGraphEndpoints(this WebApplication app)
    {
        app.MapGet("/api/graph", GetGraphAsync);

        return app;
    }

    private static Task<IResult> GetGraphAsync(ILinkIndex linkIndex)
    {
        var graph = linkIndex.GetGraph();

        var nodes = graph.Nodes
            .Select(n => new GraphNodeResponse(n.Id, n.Label, n.Exists))
            .ToArray();
        var edges = graph.Edges
            .Select(e => new GraphEdgeResponse(e.Source, e.Target))
            .ToArray();

        return Task.FromResult(Results.Ok(new GraphResponse(nodes, edges)));
    }

    /// <summary>
    /// Backs <c>GET /api/graph</c>. docs/04-API-SPEC.md's example response
    /// shows node shape <c>{ id, label }</c> without `exists`, but
    /// docs/06-DATA-MODEL.md's "Graph model" section requires unresolved
    /// link targets to be flagged `exists: false` - `Exists` is added
    /// here as an additive field (every existing note also gets
    /// `exists: true`), not a breaking change to the documented shape.
    /// </summary>
    private sealed record GraphResponse(IReadOnlyList<GraphNodeResponse> Nodes, IReadOnlyList<GraphEdgeResponse> Edges);

    private sealed record GraphNodeResponse(string Id, string Label, bool Exists);

    private sealed record GraphEdgeResponse(string Source, string Target);
}
