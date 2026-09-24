namespace DotNotes.Core.Tasks;

/// <summary>
/// A task, as read from a note's frontmatter + body sections. Backs
/// <c>Task</c>/<c>TaskSummary</c> in docs/04-API-SPEC.md's Tasks section
/// (the REST layer decides which subset of fields to serialize for each
/// shape - this type carries everything).
/// </summary>
public sealed record TaskItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public IReadOnlyList<string> Assignee { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
    public string? Reporter { get; init; }
    public string? Priority { get; init; }
    public string? Milestone { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public DateTimeOffset? CreatedDate { get; init; }
    public DateTimeOffset? UpdatedDate { get; init; }

    /// <summary>Board/list sort key; <see langword="null"/> sorts last, per <see cref="TaskOrdering"/>.</summary>
    public double? Ordinal { get; init; }

    /// <summary>Vault-relative path of the note backing this task.</summary>
    public required string Path { get; init; }

    /// <summary>The note file's last-write timestamp, UTC (distinct from the frontmatter's <c>updated_date</c>).</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// <see langword="true"/> if any directory segment of <see cref="Path"/>
    /// is exactly <see cref="TaskFolders.Completed"/> (ordinal,
    /// case-sensitive - see <see cref="TaskFolders.IsInCompletedFolder"/>),
    /// per docs/features/tasks-kanban/PLAN.md's v0.2.1 "Complete" update.
    /// </summary>
    public bool Completed { get; init; }

    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria { get; init; } = Array.Empty<AcceptanceCriterion>();
    public string? ImplementationPlan { get; init; }
    public string? ImplementationNotes { get; init; }
    public string? FinalSummary { get; init; }

    /// <summary>
    /// First ~160 characters of <see cref="Description"/>, with markdown
    /// syntax roughly stripped, for card/list display.
    /// </summary>
    public string Excerpt => BuildExcerpt(Description);

    private static string BuildExcerpt(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        // Rough markdown stripping: collapse whitespace/newlines, drop the
        // most common inline syntax markers. Not a full markdown parser -
        // good enough for a one-line card excerpt.
        var text = description
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '#' or '*' or '_' or '`' or '>')
            {
                continue;
            }

            builder.Append(c);
        }

        var collapsed = System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        return collapsed.Length <= 160 ? collapsed : collapsed[..160];
    }
}
