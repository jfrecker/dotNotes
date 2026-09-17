using DotNotes.Core.Config;
using DotNotes.Core.Notes;
using DotNotes.Core.Search;
using Microsoft.Extensions.Options;

namespace DotNotes.Core.Tests.Search;

/// <summary>
/// Covers <see cref="InMemorySearchIndex"/>'s full-scan rebuild and its
/// incremental update logic (<see cref="InMemorySearchIndex.NoteChanged"/>/
/// <see cref="InMemorySearchIndex.NoteDeleted"/>) directly, with no
/// <see cref="System.IO.FileSystemWatcher"/> involved - mirrors
/// <c>InMemoryLinkIndexTests</c>'s style for <c>ILinkIndex</c>.
/// </summary>
public sealed class InMemorySearchIndexTests : IDisposable
{
    private readonly DirectoryInfo _vaultDirectory;
    private readonly FileSystemNoteRepository _noteRepository;
    private readonly InMemorySearchIndex _index;

    public InMemorySearchIndexTests()
    {
        _vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-search-index-tests-");
        _noteRepository = new FileSystemNoteRepository(
            Options.Create(new VaultOptions { RootPath = _vaultDirectory.FullName }));
        _index = new InMemorySearchIndex(_noteRepository);
    }

    public void Dispose() => _vaultDirectory.Delete(recursive: true);

    // ---- RebuildAsync (full scan from disk) ----

    [Fact]
    public async Task RebuildAsync_EmptyVault_ProducesNoResults()
    {
        await _index.RebuildAsync();

        Assert.Empty(_index.Search("anything", limit: 10));
    }

    [Fact]
    public async Task RebuildAsync_ScansExistingNotesAndMakesThemSearchable()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "This note is about a great idea.");

        await _index.RebuildAsync();

        var result = Assert.Single(_index.Search("idea", limit: 10));
        Assert.Equal("projects/idea.md", result.Path);
    }

    [Fact]
    public async Task RebuildAsync_IsSafeToCallAgainAndFullyDiscardsPriorState()
    {
        await _noteRepository.SaveAsync("projects/idea.md", "about idea");
        await _index.RebuildAsync();
        Assert.Single(_index.Search("idea", limit: 10));

        // Delete the note on disk without going through NoteDeleted - a
        // rebuild must reflect whatever is on disk *now*, discarding all
        // prior in-memory state, per CLAUDE.md's "safe to discard and
        // rebuild entirely from files on disk at any time" rule.
        await _noteRepository.DeleteAsync("projects/idea.md");
        await _index.RebuildAsync();

        Assert.Empty(_index.Search("idea", limit: 10));
    }

    // ---- NoteChanged (create/update) ----

    [Fact]
    public void NoteChanged_NewNote_IsSearchableByBodyContent()
    {
        _index.NoteChanged("projects/idea.md", "Some notes about gardening.");

        var result = Assert.Single(_index.Search("gardening", limit: 10));
        Assert.Equal("projects/idea.md", result.Path);
        Assert.Equal("idea", result.Title);
    }

    [Fact]
    public void NoteChanged_NoteWithNoMatchingTerm_IsNotReturned()
    {
        _index.NoteChanged("projects/idea.md", "Some notes about gardening.");

        Assert.Empty(_index.Search("astronomy", limit: 10));
    }

    [Fact]
    public void NoteChanged_EditingANote_RemovesStaleTermsAndAddsNewOnes()
    {
        _index.NoteChanged("projects/idea.md", "about gardening");
        Assert.Single(_index.Search("gardening", limit: 10));

        _index.NoteChanged("projects/idea.md", "about astronomy now");

        Assert.Empty(_index.Search("gardening", limit: 10));
        Assert.Single(_index.Search("astronomy", limit: 10));
    }

    [Fact]
    public void NoteChanged_StopwordsInBody_AreNotIndexedAsSearchableTerms()
    {
        _index.NoteChanged("projects/idea.md", "the idea of a project");

        // "the", "of", "a" are stopwords - searching for one of them alone
        // tokenizes to nothing, so it must return no results, not
        // "everything" (which a naive substring search might do).
        Assert.Empty(_index.Search("the", limit: 10));
    }

    [Fact]
    public void NoteChanged_ReindexingSameContentTwice_DoesNotDoubleCountTermFrequency()
    {
        _index.NoteChanged("projects/idea.md", "idea idea idea");
        var first = Assert.Single(_index.Search("idea", limit: 10));

        _index.NoteChanged("projects/idea.md", "idea idea idea");
        var second = Assert.Single(_index.Search("idea", limit: 10));

        Assert.Equal(first.Score, second.Score);
    }

    [Fact]
    public void NoteChanged_QueryIsCaseInsensitive()
    {
        _index.NoteChanged("projects/idea.md", "Gardening Tips");

        Assert.Single(_index.Search("GARDENING", limit: 10));
        Assert.Single(_index.Search("gardening", limit: 10));
    }

    // ---- NoteDeleted ----

    [Fact]
    public void NoteDeleted_RemovesNoteFromSearchResults()
    {
        _index.NoteChanged("projects/idea.md", "about gardening");
        Assert.Single(_index.Search("gardening", limit: 10));

        _index.NoteDeleted("projects/idea.md");

        Assert.Empty(_index.Search("gardening", limit: 10));
    }

    [Fact]
    public void NoteDeleted_UnknownPath_IsANoOp()
    {
        _index.NoteDeleted("never/indexed.md");

        Assert.Empty(_index.Search("anything", limit: 10));
    }

    [Fact]
    public void EditThenDeleteThenRecreate_LeavesIndexConsistentAtEachStep()
    {
        _index.NoteChanged("projects/idea.md", "about gardening");
        Assert.Single(_index.Search("gardening", limit: 10));

        _index.NoteChanged("projects/idea.md", "about astronomy");
        Assert.Empty(_index.Search("gardening", limit: 10));
        Assert.Single(_index.Search("astronomy", limit: 10));

        _index.NoteDeleted("projects/idea.md");
        Assert.Empty(_index.Search("astronomy", limit: 10));

        _index.NoteChanged("projects/idea.md", "about gardening again");
        Assert.Single(_index.Search("gardening", limit: 10));
    }

    // ---- Ranking: title match must outrank a body-only match ----

    [Fact]
    public void Search_TitleMatch_RanksAboveABodyOnlyMatchForTheSameTerm()
    {
        // "idea.md" has "idea" in its title/filename but not its body;
        // "other.md" mentions "idea" only in its body a few times.
        _index.NoteChanged("projects/idea.md", "This note has no matching words in its body at all.");
        _index.NoteChanged("projects/other.md", "idea idea idea - lots of body mentions of idea.");

        var results = _index.Search("idea", limit: 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("projects/idea.md", results[0].Path);
        Assert.Equal("projects/other.md", results[1].Path);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void Search_NoteMatchingBothTitleAndBody_ScoresHigherThanTitleOnlyMatch()
    {
        _index.NoteChanged("projects/idea.md", "no relevant body terms here");
        _index.NoteChanged("projects/idea-notes.md", "idea idea - this body also mentions idea");

        var results = _index.Search("idea", limit: 10);

        Assert.Equal("projects/idea-notes.md", results[0].Path);
    }

    // ---- Search: query handling, limit, snippets ----

    [Fact]
    public void Search_EmptyOrWhitespaceQuery_ReturnsNoResults()
    {
        _index.NoteChanged("projects/idea.md", "about gardening");

        Assert.Empty(_index.Search("", limit: 10));
        Assert.Empty(_index.Search("   ", limit: 10));
    }

    [Fact]
    public void Search_QueryThatTokenizesToOnlyStopwords_ReturnsNoResults()
    {
        _index.NoteChanged("projects/idea.md", "about gardening");

        Assert.Empty(_index.Search("the a of", limit: 10));
    }

    [Fact]
    public void Search_ZeroOrNegativeLimit_ReturnsNoResults()
    {
        _index.NoteChanged("projects/idea.md", "about gardening");

        Assert.Empty(_index.Search("gardening", limit: 0));
        Assert.Empty(_index.Search("gardening", limit: -5));
    }

    [Fact]
    public void Search_RespectsLimit_ReturningAtMostThatManyResults()
    {
        for (var i = 0; i < 5; i++)
        {
            _index.NoteChanged($"notes/note{i}.md", "gardening tips and tricks");
        }

        var results = _index.Search("gardening", limit: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Search_EachResultIncludesASnippet()
    {
        _index.NoteChanged("projects/idea.md", "Some long-form notes about gardening in the springtime.");

        var result = Assert.Single(_index.Search("gardening", limit: 10));

        Assert.False(string.IsNullOrWhiteSpace(result.Snippet));
        Assert.Contains("gardening", result.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_MultiWordQuery_MatchesNotesContainingEitherTerm()
    {
        _index.NoteChanged("projects/a.md", "all about gardening");
        _index.NoteChanged("projects/b.md", "all about astronomy");
        _index.NoteChanged("projects/c.md", "completely unrelated content");

        var results = _index.Search("gardening astronomy", limit: 10);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Path == "projects/a.md");
        Assert.Contains(results, r => r.Path == "projects/b.md");
    }

    [Fact]
    public void Search_ResultsAreOrderedByDescendingScore()
    {
        _index.NoteChanged("projects/a.md", "idea mentioned once");
        _index.NoteChanged("projects/b.md", "idea idea idea idea mentioned four times");

        var results = _index.Search("idea", limit: 10);

        Assert.Equal("projects/b.md", results[0].Path);
        Assert.Equal("projects/a.md", results[1].Path);
        Assert.True(results[0].Score > results[1].Score);
    }
}
