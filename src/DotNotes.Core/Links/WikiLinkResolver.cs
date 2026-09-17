namespace DotNotes.Core.Links;

/// <summary>
/// Resolves a wikilink's raw target text (whatever was written inside
/// <c>[[...]]</c>) to a concrete, normalized vault-relative note path,
/// given the current set of known note paths in the vault. Matching is
/// always case-insensitive, per docs/06-DATA-MODEL.md.
/// </summary>
/// <remarks>
/// docs/06-DATA-MODEL.md deliberately leaves one case open: what a bare
/// title like <c>[[idea]]</c> should resolve to when multiple notes
/// across different folders share that filename. This type defines and
/// documents the concrete rule this codebase uses:
/// <list type="number">
/// <item>
/// <description>
/// <b>Exact full-path match.</b> Normalize the raw target to a
/// <c>/</c>-separated relative path ending in <c>.md</c>, and compare it
/// case-insensitively against every known note path. This handles both a
/// root-level bare name (<c>[[idea]]</c> -&gt; <c>idea.md</c>) and an
/// explicit folder-qualified link (<c>[[projects/idea]]</c> -&gt;
/// <c>projects/idea.md</c>) whenever that exact path exists.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Unique title match.</b> If nothing matched by full path, compare
/// the raw target's bare filename (its last path segment, without
/// extension) case-insensitively against the bare filename of every
/// known note. If exactly one note matches, resolve to it - this lets
/// <c>[[idea]]</c> find <c>projects/idea.md</c> even though the link
/// didn't spell out the folder.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Deterministic tie-break.</b> If more than one note shares that
/// bare title (e.g. both <c>projects/idea.md</c> and
/// <c>archive/idea.md</c> exist), pick the candidate with the fewest
/// path segments (shallowest in the vault - i.e. closest to the vault
/// root), then break any remaining tie with an ordinal
/// case-insensitive comparison of the full path. This is an arbitrary
/// but *stable* rule, so the same ambiguous link always resolves to the
/// same note across restarts and index rebuilds, rather than resolving
/// differently depending on enumeration order.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Unresolved ("missing").</b> If nothing matches at all, the link
/// still resolves to the normalized path from step 1 - giving it a
/// stable identity for the backlink index / graph - but is flagged
/// <c>Exists: false</c>, per docs/06-DATA-MODEL.md's "unresolved links
/// ... are still tracked" rule.
/// </description>
/// </item>
/// </list>
/// Note: adding or removing a note changes the "known note paths" set,
/// which can in turn change how a previously-ambiguous or
/// previously-unresolved bare-title link *would* resolve. This resolver
/// itself is stateless and always re-resolves against whatever set it's
/// given; it is the caller's responsibility (see <see cref="InMemoryLinkIndex"/>)
/// to decide when to re-resolve - see that type's remarks for the
/// specific (documented) trade-off made there.
/// </remarks>
public static class WikiLinkResolver
{
    public static ResolvedWikiLink Resolve(string rawTarget, IReadOnlyCollection<string> knownNotePaths)
    {
        var normalizedTarget = NormalizeToNotePath(rawTarget);

        foreach (var known in knownNotePaths)
        {
            if (string.Equals(known, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedWikiLink(known, Exists: true);
            }
        }

        var bareTitle = GetBareTitle(normalizedTarget);
        List<string>? titleMatches = null;
        foreach (var known in knownNotePaths)
        {
            if (string.Equals(GetBareTitle(known), bareTitle, StringComparison.OrdinalIgnoreCase))
            {
                (titleMatches ??= []).Add(known);
            }
        }

        switch (titleMatches?.Count)
        {
            case null or 0:
                // Nothing matched at all - unresolved/"missing" target.
                return new ResolvedWikiLink(normalizedTarget, Exists: false);

            case 1:
                return new ResolvedWikiLink(titleMatches[0], Exists: true);

            default:
                // Ambiguous: more than one note shares this bare title.
                // Deterministic tie-break - see remarks on this type.
                var chosen = titleMatches
                    .OrderBy(CountPathSegments)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new ResolvedWikiLink(chosen, Exists: true);
        }
    }

    /// <summary>
    /// Normalizes a raw wikilink target (or any note path) to this
    /// codebase's canonical form: forward-slash separated, no leading or
    /// trailing slash, and ending in <c>.md</c> (appended if not already
    /// present, case-insensitively).
    /// </summary>
    public static string NormalizeToNotePath(string rawTarget)
    {
        var normalized = rawTarget.Trim().Replace('\\', '/').Trim('/');
        return normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".md";
    }

    /// <summary>
    /// The bare display title of a normalized note path: its last
    /// <c>/</c>-separated segment, without a trailing <c>.md</c>
    /// extension. E.g. <c>projects/idea.md</c> -&gt; <c>idea</c>.
    /// </summary>
    public static string GetBareTitle(string notePath)
    {
        var lastSlash = notePath.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? notePath[(lastSlash + 1)..] : notePath;
        return fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".md".Length]
            : fileName;
    }

    private static int CountPathSegments(string path) => path.Count(c => c == '/');
}
