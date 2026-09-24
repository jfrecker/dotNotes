namespace DotNotes.Core.Tasks;

/// <summary>One kanban column: a status plus its (already sorted, non-completed) tasks.</summary>
public sealed record TaskColumn(string Status, IReadOnlyList<TaskItem> Tasks, bool IsBacklog);

/// <summary>
/// The result of <see cref="ITaskService.GetBoard"/>: one column per
/// effective status (see <see cref="TaskStatuses.GetEffectiveStatuses"/>),
/// Backlog first, in order - a task whose raw status is empty or doesn't
/// match any other effective status lands in the Backlog column rather than
/// a trailing column of its own, per docs/features/tasks-kanban/PLAN.md's
/// v0.2.1 update. <see cref="Revision"/> mirrors <see cref="ITaskIndex.Revision"/>
/// at the moment the board was built, for the polling <c>GET /api/tasks/revision</c>
/// live-refresh contract.
/// </summary>
public sealed record TaskBoard(IReadOnlyList<TaskColumn> Columns, long Revision);
