using System.Text.RegularExpressions;

namespace DotNotes.Core.Links;

/// <summary>
/// Recognizes <c>[[path]]</c> and <c>[[path|Display Text]]</c> wikilinks
/// inside raw markdown content, per docs/06-DATA-MODEL.md's "Wikilink
/// syntax &amp; resolution" section. This is purely textual - it does
/// not know which notes actually exist in the vault; see
/// <see cref="WikiLinkResolver"/> for resolving a parsed occurrence's
/// <see cref="WikiLinkOccurrence.RawTarget"/> against the vault's known
/// note paths.
/// </summary>
public static class WikiLinkParser
{
    // Group 1: everything between "[[" and either "|" or "]]" - the raw
    // target text as written. Group 2 (optional): everything between "|"
    // and "]]" - the pipe-alias display text. Excluding '[' and ']' from
    // both groups keeps adjacent links ("[[a]] [[b]]") on the same line
    // from being merged into one greedy match, and keeps this from
    // reaching across a markdown link/image ("[text](url)") that happens
    // to sit next to a wikilink.
    private static readonly Regex WikiLinkPattern = new(
        @"\[\[([^\[\]|]+)(?:\|([^\[\]]+))?\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds every wikilink occurrence in <paramref name="content"/>, in
    /// document order. Returns an empty list for null/empty content, or
    /// content with no wikilinks. An occurrence whose target is empty
    /// after trimming (e.g. <c>[[ ]]</c> or <c>[[|Alias]]</c>) is
    /// skipped, since there is nothing to resolve it against.
    /// </summary>
    public static IReadOnlyList<WikiLinkOccurrence> Parse(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<WikiLinkOccurrence>();
        }

        List<WikiLinkOccurrence>? occurrences = null;
        foreach (Match match in WikiLinkPattern.Matches(content))
        {
            var rawTarget = match.Groups[1].Value.Trim();
            if (rawTarget.Length == 0)
            {
                continue;
            }

            var displayText = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
            occurrences ??= [];
            occurrences.Add(new WikiLinkOccurrence(rawTarget, string.IsNullOrEmpty(displayText) ? null : displayText));
        }

        return occurrences ?? (IReadOnlyList<WikiLinkOccurrence>)Array.Empty<WikiLinkOccurrence>();
    }

    /// <summary>
    /// Same recognition rules as <see cref="Parse"/>, but additionally
    /// reports each occurrence's exact source position (see
    /// <see cref="WikiLinkPosition"/>) and whether it sits inside a fenced
    /// code block or inline code span. Used by the Phase 10 move/rename
    /// wikilink-rewrite orchestration, which needs to replace exactly one
    /// occurrence's target text without disturbing anything else in the
    /// source. Does not change <see cref="Parse"/>'s behavior or signature;
    /// the two methods are independent, parallel entry points over the
    /// same underlying pattern.
    /// </summary>
    public static IReadOnlyList<WikiLinkPosition> ParseWithPositions(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<WikiLinkPosition>();
        }

        var codeRanges = FindCodeRanges(content);

        List<WikiLinkPosition>? positions = null;
        foreach (Match match in WikiLinkPattern.Matches(content))
        {
            var rawGroup = match.Groups[1];
            var rawValue = rawGroup.Value;
            var trimmedLeadingLength = rawValue.Length - rawValue.TrimStart().Length;
            var rawTarget = rawValue.Trim();
            if (rawTarget.Length == 0)
            {
                continue;
            }

            var targetStart = rawGroup.Index + trimmedLeadingLength;

            var displayGroup = match.Groups[2];
            var displayText = displayGroup.Success ? displayGroup.Value.Trim() : null;

            var isCode = IsWithinAnyRange(codeRanges, targetStart);

            positions ??= [];
            positions.Add(new WikiLinkPosition(
                targetStart,
                rawTarget.Length,
                rawTarget,
                string.IsNullOrEmpty(displayText) ? null : displayText,
                isCode));
        }

        return positions ?? (IReadOnlyList<WikiLinkPosition>)Array.Empty<WikiLinkPosition>();
    }

    /// <summary>
    /// Every code region in <paramref name="content"/> - fenced code
    /// blocks plus inline code spans found outside them - as a list of
    /// half-open <c>[Start, End)</c> character ranges. A target position
    /// inside any of these ranges must never be rewritten, per
    /// docs/06-DATA-MODEL.md.
    /// </summary>
    private static List<(int Start, int End)> FindCodeRanges(string content)
    {
        var ranges = FindFencedCodeBlockRanges(content);
        ranges.AddRange(FindInlineCodeSpanRanges(content, ranges));
        return ranges;
    }

    /// <summary>
    /// Finds every fenced code block (<c>```</c> or <c>~~~</c>) in
    /// <paramref name="content"/>, line by line. A fence opens on a line
    /// whose content, after trimming leading whitespace, starts with a run
    /// of three or more of the same fence character; it closes on the next
    /// such line whose fence run is the same character and *at least* as
    /// long, with nothing but whitespace after it. An opened-but-never-closed
    /// fence runs to the end of the document. Each returned range spans
    /// from the start of the opening fence's line to the end of the closing
    /// fence's line (or end of document, if unclosed).
    /// </summary>
    private static List<(int Start, int End)> FindFencedCodeBlockRanges(string content)
    {
        var ranges = new List<(int Start, int End)>();
        var length = content.Length;
        var pos = 0;

        while (pos < length)
        {
            var lineEnd = content.IndexOf('\n', pos);
            var lineEndExclusive = lineEnd < 0 ? length : lineEnd;
            var line = content[pos..lineEndExclusive];
            var trimmedLine = line.TrimStart();

            if (trimmedLine.Length >= 3 && (trimmedLine[0] == '`' || trimmedLine[0] == '~'))
            {
                var fenceChar = trimmedLine[0];
                var fenceLength = 0;
                while (fenceLength < trimmedLine.Length && trimmedLine[fenceLength] == fenceChar)
                {
                    fenceLength++;
                }

                if (fenceLength >= 3)
                {
                    var blockStart = pos;
                    var scanPos = lineEnd < 0 ? length : lineEnd + 1;
                    var closed = false;

                    while (scanPos <= length && !closed)
                    {
                        if (scanPos == length)
                        {
                            break;
                        }

                        var candidateLineEnd = content.IndexOf('\n', scanPos);
                        var candidateLineEndExclusive = candidateLineEnd < 0 ? length : candidateLineEnd;
                        var candidateLine = content[scanPos..candidateLineEndExclusive];
                        var candidateTrimmed = candidateLine.TrimStart();

                        if (candidateTrimmed.Length > 0 && candidateTrimmed[0] == fenceChar)
                        {
                            var candidateFenceLength = 0;
                            while (candidateFenceLength < candidateTrimmed.Length && candidateTrimmed[candidateFenceLength] == fenceChar)
                            {
                                candidateFenceLength++;
                            }

                            var remainder = candidateTrimmed[candidateFenceLength..].Trim();
                            if (candidateFenceLength >= fenceLength && remainder.Length == 0)
                            {
                                ranges.Add((blockStart, candidateLineEndExclusive));
                                pos = candidateLineEnd < 0 ? length : candidateLineEnd + 1;
                                closed = true;
                                break;
                            }
                        }

                        scanPos = candidateLineEnd < 0 ? length : candidateLineEnd + 1;
                    }

                    if (!closed)
                    {
                        ranges.Add((blockStart, length));
                        pos = length;
                    }

                    continue;
                }
            }

            pos = lineEnd < 0 ? length : lineEnd + 1;
        }

        return ranges;
    }

    /// <summary>
    /// Finds every inline code span (a run of one or more backticks,
    /// closed by the next run of *exactly* the same length) in
    /// <paramref name="content"/>, skipping any backtick that already sits
    /// inside a fenced code block per <paramref name="fencedRanges"/>. An
    /// opening run with no matching closing run of equal length is left
    /// alone (not treated as code), matching standard Markdown inline-code
    /// semantics: unmatched backticks are literal text.
    /// </summary>
    private static List<(int Start, int End)> FindInlineCodeSpanRanges(string content, List<(int Start, int End)> fencedRanges)
    {
        var ranges = new List<(int Start, int End)>();
        var length = content.Length;
        var i = 0;

        while (i < length)
        {
            if (content[i] != '`' || IsWithinAnyRange(fencedRanges, i))
            {
                i++;
                continue;
            }

            var runStart = i;
            var runLength = 0;
            while (i < length && content[i] == '`')
            {
                i++;
                runLength++;
            }

            var searchPos = i;
            var closeStart = -1;
            var closeLength = 0;

            while (searchPos < length)
            {
                if (content[searchPos] != '`' || IsWithinAnyRange(fencedRanges, searchPos))
                {
                    searchPos++;
                    continue;
                }

                var candidateStart = searchPos;
                var candidateLength = 0;
                while (searchPos < length && content[searchPos] == '`')
                {
                    searchPos++;
                    candidateLength++;
                }

                if (candidateLength == runLength)
                {
                    closeStart = candidateStart;
                    closeLength = candidateLength;
                    break;
                }
            }

            if (closeStart >= 0)
            {
                ranges.Add((runStart, closeStart + closeLength));
            }
        }

        return ranges;
    }

    private static bool IsWithinAnyRange(List<(int Start, int End)> ranges, int position)
    {
        foreach (var (start, end) in ranges)
        {
            if (position >= start && position < end)
            {
                return true;
            }
        }

        return false;
    }
}
