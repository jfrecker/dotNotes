using DotNotes.Core.Links;

namespace DotNotes.Core.Tests.Links;

public sealed class WikiLinkResolverTests
{
    [Fact]
    public void Resolve_ExactFullPathMatch_ResolvesToKnownPathAndExistsTrue()
    {
        var known = new[] { "projects/idea.md", "daily/2026-09-16.md" };

        var result = WikiLinkResolver.Resolve("projects/idea", known);

        Assert.Equal("projects/idea.md", result.TargetPath);
        Assert.True(result.Exists);
    }

    [Fact]
    public void Resolve_ExactMatch_IsCaseInsensitive()
    {
        var known = new[] { "Projects/Idea.md" };

        var result = WikiLinkResolver.Resolve("projects/IDEA", known);

        Assert.Equal("Projects/Idea.md", result.TargetPath);
        Assert.True(result.Exists);
    }

    [Fact]
    public void Resolve_BareTitleMatchingSingleNoteInADifferentFolder_ResolvesToThatNote()
    {
        var known = new[] { "projects/idea.md", "daily/2026-09-16.md" };

        var result = WikiLinkResolver.Resolve("idea", known);

        Assert.Equal("projects/idea.md", result.TargetPath);
        Assert.True(result.Exists);
    }

    [Fact]
    public void Resolve_BareTitleMatch_IsCaseInsensitive()
    {
        var known = new[] { "projects/Idea.md" };

        var result = WikiLinkResolver.Resolve("IDEA", known);

        Assert.Equal("projects/Idea.md", result.TargetPath);
        Assert.True(result.Exists);
    }

    [Fact]
    public void Resolve_AmbiguousBareTitle_PrefersShallowestPath()
    {
        var known = new[] { "archive/deep/idea.md", "projects/idea.md" };

        var result = WikiLinkResolver.Resolve("idea", known);

        Assert.Equal("projects/idea.md", result.TargetPath);
        Assert.True(result.Exists);
    }

    [Fact]
    public void Resolve_AmbiguousBareTitleAtSameDepth_BreaksTieOrdinally()
    {
        var known = new[] { "zzz/idea.md", "aaa/idea.md" };

        var result = WikiLinkResolver.Resolve("idea", known);

        Assert.Equal("aaa/idea.md", result.TargetPath);
    }

    [Fact]
    public void Resolve_AmbiguousBareTitle_IsDeterministicRegardlessOfInputOrder()
    {
        var known = new[] { "b/idea.md", "a/idea.md", "c/idea.md" };

        var first = WikiLinkResolver.Resolve("idea", known);
        var second = WikiLinkResolver.Resolve("idea", known.Reverse().ToArray());

        Assert.Equal(first.TargetPath, second.TargetPath);
        Assert.Equal("a/idea.md", first.TargetPath);
    }

    [Fact]
    public void Resolve_NoMatchAtAll_ReturnsUnresolvedWithNormalizedPath()
    {
        var known = new[] { "projects/idea.md" };

        var result = WikiLinkResolver.Resolve("not-created-yet", known);

        Assert.Equal("not-created-yet.md", result.TargetPath);
        Assert.False(result.Exists);
    }

    [Fact]
    public void Resolve_NoMatchWithFolderPrefix_ReturnsUnresolvedWithFullNormalizedPath()
    {
        var result = WikiLinkResolver.Resolve("someday/future-note", knownNotePaths: []);

        Assert.Equal("someday/future-note.md", result.TargetPath);
        Assert.False(result.Exists);
    }

    [Fact]
    public void Resolve_TargetAlreadyEndingInMd_DoesNotDoubleAppendExtension()
    {
        var known = new[] { "projects/idea.md" };

        var result = WikiLinkResolver.Resolve("projects/idea.md", known);

        Assert.Equal("projects/idea.md", result.TargetPath);
        Assert.True(result.Exists);
    }

    [Fact]
    public void Resolve_BackslashSeparatedRawTarget_IsNormalizedToForwardSlashes()
    {
        var known = new[] { "projects/idea.md" };

        var result = WikiLinkResolver.Resolve(@"projects\idea", known);

        Assert.Equal("projects/idea.md", result.TargetPath);
        Assert.True(result.Exists);
    }

    [Fact]
    public void Resolve_SelfLink_ResolvesToItsOwnKnownPath()
    {
        // A note's own path is itself part of "known note paths" once it
        // exists, so a self-link resolves exactly like any other link.
        var known = new[] { "projects/idea.md" };

        var result = WikiLinkResolver.Resolve("projects/idea", known);

        Assert.Equal("projects/idea.md", result.TargetPath);
        Assert.True(result.Exists);
    }

    [Theory]
    [InlineData("idea", "idea")]
    [InlineData("projects/idea.md", "idea")]
    [InlineData("projects/idea", "idea")]
    [InlineData("projects/nested/deep", "deep")]
    public void GetBareTitle_StripsFolderAndExtension(string notePath, string expectedTitle)
    {
        var normalized = WikiLinkResolver.NormalizeToNotePath(notePath);

        Assert.Equal(expectedTitle, WikiLinkResolver.GetBareTitle(normalized));
    }

    [Theory]
    [InlineData("idea", "idea.md")]
    [InlineData("idea.md", "idea.md")]
    [InlineData("IDEA.MD", "IDEA.MD")]
    [InlineData("/projects/idea/", "projects/idea.md")]
    [InlineData(@"projects\idea", "projects/idea.md")]
    public void NormalizeToNotePath_ProducesCanonicalForwardSlashMdPath(string rawTarget, string expected)
    {
        Assert.Equal(expected, WikiLinkResolver.NormalizeToNotePath(rawTarget));
    }
}
