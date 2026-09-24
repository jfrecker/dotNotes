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
    /// create request specifies its own folder). The archive lives at
    /// <c>&lt;Folder&gt;/archive</c>.
    /// </summary>
    public string Folder { get; set; } = "tasks";

    /// <summary>Prefix used for generated task ids, e.g. <c>TASK</c> -&gt; <c>TASK-12</c>.</summary>
    public string IdPrefix { get; set; } = "TASK";

    /// <summary>
    /// Configured board columns/workflow statuses, in display order. A task
    /// whose status isn't in this list still shows up in an extra trailing
    /// board column for that status, per docs/features/tasks-kanban/PLAN.md.
    /// </summary>
    public List<string> Statuses { get; set; } = new() { "To Do", "In Progress", "Done" };

    /// <summary>
    /// Status assigned to a newly created task when none is specified.
    /// <see langword="null"/>/empty means "the first entry of <see cref="Statuses"/>".
    /// </summary>
    public string? DefaultStatus { get; set; }

    /// <summary>Allowed priority values (case-insensitive).</summary>
    public List<string> Priorities { get; set; } = new() { "high", "medium", "low" };
}
