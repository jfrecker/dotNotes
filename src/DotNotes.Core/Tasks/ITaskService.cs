namespace DotNotes.Core.Tasks;

/// <summary>
/// Read-modify-write task operations layered over
/// <see cref="Notes.INoteRepository"/>/<see cref="Reorganization.IVaultReorganizationService"/>,
/// per docs/features/tasks-kanban/PLAN.md §3. Both REST endpoints and MCP
/// tools call this - there is only ever one code path that reads or writes
/// a task note, the same shape as every other DotNotes.Core service.
/// </summary>
public interface ITaskService
{
    /// <summary>Matching, non-completed (unless <see cref="TaskFilter.IncludeCompleted"/>) tasks, sorted per <see cref="TaskOrdering.Comparer"/> within each effective-status column, then by column order (Backlog first - see <see cref="TaskStatuses.GetEffectiveStatuses"/>).</summary>
    IReadOnlyList<TaskItem> List(TaskFilter filter);

    /// <summary>One column per effective status (Backlog first, see <see cref="TaskStatuses.GetEffectiveStatuses"/>); a task with an empty or unrecognised status lands in the Backlog column rather than a trailing column of its own.</summary>
    TaskBoard GetBoard(TaskFilter filter);

    /// <summary>Case-insensitive substring/token match over id, title, description, labels, assignee. Id/title hits rank first. Excludes completed tasks - same shape as <see cref="Search(string, int, bool)"/> with <c>includeCompleted: false</c>.</summary>
    IReadOnlyList<TaskItem> Search(string query, int limit);

    /// <summary>
    /// Like <see cref="Search(string, int)"/>, but includes completed tasks
    /// too when <paramref name="includeCompleted"/> is <see langword="true"/>
    /// (mirrors <see cref="TaskFilter.IncludeCompleted"/> for
    /// <see cref="List"/>/<see cref="GetBoard"/>).
    /// </summary>
    IReadOnlyList<TaskItem> Search(string query, int limit, bool includeCompleted);

    /// <summary>Looks up a task by id, case-insensitively. <see langword="null"/> if not found.</summary>
    TaskItem? GetById(string id);

    /// <summary>Creates a new task note. Throws <see cref="TaskValidationException"/> if the request is invalid.</summary>
    Task<TaskItem> CreateAsync(TaskCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a patch to the task's fresh, on-disk state. Throws
    /// <see cref="TaskNotFoundException"/> if <paramref name="id"/> doesn't
    /// exist, <see cref="TaskValidationException"/> if the patch is
    /// invalid. Renames the note (rewriting incoming wikilinks) if the
    /// title changes and the current file name is still id-derived.
    /// </summary>
    Task<TaskItem> UpdateAsync(string id, TaskUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a task to the board column named <paramref name="status"/>
    /// (one of <see cref="TaskStatuses.GetEffectiveStatuses"/>, or the
    /// task's own current status), optionally at a specific 0-based
    /// <paramref name="index"/> among the destination column's other
    /// (non-completed) tasks. Absent/too-large index means "end". Moving
    /// into the Backlog column sets the task's status to the effective
    /// Backlog status, <i>except</i> when the task already belongs to the
    /// Backlog column (an empty/unrecognised/Backlog raw status) - a
    /// reorder within Backlog keeps the task's raw status untouched and
    /// only changes its ordinal.
    /// </summary>
    Task<TaskItem> MoveAsync(string id, string status, int? index, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="MoveAsync(string, string, int?, CancellationToken)"/>,
    /// but when <paramref name="beforeTaskId"/> is supplied the task is
    /// inserted immediately before that task (which must be another active
    /// task in the destination column, else <see cref="TaskValidationException"/>);
    /// it takes precedence over <paramref name="index"/>, which is ambiguous
    /// when the caller's view of the column is filtered. Moving a task to
    /// where it already is (same column, same position) is a no-op that
    /// writes nothing.
    /// </summary>
    Task<TaskItem> MoveAsync(string id, string status, int? index, string? beforeTaskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the task complete: sets its status to
    /// <see cref="TaskStatuses.GetEffectiveCompletedStatus"/> and moves its
    /// note into a <see cref="TaskFolders.Completed"/> subfolder next to its
    /// current location (rewriting incoming wikilinks), creating that
    /// subfolder if needed and appending a numeric suffix on a name
    /// collision (never overwriting). A no-op beyond ensuring the status if
    /// the task's note is already under a <see cref="TaskFolders.Completed"/>
    /// folder (idempotent - never nests <c>Completed/Completed</c>).
    /// </summary>
    Task<TaskItem> CompleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts an existing plain note into a task: adds frontmatter (new
    /// id, title from the filename stem, <paramref name="status"/> or the
    /// default, current dates), keeps the body as the description
    /// (fallback), and renames the note to
    /// <c>&lt;ID&gt; - &lt;Title&gt;.md</c> in its current folder.
    /// </summary>
    /// <exception cref="TaskNotFoundException">No note exists at <paramref name="path"/>.</exception>
    /// <exception cref="TaskValidationException">The note at <paramref name="path"/> is already a task.</exception>
    Task<TaskItem> ConvertNoteAsync(string path, string? status, CancellationToken cancellationToken = default);
}
