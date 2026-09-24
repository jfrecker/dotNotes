using System.Text;

namespace DotNotes.Core.Tasks;

/// <summary>
/// Sanitises a task title into a safe file-name segment and builds the
/// <c>&lt;ID&gt; - &lt;Title&gt;.md</c> file name convention, per
/// docs/features/tasks-kanban/PLAN.md §2 ("Filename"). The sanitised title
/// must pass <see cref="Notes.FileSystemNoteRepository"/>'s new-name
/// validation (docs/06-DATA-MODEL.md's "Names" section) - i.e. it must not
/// contain <c>[</c>, <c>]</c>, <c>|</c>, a control character, or a
/// character invalid for a file name on the host OS, and must not have
/// leading/trailing whitespace.
/// </summary>
public static class TaskFileNames
{
    private static readonly char[] CharsToStrip = "[]|\\/:*?\"<>#".ToCharArray();
    private const int MaxTitleLength = 80;

    /// <summary>
    /// Sanitises <paramref name="title"/> for use as a file-name segment:
    /// strips <c>[ ] | \ / : * ? " &lt; &gt; #</c> and control characters,
    /// collapses whitespace runs to a single space, trims, trims trailing
    /// dots, and caps the result at <see cref="MaxTitleLength"/> characters.
    /// Returns <c>"Untitled"</c> if nothing is left.
    /// </summary>
    public static string Sanitize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Untitled";
        }

        var builder = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            // Control characters that are also whitespace (tab, newline,
            // CR, ...) are left in place here and collapsed to a single
            // space below, rather than deleted outright - deleting them
            // would fuse the words on either side together.
            if ((char.IsControl(c) && !char.IsWhiteSpace(c)) || Array.IndexOf(CharsToStrip, c) >= 0)
            {
                continue;
            }

            builder.Append(c);
        }

        // Collapse any whitespace run (spaces, tabs, newlines) to a single space.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"\s+", " ").Trim();

        // Trailing dots are disallowed on Windows file names and are
        // visually odd anyway ("My Task..." -> "My Task").
        collapsed = collapsed.TrimEnd('.').Trim();

        if (collapsed.Length > MaxTitleLength)
        {
            // Never cut between the two halves of a surrogate pair (emoji,
            // some CJK): a lone surrogate is not a valid file name.
            var cut = char.IsHighSurrogate(collapsed[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
            collapsed = collapsed[..cut].TrimEnd().TrimEnd('.').Trim();
        }

        return collapsed.Length == 0 ? "Untitled" : collapsed;
    }

    /// <summary>Builds <c>&lt;ID&gt; - &lt;SanitizedTitle&gt;.md</c>.</summary>
    public static string BuildFileName(string id, string? title) => $"{id} - {Sanitize(title)}.md";

    /// <summary>
    /// <see langword="true"/> if <paramref name="fileNameOrPath"/>'s file
    /// name (ignoring any directory prefix) is exactly the id-derived name
    /// this type would build for <paramref name="id"/> and
    /// <paramref name="title"/> - used by <see cref="ITaskService.UpdateAsync"/>
    /// to decide whether a title change should rename the file (i.e. the
    /// current file name still follows the <c>&lt;ID&gt; - &lt;Title&gt;.md</c>
    /// convention) versus leaving a manually-renamed file alone.
    /// </summary>
    public static bool IsIdDerivedFileName(string fileNameOrPath, string id)
    {
        var fileName = System.IO.Path.GetFileName(fileNameOrPath.Replace('\\', '/'));
        var prefix = id + " - ";
        return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }
}
