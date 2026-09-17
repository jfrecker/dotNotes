using DotNotes.Core.Links;

namespace DotNotes.Core.Tests.Links;

/// <summary>
/// Tests for <see cref="WikiLinkParser.ParseWithPositions"/> - the
/// span-aware, code-block-aware parsing API added for Phase 10's
/// move/rename wikilink rewriting. <see cref="WikiLinkParser.Parse"/>
/// itself is unchanged and remains covered by <see cref="WikiLinkParserTests"/>.
/// </summary>
public sealed class WikiLinkParserPositionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseWithPositions_NullOrEmptyContent_ReturnsEmpty(string? content)
    {
        Assert.Empty(WikiLinkParser.ParseWithPositions(content));
    }

    [Fact]
    public void ParseWithPositions_NoLinks_ReturnsEmpty()
    {
        Assert.Empty(WikiLinkParser.ParseWithPositions("Just plain text, no links here."));
    }

    [Fact]
    public void ParseWithPositions_SimpleLink_TargetSpanIsExact()
    {
        const string content = "See [[idea]] for more.";
        var result = WikiLinkParser.ParseWithPositions(content);

        var link = Assert.Single(result);
        Assert.Equal("idea", link.RawTarget);
        Assert.Null(link.DisplayText);
        Assert.False(link.IsCode);

        var extracted = content.Substring(link.TargetStart, link.TargetLength);
        Assert.Equal("idea", extracted);
    }

    [Fact]
    public void ParseWithPositions_TargetSpanExcludesInnerWhitespace()
    {
        const string content = "[[  idea  |  My Idea  ]]";
        var result = WikiLinkParser.ParseWithPositions(content);

        var link = Assert.Single(result);
        Assert.Equal("idea", link.RawTarget);
        Assert.Equal("My Idea", link.DisplayText);

        var extracted = content.Substring(link.TargetStart, link.TargetLength);
        Assert.Equal("idea", extracted);

        // Replacing exactly [TargetStart, TargetStart+TargetLength) with a
        // new target must leave every surrounding character - including
        // the inner whitespace and the alias - byte-identical.
        var rewritten = content[..link.TargetStart] + "new/idea" + content[(link.TargetStart + link.TargetLength)..];
        Assert.Equal("[[  new/idea  |  My Idea  ]]", rewritten);
    }

    [Fact]
    public void ParseWithPositions_AliasedLink_ReportsDisplayTextButSpanCoversOnlyTarget()
    {
        const string content = "See [[projects/idea|My Idea]] for more.";
        var result = WikiLinkParser.ParseWithPositions(content);

        var link = Assert.Single(result);
        Assert.Equal("projects/idea", link.RawTarget);
        Assert.Equal("My Idea", link.DisplayText);
        Assert.Equal("projects/idea", content.Substring(link.TargetStart, link.TargetLength));
    }

    [Fact]
    public void ParseWithPositions_AdjacentLinks_EachHasItsOwnCorrectSpan()
    {
        const string content = "[[a]][[b]]";
        var result = WikiLinkParser.ParseWithPositions(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("a", content.Substring(result[0].TargetStart, result[0].TargetLength));
        Assert.Equal("b", content.Substring(result[1].TargetStart, result[1].TargetLength));
    }

    [Fact]
    public void ParseWithPositions_MultipleLinks_AreInDocumentOrder()
    {
        const string content = "[[first]] then [[second|Second Note]] then [[third]].";
        var result = WikiLinkParser.ParseWithPositions(content);

        Assert.Equal(3, result.Count);
        Assert.True(result[0].TargetStart < result[1].TargetStart);
        Assert.True(result[1].TargetStart < result[2].TargetStart);
    }

    // ---- Fenced code blocks ----

    [Fact]
    public void ParseWithPositions_LinkInsideBacktickFence_IsFlaggedAsCode()
    {
        const string content = "before\n```\ncode [[x]]\n```\nafter [[y]]";
        var result = WikiLinkParser.ParseWithPositions(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("x", result[0].RawTarget);
        Assert.True(result[0].IsCode);
        Assert.Equal("y", result[1].RawTarget);
        Assert.False(result[1].IsCode);
    }

    [Fact]
    public void ParseWithPositions_LinkInsideTildeFence_IsFlaggedAsCode()
    {
        const string content = "before\n~~~\ncode [[x]]\n~~~\nafter [[y]]";
        var result = WikiLinkParser.ParseWithPositions(content);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsCode);
        Assert.False(result[1].IsCode);
    }

    [Fact]
    public void ParseWithPositions_UnclosedFence_RunsToEndOfDocument()
    {
        const string content = "before [[outside]]\n```\ncode [[x]]\nstill code [[z]]";
        var result = WikiLinkParser.ParseWithPositions(content);

        Assert.Equal(3, result.Count);
        Assert.False(result[0].IsCode); // "outside", before the fence
        Assert.True(result[1].IsCode); // "x"
        Assert.True(result[2].IsCode); // "z" - still inside the unclosed fence
    }

    [Fact]
    public void ParseWithPositions_ClosingFenceMustBeAtLeastAsLongAsOpening_NestedShorterFenceDoesNotClose()
    {
        // A 4-backtick fence containing a literal 3-backtick line (a
        // common technique for showing fenced code inside documentation)
        // must not be closed by that shorter, nested fence.
        const string content = "````\n[[a]]\n```\n[[b]]\n````\n[[c]]";
        var result = WikiLinkParser.ParseWithPositions(content);

        Assert.Equal(3, result.Count);
        Assert.True(result[0].IsCode); // "a"
        Assert.True(result[1].IsCode); // "b" - the nested ``` doesn't close the ```` fence
        Assert.False(result[2].IsCode); // "c" - after the real closing ````
    }

    // ---- Inline code spans ----

    [Fact]
    public void ParseWithPositions_LinkInsideInlineCodeSpan_IsFlaggedAsCode()
    {
        const string content = "Some `code with [[link]] inside` and [[outside]].";
        var result = WikiLinkParser.ParseWithPositions(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("link", result[0].RawTarget);
        Assert.True(result[0].IsCode);
        Assert.Equal("outside", result[1].RawTarget);
        Assert.False(result[1].IsCode);
    }

    [Fact]
    public void ParseWithPositions_InlineCodeSpanClosedByEqualLengthRun_LongerRunDoesNotClose()
    {
        // A double-backtick span containing a single backtick must be
        // closed by another double-backtick run, not by the lone backtick.
        const string content = "``code ` with [[link]] backtick`` and [[outside]]";
        var result = WikiLinkParser.ParseWithPositions(content);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsCode);
        Assert.False(result[1].IsCode);
    }

    [Fact]
    public void ParseWithPositions_UnmatchedInlineBacktick_IsNotTreatedAsCode()
    {
        const string content = "a stray ` backtick then [[link]]";
        var result = WikiLinkParser.ParseWithPositions(content);

        var link = Assert.Single(result);
        Assert.False(link.IsCode);
    }

    [Fact]
    public void ParseWithPositions_EmptyTargetAfterTrimming_IsSkipped()
    {
        Assert.Empty(WikiLinkParser.ParseWithPositions("[[]] [[   ]] [[|OnlyAlias]]"));
    }
}
