namespace DotNotes.Core.Tasks;

/// <summary>
/// One frontmatter key <see cref="TaskMarkdown"/> doesn't recognise,
/// captured as its exact original YAML text (<c>"key: value"</c>, possibly
/// spanning multiple lines for a block list) so it can be re-emitted
/// byte-for-byte after the known keys, in original order, per
/// docs/features/tasks-kanban/PLAN.md §2/§3.
/// </summary>
public sealed record TaskFrontmatterField(string Key, string RawText);

/// <summary>
/// The strongly-typed subset of a task note's YAML frontmatter that
/// <see cref="TaskMarkdown"/> understands, plus every other key preserved
/// verbatim in <see cref="UnknownFields"/>. <see cref="Title"/> may be
/// <see langword="null"/>/empty (detection only requires <c>id</c> and
/// <c>status</c>) - callers apply the filename-derived fallback.
/// </summary>
public sealed class TaskFrontmatterData
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public required string Status { get; init; }
    public IReadOnlyList<string> Assignee { get; init; } = Array.Empty<string>();
    public string? Reporter { get; init; }
    public DateTimeOffset? CreatedDate { get; init; }
    public DateTimeOffset? UpdatedDate { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
    public string? Milestone { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public string? Priority { get; init; }
    public double? Ordinal { get; init; }
    public IReadOnlyList<TaskFrontmatterField> UnknownFields { get; init; } = Array.Empty<TaskFrontmatterField>();
}

/// <summary>
/// A parsed task note: <see cref="Frontmatter"/> plus the raw, untouched
/// body text (everything after the closing <c>---</c> line, including its
/// leading newline) - see <see cref="TaskMarkdown"/>'s remarks for why the
/// body is edited by span, not rebuilt, to keep unrelated content
/// byte-stable.
/// </summary>
public sealed class TaskDocument
{
    public required TaskFrontmatterData Frontmatter { get; init; }
    public required string Body { get; init; }

    /// <summary>Whether the original content started with a UTF-8 BOM.</summary>
    public bool HasBom { get; init; }
}
