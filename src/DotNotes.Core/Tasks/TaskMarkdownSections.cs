using System.Text.RegularExpressions;

namespace DotNotes.Core.Tasks;

/// <summary>
/// Body-section half of <see cref="TaskMarkdown"/>: Backlog.md-style
/// marker sections (Description, Acceptance Criteria, Implementation Plan,
/// Implementation Notes, Final Summary), per
/// docs/features/tasks-kanban/PLAN.md §2. Every mutator here edits the
/// body <b>by character span</b> and returns a new body string - content
/// outside the touched span (including preserved-only sections like
/// <c>## Definition of Done</c>/<c>## Comments</c>, and any other
/// unrecognised prose) is left byte-for-byte untouched.
/// </summary>
public static partial class TaskMarkdown
{
    private const string DescriptionHeading = "## Description";
    private const string DescriptionBegin = "<!-- SECTION:DESCRIPTION:BEGIN -->";
    private const string DescriptionEnd = "<!-- SECTION:DESCRIPTION:END -->";

    private const string AcceptanceCriteriaHeading = "## Acceptance Criteria";
    private const string AcceptanceCriteriaBegin = "<!-- AC:BEGIN -->";
    private const string AcceptanceCriteriaEnd = "<!-- AC:END -->";

    private const string PlanHeading = "## Implementation Plan";
    private const string PlanBegin = "<!-- SECTION:PLAN:BEGIN -->";
    private const string PlanEnd = "<!-- SECTION:PLAN:END -->";

    private const string NotesHeading = "## Implementation Notes";
    private const string NotesBegin = "<!-- SECTION:NOTES:BEGIN -->";
    private const string NotesEnd = "<!-- SECTION:NOTES:END -->";

    private const string FinalSummaryHeading = "## Final Summary";
    private const string FinalSummaryBegin = "<!-- SECTION:FINAL_SUMMARY:BEGIN -->";
    private const string FinalSummaryEnd = "<!-- SECTION:FINAL_SUMMARY:END -->";

    private const string DefinitionOfDoneHeading = "## Definition of Done";
    private const string CommentsHeading = "## Comments";

    /// <summary>
    /// Canonical insertion order for every recognised or preserved-only
    /// section heading, per docs/features/tasks-kanban/PLAN.md §2's
    /// "Section order when inserting" rule.
    /// </summary>
    private static readonly string[] CanonicalHeadingOrder =
    {
        DescriptionHeading,
        AcceptanceCriteriaHeading,
        DefinitionOfDoneHeading,
        PlanHeading,
        NotesHeading,
        CommentsHeading,
        FinalSummaryHeading,
    };

    private static readonly Regex AcceptanceCriterionLinePattern = new(
        @"^-\s\[([ xX])\]\s*(?:#(\d+)\s+)?(.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// The description: content inside <c>## Description</c>'s markers if
    /// present, else the free body text outside any other recognised
    /// section heading (the "converted note keeps its existing text"
    /// fallback), trimmed.
    /// </summary>
    public static string GetDescription(string body)
    {
        var section = FindMarkerSection(body, DescriptionHeading, DescriptionBegin, DescriptionEnd, blankLineAfterHeading: true);
        if (section is not null)
        {
            return body.Substring(section.Value.InnerStart, section.Value.InnerLength);
        }

        var headingStart = FindEarliestOtherSectionHeadingStart(body);
        var fallback = headingStart is null ? body : body[..headingStart.Value];
        return fallback.Trim();
    }

    /// <summary>
    /// Sets the description. If <c>## Description</c> markers already
    /// exist, only their inner span changes. Otherwise, any free-text
    /// fallback content (see <see cref="GetDescription"/>) is replaced by a
    /// fresh marker block; if there was no free text either, a new section
    /// is inserted at its canonical position.
    /// </summary>
    public static string SetDescription(string body, string text)
    {
        var section = FindMarkerSection(body, DescriptionHeading, DescriptionBegin, DescriptionEnd, blankLineAfterHeading: true);
        if (section is not null)
        {
            return ReplaceSpan(body, section.Value, text);
        }

        var headingStart = FindEarliestOtherSectionHeadingStart(body);
        var fallbackEnd = headingStart ?? body.Length;
        var hasFallbackText = body[..fallbackEnd].Trim().Length > 0;
        var block = $"{DescriptionHeading}\n\n{DescriptionBegin}\n{text}\n{DescriptionEnd}\n";

        if (hasFallbackText)
        {
            return block + body[fallbackEnd..];
        }

        if (headingStart is not null)
        {
            return body[..headingStart.Value] + block + "\n" + body[headingStart.Value..];
        }

        return "\n" + block + "\n";
    }

    /// <summary>Parsed <c>- [ ] #n text</c> lines from the Acceptance Criteria section, renumbered 1..n by position. Empty if no section.</summary>
    public static IReadOnlyList<AcceptanceCriterion> GetAcceptanceCriteria(string body)
    {
        var section = FindMarkerSection(body, AcceptanceCriteriaHeading, AcceptanceCriteriaBegin, AcceptanceCriteriaEnd, blankLineAfterHeading: false);
        if (section is null)
        {
            return Array.Empty<AcceptanceCriterion>();
        }

        var inner = body.Substring(section.Value.InnerStart, section.Value.InnerLength);
        var items = new List<AcceptanceCriterion>();
        var index = 0;

        foreach (var rawLine in inner.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = AcceptanceCriterionLinePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            index++;
            var isChecked = match.Groups[1].Value is "x" or "X";
            items.Add(new AcceptanceCriterion(index, match.Groups[3].Value, isChecked));
        }

        return items;
    }

    /// <summary>Replaces the whole Acceptance Criteria list, renumbering items 1..n; inserts the section if missing.</summary>
    public static string SetAcceptanceCriteria(string body, IReadOnlyList<(string Text, bool Checked)> items)
    {
        var lines = new List<string>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var mark = items[i].Checked ? "x" : " ";
            // An AC is one checklist line: a raw line break would leave the
            // continuation text outside the "- [ ] #n" format, where the
            // parser can't see it and the next rewrite of the list drops it.
            var text = System.Text.RegularExpressions.Regex.Replace(items[i].Text ?? string.Empty, @"\s*[\r\n\u0085\u2028\u2029]\s*", " ").Trim();
            lines.Add($"- [{mark}] #{i + 1} {text}");
        }

        var inner = string.Join("\n", lines);

        var section = FindMarkerSection(body, AcceptanceCriteriaHeading, AcceptanceCriteriaBegin, AcceptanceCriteriaEnd, blankLineAfterHeading: false);
        if (section is not null)
        {
            return ReplaceSpan(body, section.Value, inner);
        }

        var block = $"{AcceptanceCriteriaHeading}\n{AcceptanceCriteriaBegin}\n{inner}\n{AcceptanceCriteriaEnd}";
        return InsertAtCanonicalPosition(body, AcceptanceCriteriaHeading, block);
    }

    public static string? GetImplementationPlan(string body) => GetOptionalSection(body, PlanHeading, PlanBegin, PlanEnd);

    public static string SetImplementationPlan(string body, string text) => SetOptionalSection(body, PlanHeading, PlanBegin, PlanEnd, text);

    public static string? GetImplementationNotes(string body) => GetOptionalSection(body, NotesHeading, NotesBegin, NotesEnd);

    public static string SetImplementationNotes(string body, string text) => SetOptionalSection(body, NotesHeading, NotesBegin, NotesEnd, text);

    public static string? GetFinalSummary(string body) => GetOptionalSection(body, FinalSummaryHeading, FinalSummaryBegin, FinalSummaryEnd);

    public static string SetFinalSummary(string body, string text) => SetOptionalSection(body, FinalSummaryHeading, FinalSummaryBegin, FinalSummaryEnd, text);

    private static string? GetOptionalSection(string body, string heading, string begin, string end)
    {
        var section = FindMarkerSection(body, heading, begin, end, blankLineAfterHeading: true);
        return section is null ? null : body.Substring(section.Value.InnerStart, section.Value.InnerLength);
    }

    private static string SetOptionalSection(string body, string heading, string begin, string end, string text)
    {
        var section = FindMarkerSection(body, heading, begin, end, blankLineAfterHeading: true);
        if (section is not null)
        {
            return ReplaceSpan(body, section.Value, text);
        }

        var block = $"{heading}\n\n{begin}\n{text}\n{end}";
        return InsertAtCanonicalPosition(body, heading, block);
    }

    private readonly record struct MarkerSection(int InnerStart, int InnerLength);

    private static MarkerSection? FindMarkerSection(string body, string heading, string begin, string end, bool blankLineAfterHeading)
    {
        var gap = blankLineAfterHeading ? @"\r?\n(?:[ \t]*\r?\n)?" : @"\r?\n";
        var pattern = $@"(?m)^{Regex.Escape(heading)}[ \t]*{gap}{Regex.Escape(begin)}\r?\n(.*?)\r?\n{Regex.Escape(end)}[ \t]*";
        var match = Regex.Match(body, pattern, RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var inner = match.Groups[1];
        return new MarkerSection(inner.Index, inner.Length);
    }

    private static string ReplaceSpan(string body, MarkerSection section, string replacement) =>
        body[..section.InnerStart] + replacement + body[(section.InnerStart + section.InnerLength)..];

    private static int? FindHeadingLineStart(string body, string heading)
    {
        var pattern = $@"(?m)^{Regex.Escape(heading)}[ \t]*$";
        var match = Regex.Match(body, pattern);
        return match.Success ? match.Index : null;
    }

    private static int? FindEarliestOtherSectionHeadingStart(string body)
    {
        int? earliest = null;
        foreach (var heading in CanonicalHeadingOrder)
        {
            if (heading == DescriptionHeading)
            {
                continue;
            }

            var index = FindHeadingLineStart(body, heading);
            if (index is not null && (earliest is null || index < earliest))
            {
                earliest = index;
            }
        }

        return earliest;
    }

    /// <summary>
    /// Inserts <paramref name="blockText"/> (a full section block, no
    /// trailing newline) immediately before the earliest already-present
    /// heading that canonically comes after <paramref name="headingLabel"/>,
    /// or appends it at the end of the body if none of the later sections
    /// exist yet.
    /// </summary>
    private static string InsertAtCanonicalPosition(string body, string headingLabel, string blockText)
    {
        var kindIndex = Array.IndexOf(CanonicalHeadingOrder, headingLabel);
        for (var i = kindIndex + 1; i < CanonicalHeadingOrder.Length; i++)
        {
            var laterStart = FindHeadingLineStart(body, CanonicalHeadingOrder[i]);
            if (laterStart is not null)
            {
                return body[..laterStart.Value] + blockText + "\n\n" + body[laterStart.Value..];
            }
        }

        if (body.Trim().Length == 0)
        {
            return "\n" + blockText + "\n";
        }

        var trimmedEnd = body.TrimEnd('\n', '\r');
        var tail = body[trimmedEnd.Length..];
        return trimmedEnd + "\n\n" + blockText + "\n" + tail;
    }
}
