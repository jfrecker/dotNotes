namespace DotNotes.Core.Links;

/// <summary>
/// A single <c>[[...]]</c> wikilink occurrence located precisely within
/// its source text, for rewrite logic (docs/06-DATA-MODEL.md's "Folder
/// &amp; note move/rename" section) that needs to replace exactly the
/// target portion of an occurrence and nothing else - leaving surrounding
/// whitespace, any pipe alias, and the brackets themselves byte-identical.
/// See <see cref="WikiLinkParser.ParseWithPositions"/>. Deliberately a
/// separate type from <see cref="WikiLinkOccurrence"/> (used by
/// <see cref="WikiLinkParser.Parse"/>) rather than an addition to it, so
/// the existing indexes and their tests - which depend on
/// <see cref="WikiLinkOccurrence"/>'s exact shape - are unaffected.
/// </summary>
/// <param name="TargetStart">
/// The zero-based index, within the original source text, of the first
/// character of the *trimmed* raw target - i.e. exactly where
/// <paramref name="RawTarget"/> begins, skipping any inner whitespace
/// between <c>[[</c> (or a preceding <c>|</c>, for the alias case - not
/// applicable here since this always refers to the target, not the alias)
/// and the target text itself.
/// </param>
/// <param name="TargetLength">
/// The length, in UTF-16 code units, of <paramref name="RawTarget"/> at
/// <paramref name="TargetStart"/> - together these give the exact span a
/// rewrite should replace with a new target, leaving everything else in
/// the source text (including inner whitespace, a pipe alias, and the
/// surrounding <c>[[</c>/<c>]]</c>) untouched.
/// </param>
/// <param name="RawTarget">Same meaning as <see cref="WikiLinkOccurrence.RawTarget"/>.</param>
/// <param name="DisplayText">Same meaning as <see cref="WikiLinkOccurrence.DisplayText"/>.</param>
/// <param name="IsCode">
/// <see langword="true"/> if this occurrence sits inside a fenced code
/// block (<c>```</c> or <c>~~~</c>, closed by a matching fence of the same
/// character and at least the same length, or running to end of document
/// if unclosed) or an inline code span (a backtick run closed by a run of
/// exactly equal length) - per docs/06-DATA-MODEL.md, such an occurrence
/// must never be rewritten on a move/rename.
/// </param>
public sealed record WikiLinkPosition(int TargetStart, int TargetLength, string RawTarget, string? DisplayText, bool IsCode);
