namespace DotNotes.Core.Tasks;

/// <summary>
/// Filter criteria for <see cref="ITaskService.List"/>/<see cref="ITaskService.GetBoard"/>,
/// mirroring the query parameters on <c>GET /api/tasks</c>/<c>GET /api/tasks/board</c>
/// per docs/features/tasks-kanban/PLAN.md §4. Every field is optional;
/// <see langword="null"/>/empty means "don't filter on this".
/// </summary>
public sealed record TaskFilter
{
    public string? Status { get; init; }
    public string? Label { get; init; }
    public string? Assignee { get; init; }
    public string? Priority { get; init; }
    public string? Milestone { get; init; }

    /// <summary>Free-text query, matched the same way as <see cref="ITaskService.Search"/>.</summary>
    public string? Query { get; init; }

    /// <summary>
    /// Whether completed tasks (see <see cref="TaskItem.Completed"/>) are
    /// included. Defaults to <see langword="false"/>.
    /// </summary>
    public bool IncludeCompleted { get; init; }

    public static readonly TaskFilter Empty = new();
}
