namespace DotNotes.Core.Links;

/// <summary>
/// One deduplicated <c>(sourcePath, targetPath)</c> wikilink edge in
/// <see cref="LinkGraph"/>, per docs/06-DATA-MODEL.md's "Graph model"
/// section.
/// </summary>
public sealed class LinkGraphEdge
{
    /// <summary>The note containing the <c>[[wikilink]]</c>.</summary>
    public required string Source { get; init; }

    /// <summary>The wikilink's resolved target note path.</summary>
    public required string Target { get; init; }
}
