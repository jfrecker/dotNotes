using DotNotes.Core.Search;

namespace DotNotes.Core.Tests.Search;

/// <summary>
/// Covers <see cref="Tokenizer.Tokenize"/> in isolation: whitespace/
/// punctuation splitting, lowercasing, and stopword removal, per
/// docs/06-DATA-MODEL.md's "Search index" section.
/// </summary>
public sealed class TokenizerTests
{
    [Fact]
    public void Tokenize_SplitsOnWhitespace()
    {
        Assert.Equal(["idea", "notes", "vault"], Tokenizer.Tokenize("idea notes vault"));
    }

    [Fact]
    public void Tokenize_SplitsOnPunctuation()
    {
        Assert.Equal(["hello", "world"], Tokenizer.Tokenize("hello, world!"));
    }

    [Fact]
    public void Tokenize_SplitsOnMarkdownSyntaxCharacters()
    {
        Assert.Equal(["heading", "one"], Tokenizer.Tokenize("# Heading [[one]]"));
    }

    [Fact]
    public void Tokenize_LowercasesEveryToken()
    {
        Assert.Equal(["idea"], Tokenizer.Tokenize("IDEA"));
        Assert.Equal(["idea"], Tokenizer.Tokenize("Idea"));
        Assert.Equal(["mixed", "case"], Tokenizer.Tokenize("MiXeD CaSe"));
    }

    [Fact]
    public void Tokenize_RemovesStopwords()
    {
        Assert.Equal(["idea", "vault"], Tokenizer.Tokenize("the idea of a vault"));
    }

    [Fact]
    public void Tokenize_QueryThatIsOnlyStopwords_ProducesNoTokens()
    {
        Assert.Empty(Tokenizer.Tokenize("the a an of"));
    }

    [Fact]
    public void Tokenize_EmptyOrNullInput_ProducesNoTokens()
    {
        Assert.Empty(Tokenizer.Tokenize(""));
        Assert.Empty(Tokenizer.Tokenize(null));
        Assert.Empty(Tokenizer.Tokenize("   "));
    }

    [Fact]
    public void Tokenize_PreservesDuplicatesAndOrder()
    {
        Assert.Equal(["idea", "idea", "notes"], Tokenizer.Tokenize("idea idea notes"));
    }

    [Fact]
    public void Tokenize_HyphenatedWord_SplitsIntoSeparateTokens()
    {
        // Documented MVP simplification (see Tokenizer's remarks): a
        // hyphen is treated purely as a separator, not part of the word.
        Assert.Equal(["front", "matter"], Tokenizer.Tokenize("front-matter"));
    }

    [Fact]
    public void Tokenize_DigitsAreKeptAsPartOfTokens()
    {
        Assert.Equal(["2026", "plan"], Tokenizer.Tokenize("2026 plan"));
    }
}
