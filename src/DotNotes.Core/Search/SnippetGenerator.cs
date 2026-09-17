using System.Text.RegularExpressions;

namespace DotNotes.Core.Search;

/// <summary>
/// Builds a short excerpt of a note's raw content around a matched search
/// term, for <see cref="SearchResult.Snippet"/> - not the whole note body,
/// per this phase's brief.
/// </summary>
public static class SnippetGenerator
{
    /// <summary>Characters of context kept on each side of the matched term.</summary>
    private const int ContextChars = 60;

    /// <summary>Length of the no-match fallback excerpt (from the start of the note).</summary>
    private const int FallbackLength = ContextChars * 2;

    /// <summary>
    /// Generates a snippet from <paramref name="content"/>, centered on
    /// the first whole-word, case-insensitive occurrence of any token in
    /// <paramref name="matchedTokens"/> (tried in order, so a caller can
    /// pass its matched query tokens most-relevant-first). Runs of
    /// whitespace (including newlines) are collapsed to a single space so
    /// the snippet reads as one line. If none of <paramref name="matchedTokens"/>
    /// literally occurs in <paramref name="content"/> (e.g. the result
    /// matched only via a title-boost, with no body occurrence at all),
    /// falls back to an excerpt from the start of the note.
    /// </summary>
    public static string Generate(string? content, IReadOnlyList<string> matchedTokens)
    {
        var flattened = FlattenWhitespace(content);
        if (flattened.Length == 0)
        {
            return string.Empty;
        }

        foreach (var token in matchedTokens)
        {
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            var match = Regex.Match(
                flattened,
                $@"\b{Regex.Escape(token)}\b",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));

            if (match.Success)
            {
                var start = Math.Max(0, match.Index - ContextChars);
                var end = Math.Min(flattened.Length, match.Index + match.Length + ContextChars);
                return BuildExcerpt(flattened, start, end);
            }
        }

        return BuildExcerpt(flattened, 0, Math.Min(flattened.Length, FallbackLength));
    }

    private static string BuildExcerpt(string flattened, int start, int end)
    {
        var excerpt = flattened[start..end].Trim();
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < flattened.Length ? "…" : string.Empty;
        return prefix + excerpt + suffix;
    }

    private static string FlattenWhitespace(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return Regex.Replace(content, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
    }
}
