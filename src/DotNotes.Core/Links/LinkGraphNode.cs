namespace DotNotes.Core.Links;

/// <summary>
/// One node in <see cref="LinkGraph"/>: a note path that either exists on
/// disk, or is an unresolved ("missing", not-yet-created) wikilink
/// target that at least one existing note currently links to. See
/// docs/06-DATA-MODEL.md's "Graph model" section.
/// </summary>
public sealed class LinkGraphNode
{
    /// <summary>Vault-relative note path - a note's identity, per docs/06-DATA-MODEL.md.</summary>
    public required string Id { get; init; }

    /// <summary>Display label: the node's filename without the <c>.md</c> extension.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Whether a note actually exists at <see cref="Id"/> right now.
    /// <see langword="false"/> flags an unresolved wikilink target that
    /// has never been created (or has since been deleted) but is still
    /// linked to from at least one existing note.
    /// </summary>
    public required bool Exists { get; init; }
}
