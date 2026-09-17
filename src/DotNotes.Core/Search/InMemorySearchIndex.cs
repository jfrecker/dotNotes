using DotNotes.Core.Links;
using DotNotes.Core.Notes;

namespace DotNotes.Core.Search;

/// <summary>
/// Default <see cref="ISearchIndex"/> implementation: an in-memory
/// inverted index (token -&gt; postings) over both note bodies and note
/// titles, guarded by a single lock - the same style as
/// <see cref="InMemoryLinkIndex"/>. This is a derived cache only (see
/// docs/06-DATA-MODEL.md's "Search index" section) and is safe to
/// discard/rebuild at any time via <see cref="RebuildAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoring.</b> For each distinct query token, every note whose body
/// contains that token scores <c>+1</c> per occurrence (raw term
/// frequency, no length normalization - fine at the "hundreds to a few
/// thousand notes" personal-vault scale this MVP targets), and every note
/// whose <i>title</i> (bare filename, without <c>.md</c>) contains that
/// token additionally scores <c>+<see cref="TitleMatchBoost"/></c>. A
/// note's total score is the sum of these contributions across every
/// query token it matched at all (by either body or title); notes that
/// match nothing score nothing and are excluded from results entirely.
/// The boost is large enough that a title match always outranks a
/// realistic body-only match for the same term, without needing a full
/// tf-idf model for an MVP index.
/// </para>
/// <para>
/// <b>Never returns a result outside the vault root.</b> This type never
/// touches the filesystem directly - every path it knows about was handed
/// to it via <see cref="NoteChanged"/> (ultimately sourced from
/// <see cref="INoteRepository"/>, which already rejects/normalizes paths
/// outside the vault root) or discovered via <see cref="RebuildAsync"/>'s
/// own <see cref="INoteRepository"/> scan - so this invariant holds
/// trivially as long as callers never invent a path themselves.
/// </para>
/// </remarks>
public sealed class InMemorySearchIndex : ISearchIndex
{
    /// <summary>
    /// Added to a note's score, per matched query token, when that token
    /// appears in the note's title - see this type's remarks for why this
    /// value is large enough to make a title match outrank a realistic
    /// body-only match for the same term.
    /// </summary>
    internal const double TitleMatchBoost = 10.0;

    private readonly INoteRepository _noteRepository;
    private readonly object _lock = new();

    /// <summary>token -&gt; (path -&gt; body term frequency).</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _bodyPostings =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>token -&gt; paths whose title contains that token.</summary>
    private readonly Dictionary<string, HashSet<string>> _titlePostings =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// path -&gt; the distinct set of body tokens currently posted for it -
    /// internal bookkeeping so <see cref="RemoveIndexedPath"/> knows
    /// exactly which <see cref="_bodyPostings"/> entries to clean up,
    /// without scanning every token in the index.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _bodyTokensByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Same bookkeeping as <see cref="_bodyTokensByPath"/>, for <see cref="_titlePostings"/>.</summary>
    private readonly Dictionary<string, HashSet<string>> _titleTokensByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>path -&gt; raw note content, kept only for snippet generation.</summary>
    private readonly Dictionary<string, string> _contentByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>path -&gt; display title (bare filename, no ".md").</summary>
    private readonly Dictionary<string, string> _titleByPath =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemorySearchIndex(INoteRepository noteRepository)
    {
        _noteRepository = noteRepository;
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var tree = await _noteRepository.GetTreeAsync(cancellationToken).ConfigureAwait(false);
        var filePaths = new List<string>();
        CollectFilePaths(tree, filePaths);

        var newBodyPostings = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var newTitlePostings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var newBodyTokensByPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var newTitleTokensByPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var newContentByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newTitleByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The note could have been deleted between GetTreeAsync's
            // snapshot and this read; skip it rather than fail the whole
            // rebuild, matching InMemoryLinkIndex.RebuildAsync's
            // documented trade-off - a subsequent file-watcher event (or
            // the next rebuild) reconciles it.
            var note = await _noteRepository.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (note is null)
            {
                continue;
            }

            IndexNote(
                path,
                note.Content,
                newBodyPostings,
                newTitlePostings,
                newBodyTokensByPath,
                newTitleTokensByPath,
                newContentByPath,
                newTitleByPath);
        }

        lock (_lock)
        {
            ReplaceContents(_bodyPostings, newBodyPostings);
            ReplaceContents(_titlePostings, newTitlePostings);
            ReplaceContents(_bodyTokensByPath, newBodyTokensByPath);
            ReplaceContents(_titleTokensByPath, newTitleTokensByPath);

            _contentByPath.Clear();
            foreach (var (path, content) in newContentByPath)
            {
                _contentByPath[path] = content;
            }

            _titleByPath.Clear();
            foreach (var (path, title) in newTitleByPath)
            {
                _titleByPath[path] = title;
            }
        }
    }

    public void NoteChanged(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        lock (_lock)
        {
            RemoveIndexedPath(path);
            IndexNote(path, content, _bodyPostings, _titlePostings, _bodyTokensByPath, _titleTokensByPath, _contentByPath, _titleByPath);
        }
    }

    public void NoteDeleted(string path)
    {
        lock (_lock)
        {
            RemoveIndexedPath(path);
        }
    }

    public IReadOnlyList<SearchResult> Search(string query, int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<SearchResult>();
        }

        var queryTokens = Tokenizer.Tokenize(query).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (queryTokens.Length == 0)
        {
            return Array.Empty<SearchResult>();
        }

        lock (_lock)
        {
            var scoreByPath = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var matchedTokensByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var token in queryTokens)
            {
                if (_bodyPostings.TryGetValue(token, out var bodyMatches))
                {
                    foreach (var (path, frequency) in bodyMatches)
                    {
                        scoreByPath[path] = scoreByPath.GetValueOrDefault(path) + frequency;
                        RecordMatchedToken(matchedTokensByPath, path, token);
                    }
                }

                if (_titlePostings.TryGetValue(token, out var titleMatches))
                {
                    foreach (var path in titleMatches)
                    {
                        scoreByPath[path] = scoreByPath.GetValueOrDefault(path) + TitleMatchBoost;
                        RecordMatchedToken(matchedTokensByPath, path, token);
                    }
                }
            }

            return scoreByPath
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(entry => BuildResult(entry.Key, entry.Value, matchedTokensByPath[entry.Key]))
                .ToArray();
        }
    }

    private SearchResult BuildResult(string path, double score, IReadOnlyList<string> matchedTokens)
    {
        _contentByPath.TryGetValue(path, out var content);
        _titleByPath.TryGetValue(path, out var title);

        return new SearchResult
        {
            Path = path,
            Title = title ?? WikiLinkResolver.GetBareTitle(path),
            Snippet = SnippetGenerator.Generate(content, matchedTokens),
            Score = score
        };
    }

    private static void RecordMatchedToken(Dictionary<string, List<string>> matchedTokensByPath, string path, string token)
    {
        if (!matchedTokensByPath.TryGetValue(path, out var tokens))
        {
            tokens = new List<string>();
            matchedTokensByPath[path] = tokens;
        }

        if (!tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
        {
            tokens.Add(token);
        }
    }

    /// <summary>
    /// Tokenizes and indexes one note's body + title into the given
    /// (either live or newly-built) dictionaries. Must be called while
    /// holding <see cref="_lock"/> when targeting the live dictionaries.
    /// </summary>
    private static void IndexNote(
        string path,
        string content,
        Dictionary<string, Dictionary<string, int>> bodyPostings,
        Dictionary<string, HashSet<string>> titlePostings,
        Dictionary<string, HashSet<string>> bodyTokensByPath,
        Dictionary<string, HashSet<string>> titleTokensByPath,
        Dictionary<string, string> contentByPath,
        Dictionary<string, string> titleByPath)
    {
        var bodyTermFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokenizer.Tokenize(content))
        {
            bodyTermFrequency[token] = bodyTermFrequency.GetValueOrDefault(token) + 1;
        }

        var title = WikiLinkResolver.GetBareTitle(path);
        var titleTokens = new HashSet<string>(Tokenizer.Tokenize(title), StringComparer.OrdinalIgnoreCase);

        foreach (var (token, frequency) in bodyTermFrequency)
        {
            if (!bodyPostings.TryGetValue(token, out var postings))
            {
                postings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                bodyPostings[token] = postings;
            }

            postings[path] = frequency;
        }

        foreach (var token in titleTokens)
        {
            if (!titlePostings.TryGetValue(token, out var paths))
            {
                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                titlePostings[token] = paths;
            }

            paths.Add(path);
        }

        bodyTokensByPath[path] = new HashSet<string>(bodyTermFrequency.Keys, StringComparer.OrdinalIgnoreCase);
        titleTokensByPath[path] = titleTokens;
        contentByPath[path] = content;
        titleByPath[path] = title;
    }

    /// <summary>
    /// Removes every trace of <paramref name="path"/> from the live index
    /// (both postings dictionaries, pruning any token whose posting list
    /// drops to zero entries, plus the content/title lookups). Must be
    /// called while holding <see cref="_lock"/>. A no-op if
    /// <paramref name="path"/> was never indexed.
    /// </summary>
    private void RemoveIndexedPath(string path)
    {
        if (_bodyTokensByPath.TryGetValue(path, out var bodyTokens))
        {
            foreach (var token in bodyTokens)
            {
                if (_bodyPostings.TryGetValue(token, out var postings))
                {
                    postings.Remove(path);
                    if (postings.Count == 0)
                    {
                        _bodyPostings.Remove(token);
                    }
                }
            }

            _bodyTokensByPath.Remove(path);
        }

        if (_titleTokensByPath.TryGetValue(path, out var titleTokens))
        {
            foreach (var token in titleTokens)
            {
                if (_titlePostings.TryGetValue(token, out var paths))
                {
                    paths.Remove(path);
                    if (paths.Count == 0)
                    {
                        _titlePostings.Remove(token);
                    }
                }
            }

            _titleTokensByPath.Remove(path);
        }

        _contentByPath.Remove(path);
        _titleByPath.Remove(path);
    }

    private static void ReplaceContents<TValue>(Dictionary<string, TValue> target, Dictionary<string, TValue> replacement)
    {
        target.Clear();
        foreach (var (key, value) in replacement)
        {
            target[key] = value;
        }
    }

    private static void CollectFilePaths(IReadOnlyList<NoteTreeEntry> entries, List<string> filePaths)
    {
        foreach (var entry in entries)
        {
            if (entry.Type == NoteEntryType.File)
            {
                filePaths.Add(entry.Path);
            }
            else if (entry.Children is not null)
            {
                CollectFilePaths(entry.Children, filePaths);
            }
        }
    }
}
