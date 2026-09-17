using DotNotes.Core.Links;

namespace DotNotes.Core.Tests.Links;

public sealed class WikiLinkParserTests
{
    [Fact]
    public void Parse_NoLinks_ReturnsEmpty()
    {
        var result = WikiLinkParser.Parse("Just plain text, no links here.");

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_NullOrEmptyContent_ReturnsEmpty(string? content)
    {
        Assert.Empty(WikiLinkParser.Parse(content));
    }

    [Fact]
    public void Parse_SimpleLink_ReturnsRawTargetWithNoDisplayText()
    {
        var result = WikiLinkParser.Parse("See [[idea]] for more.");

        var link = Assert.Single(result);
        Assert.Equal("idea", link.RawTarget);
        Assert.Null(link.DisplayText);
        Assert.Equal("idea", link.Label);
    }

    [Fact]
    public void Parse_AliasedLink_ReturnsRawTargetAndDisplayText()
    {
        var result = WikiLinkParser.Parse("See [[projects/idea|My Idea]] for more.");

        var link = Assert.Single(result);
        Assert.Equal("projects/idea", link.RawTarget);
        Assert.Equal("My Idea", link.DisplayText);
        Assert.Equal("My Idea", link.Label);
    }

    [Fact]
    public void Parse_NestedFolderPath_ReturnsFullRawTarget()
    {
        var result = WikiLinkParser.Parse("[[projects/nested/deep]]");

        var link = Assert.Single(result);
        Assert.Equal("projects/nested/deep", link.RawTarget);
    }

    [Fact]
    public void Parse_MultipleLinksInOneNote_ReturnsAllInDocumentOrder()
    {
        var result = WikiLinkParser.Parse("[[first]] then [[second|Second Note]] then [[third]].");

        Assert.Equal(3, result.Count);
        Assert.Equal("first", result[0].RawTarget);
        Assert.Equal("second", result[1].RawTarget);
        Assert.Equal("Second Note", result[1].DisplayText);
        Assert.Equal("third", result[2].RawTarget);
    }

    [Fact]
    public void Parse_DuplicateLinkToSameTarget_ReturnsBothOccurrences()
    {
        var result = WikiLinkParser.Parse("[[idea]] ... later again [[idea]]");

        Assert.Equal(2, result.Count);
        Assert.All(result, l => Assert.Equal("idea", l.RawTarget));
    }

    [Fact]
    public void Parse_SelfReferentialLink_IsParsedLikeAnyOtherLink()
    {
        // The parser has no notion of "self" - that's a resolution-time
        // concept (see WikiLinkResolverTests) - it just returns whatever
        // raw text was written.
        var result = WikiLinkParser.Parse("[[this-note]]");

        var link = Assert.Single(result);
        Assert.Equal("this-note", link.RawTarget);
    }

    [Fact]
    public void Parse_PreservesOriginalCasingOfTheRawTarget()
    {
        // The parser itself doesn't case-fold anything - matching
        // case-insensitively against known notes is WikiLinkResolver's
        // job (see WikiLinkResolverTests); the parser must faithfully
        // preserve whatever casing was actually written.
        var result = WikiLinkParser.Parse("[[Projects/IDEA]]");

        var link = Assert.Single(result);
        Assert.Equal("Projects/IDEA", link.RawTarget);
    }

    [Theory]
    [InlineData("[[]]")]
    [InlineData("[[   ]]")]
    [InlineData("[[|OnlyAlias]]")]
    public void Parse_EmptyTargetAfterTrimming_IsSkipped(string malformedLink)
    {
        Assert.Empty(WikiLinkParser.Parse(malformedLink));
    }

    [Fact]
    public void Parse_WhitespaceAroundTargetAndAlias_IsTrimmed()
    {
        var result = WikiLinkParser.Parse("[[  idea  |  My Idea  ]]");

        var link = Assert.Single(result);
        Assert.Equal("idea", link.RawTarget);
        Assert.Equal("My Idea", link.DisplayText);
    }

    [Fact]
    public void Parse_AdjacentLinksOnSameLine_AreNotMergedIntoOneMatch()
    {
        var result = WikiLinkParser.Parse("[[a]][[b]]");

        Assert.Equal(2, result.Count);
        Assert.Equal("a", result[0].RawTarget);
        Assert.Equal("b", result[1].RawTarget);
    }

    [Fact]
    public void Parse_DoesNotMatchSingleBracketMarkdownLinks()
    {
        var result = WikiLinkParser.Parse("[a markdown link](https://example.com)");

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_UnterminatedLink_IsNotMatched()
    {
        var result = WikiLinkParser.Parse("[[unterminated with no closing brackets");

        Assert.Empty(result);
    }
}
