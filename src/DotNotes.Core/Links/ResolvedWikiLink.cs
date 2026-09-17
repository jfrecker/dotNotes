namespace DotNotes.Core.Links;

/// <summary>
/// The result of resolving a wikilink's raw target text to a concrete,
/// normalized vault-relative note path. See <see cref="WikiLinkResolver"/>.
/// </summary>
/// <param name="TargetPath">
/// Normalized, <c>/</c>-separated vault-relative path ending in
/// <c>.md</c> - a note's identity per docs/06-DATA-MODEL.md, whether or
/// not a note actually exists there yet.
/// </param>
/// <param name="Exists">
/// Whether <paramref name="TargetPath"/> matched a known note in the
/// vault at resolution time (<see langword="true"/>), or is an
/// unresolved/"missing" link target (<see langword="false"/>).
/// </param>
public readonly record struct ResolvedWikiLink(string TargetPath, bool Exists);
