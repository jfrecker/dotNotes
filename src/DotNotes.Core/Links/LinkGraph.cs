namespace DotNotes.Core.Links;

/// <summary>
/// The full node/edge graph over the vault's wikilinks, as returned by
/// <see cref="ILinkIndex.GetGraph"/>. Backs <c>GET /api/graph</c> per
/// docs/04-API-SPEC.md; layout (force-directed positioning) happens
/// client-side, per docs/06-DATA-MODEL.md - this is just the raw graph
/// data.
/// </summary>
public sealed class LinkGraph
{
    public required IReadOnlyList<LinkGraphNode> Nodes { get; init; }

    public required IReadOnlyList<LinkGraphEdge> Edges { get; init; }
}
