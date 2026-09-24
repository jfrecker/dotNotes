namespace DotNotes.Core.Config;

/// <summary>
/// Configuration for the Tasks &amp; Kanban feature (Phase 12,
/// docs/features/tasks-kanban/PLAN.md). Statuses/prefix/folder are server
/// config (appsettings/env), not UI-editable - see that plan's "Decisions
/// made autonomously" #10.
/// </summary>
public sealed class TasksOptions
{
    /// <summary>
    /// The <c>appsettings.json</c> section name this binds to (also the
    /// environment-variable prefix, e.g. <c>Tasks__Folder</c>).
    /// </summary>
    public const string SectionName = "Tasks";

    /// <summary>
    /// Vault-relative default folder new tasks are created under (unless a
    /// create request specifies its own folder). A completed task's note
    /// lives at <c>&lt;its own directory&gt;/Completed/</c> - see
    /// <see cref="Tasks.TaskFolders"/> - not necessarily under this folder,
    /// since a task can be created in any folder via <c>TaskCreateRequest.Folder</c>.
    /// </summary>
    public string Folder { get; set; } = "Task";

    /// <summary>Prefix used for generated task ids, e.g. <c>TASK</c> -&gt; <c>TASK-12</c>.</summary>
    public string IdPrefix { get; set; } = "TASK";

    /// <summary>
    /// Configured board columns/workflow statuses, in display order. See
    /// <see cref="Tasks.TaskStatuses.GetEffectiveStatuses"/> for how this is
    /// turned into the actual, Backlog-first column list - a task whose
    /// status isn't in the effective list still shows up (in the Backlog
    /// column, not a trailing column of its own, per
    /// docs/features/tasks-kanban/PLAN.md's v0.2.1 update).
    /// </summary>
    public List<string> Statuses { get; set; } = new() { "Backlog", "To Do", "In Progress", "Done" };

    /// <summary>
    /// Status assigned to a newly created task when none is specified.
    /// <see langword="null"/>/empty means "the first effective status"
    /// (Backlog) - see <see cref="Tasks.TaskStatuses.GetEffectiveStatuses"/>.
    /// </summary>
    public string? DefaultStatus { get; set; }

    /// <summary>
    /// The status that always leads the effective status list (the leftmost
    /// board column), regardless of where - or whether - it appears in
    /// <see cref="Statuses"/>. See <see cref="Tasks.TaskStatuses.GetEffectiveStatuses"/>.
    /// </summary>
    public string BacklogStatus { get; set; } = "Backlog";

    /// <summary>
    /// The status <see cref="Tasks.ITaskService.CompleteAsync"/> sets on a
    /// task (and moves its note into a <see cref="Tasks.TaskFolders.Completed"/>
    /// subfolder for). See <see cref="Tasks.TaskStatuses.GetEffectiveCompletedStatus"/>.
    /// </summary>
    public string CompletedStatus { get; set; } = "Done";

    /// <summary>Allowed priority values (case-insensitive).</summary>
    public List<string> Priorities { get; set; } = new() { "high", "medium", "low" };
}
