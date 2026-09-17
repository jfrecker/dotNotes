using DotNotes.Core.Search;

namespace DotNotes.Core.Tests.Search;

/// <summary>
/// Covers <see cref="SnippetGenerator.Generate"/> in isolation: it should
/// produce a short excerpt around a matched term, not the whole note
/// body, per this phase's brief.
/// </summary>
public sealed class SnippetGeneratorTests
{
    [Fact]
    public void Generate_ShortNote_ReturnsWholeNoteWithNoEllipses()
    {
        var snippet = SnippetGenerator.Generate("A short note about idea.", ["idea"]);

        Assert.Equal("A short note about idea.", snippet);
    }

    [Fact]
    public void Generate_LongNote_ReturnsExcerptAroundTheMatchNotTheWholeBody()
    {
        var padding = string.Concat(Enumerable.Repeat("filler word here. ", 20));
        var content = padding + "the important idea appears right here." + padding;

        var snippet = SnippetGenerator.Generate(content, ["idea"]);

        Assert.True(snippet.Length < content.Length);
        Assert.Contains("idea", snippet);
    }

    [Fact]
    public void Generate_TruncatedOnLeft_HasLeadingEllipsis()
    {
        var padding = string.Concat(Enumerable.Repeat("filler word here. ", 20));
        var content = padding + "idea";

        var snippet = SnippetGenerator.Generate(content, ["idea"]);

        Assert.StartsWith("…", snippet);
    }

    [Fact]
    public void Generate_TruncatedOnRight_HasTrailingEllipsis()
    {
        var padding = string.Concat(Enumerable.Repeat("filler word here. ", 20));
        var content = "idea" + padding;

        var snippet = SnippetGenerator.Generate(content, ["idea"]);

        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void Generate_MatchesWholeWordOnly_NotASubstringOfALongerWord()
    {
        // "cat" must not "match" inside "category" - FindWholeWordIndex
        // (via \b word boundaries) should skip past it to the real match.
        var content = "This note is filed under category, but also mentions a cat directly.";

        var snippet = SnippetGenerator.Generate(content, ["cat"]);

        Assert.Contains("cat directly", snippet);
    }

    [Fact]
    public void Generate_IsCaseInsensitive()
    {
        var snippet = SnippetGenerator.Generate("The IDEA is here.", ["idea"]);

        Assert.Contains("IDEA", snippet);
    }

    [Fact]
    public void Generate_CollapsesNewlinesAndExtraWhitespace()
    {
        var content = "line one\n\n   line two   with   idea\nline three";

        var snippet = SnippetGenerator.Generate(content, ["idea"]);

        Assert.DoesNotContain('\n', snippet);
        Assert.DoesNotContain("  ", snippet);
    }

    [Fact]
    public void Generate_NoMatchedTokenFoundInContent_FallsBackToStartOfNote()
    {
        // e.g. a pure title-boost match, with the query term never
        // occurring in the body at all.
        var content = "This note's body never mentions the query term.";

        var snippet = SnippetGenerator.Generate(content, ["nonexistentterm"]);

        Assert.StartsWith("This note's body", snippet);
    }

    [Fact]
    public void Generate_TriesEachMatchedTokenInOrderUntilOneIsFound()
    {
        var content = "This body only contains the word alpha, nothing else relevant.";

        var snippet = SnippetGenerator.Generate(content, ["missing", "alpha"]);

        Assert.Contains("alpha", snippet);
    }

    [Fact]
    public void Generate_EmptyContent_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, SnippetGenerator.Generate("", ["idea"]));
        Assert.Equal(string.Empty, SnippetGenerator.Generate(null, ["idea"]));
    }
}
