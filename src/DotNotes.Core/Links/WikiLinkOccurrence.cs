namespace DotNotes.Core.Links;

/// <summary>
/// One <c>[[...]]</c> wikilink occurrence found in a note's raw markdown
/// content, before resolution against the vault's known note paths. See
/// docs/06-DATA-MODEL.md's "Wikilink syntax &amp; resolution" section.
/// </summary>
/// <param name="RawTarget">
/// The text between <c>[[</c> and either <c>|</c> or <c>]]</c>, trimmed,
/// exactly as written (e.g. <c>projects/idea</c> or <c>idea.md</c>) -
/// not yet resolved against the vault. See <see cref="WikiLinkResolver"/>.
/// </param>
/// <param name="DisplayText">
/// The pipe-alias text (<c>[[actual-path|Display Text]]</c>), trimmed,
/// or <see langword="null"/> if no alias was given.
/// </param>
public sealed record WikiLinkOccurrence(string RawTarget, string? DisplayText)
{
    /// <summary>
    /// The link's rendered label per docs/06-DATA-MODEL.md: the pipe
    /// alias if one was given, otherwise <see cref="RawTarget"/>'s bare
    /// filename without a <c>.md</c> extension.
    /// </summary>
    public string Label =>
        DisplayText ?? WikiLinkResolver.GetBareTitle(WikiLinkResolver.NormalizeToNotePath(RawTarget));
}
