namespace DotNotes.Core.Tasks;

/// <summary>One kanban column: a status plus its (already sorted, non-archived) tasks.</summary>
public sealed record TaskColumn(string Status, IReadOnlyList<TaskItem> Tasks);

/// <summary>
/// The result of <see cref="ITaskService.GetBoard"/>: one column per
/// configured status (in configured order), plus a trailing column for
/// each distinct status present among matching tasks that isn't in the
/// configured list, per docs/features/tasks-kanban/PLAN.md §2 ("Unknown
/// status"). <see cref="Revision"/> mirrors <see cref="ITaskIndex.Revision"/>
/// at the moment the board was built, for the polling <c>GET /api/tasks/revision</c>
/// live-refresh contract.
/// </summary>
public sealed record TaskBoard(IReadOnlyList<TaskColumn> Columns, long Revision);
