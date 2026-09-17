using System.Text;

namespace DotNotes.Core.Search;

/// <summary>
/// Splits raw text (note content, a note's title, or a search query) into
/// lowercase index tokens, per docs/06-DATA-MODEL.md's "Search index"
/// section: split on whitespace/punctuation, lowercase, strip a small
/// stopword list. Used identically at index time (note content/title) and
/// at query time, so a query term always matches the same token a note
/// was indexed under.
/// </summary>
/// <remarks>
/// MVP tokenization, deliberately simple: a "word" is any maximal run of
/// Unicode letters/digits; everything else (whitespace, punctuation,
/// apostrophes, hyphens, markdown syntax characters like <c>#</c>/<c>*</c>/
/// <c>[[</c>/<c>]]</c>) is treated purely as a separator and discarded, not
/// indexed. This means e.g. "don't" tokenizes to <c>["don", "t"]</c> and
/// "front-matter" to <c>["front", "matter"]</c> - a known, acceptable MVP
/// simplification (docs/06-DATA-MODEL.md does not require more, and this
/// is exactly the boundary a future `Lucene.NET` swap would replace with a
/// real analyzer, per <see cref="ISearchIndex"/>'s remarks).
/// </remarks>
public static class Tokenizer
{
    /// <summary>
    /// A short, deliberately non-exhaustive list of common English
    /// function words. This is an MVP relevance tweak (so near-universal
    /// words don't dominate every result's term-frequency score), not a
    /// linguistically complete stopword list.
    /// </summary>
    public static readonly IReadOnlyCollection<string> Stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by",
        "for", "from", "has", "have", "he", "in", "is", "it", "its",
        "of", "on", "or", "that", "the", "this", "to", "was", "were",
        "will", "with",
    };

    /// <summary>
    /// Tokenizes <paramref name="text"/>: lowercase letter/digit runs,
    /// with stopwords removed. Preserves the original order of
    /// appearance, including duplicates (callers that need term
    /// frequencies rely on duplicates being present; callers that need
    /// distinct terms can call <see cref="Enumerable.Distinct{TSource}(IEnumerable{TSource})"/>
    /// themselves).
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                FlushToken(current, tokens);
            }
        }

        FlushToken(current, tokens);
        return tokens;
    }

    private static void FlushToken(StringBuilder current, List<string> tokens)
    {
        if (current.Length == 0)
        {
            return;
        }

        var token = current.ToString();
        current.Clear();

        if (!Stopwords.Contains(token))
        {
            tokens.Add(token);
        }
    }
}
