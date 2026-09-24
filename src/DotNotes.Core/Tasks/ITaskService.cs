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
    /// <summary>Matching, non-archived (unless <see cref="TaskFilter.IncludeArchived"/>) tasks, sorted per <see cref="TaskOrdering.Comparer"/> within each configured status group, then by status order.</summary>
    IReadOnlyList<TaskItem> List(TaskFilter filter);

    /// <summary>One column per configured status (in configured order), plus a trailing column per unknown status present among matching tasks.</summary>
    TaskBoard GetBoard(TaskFilter filter);

    /// <summary>Case-insensitive substring/token match over id, title, description, labels, assignee. Id/title hits rank first.</summary>
    IReadOnlyList<TaskItem> Search(string query, int limit);

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
    /// Moves a task to <paramref name="status"/>, optionally at a specific
    /// 0-based <paramref name="index"/> among the destination column's
    /// other (non-archived) tasks. Absent/too-large index means "end".
    /// </summary>
    Task<TaskItem> MoveAsync(string id, string status, int? index, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="MoveAsync(string, string, int?, CancellationToken)"/>,
    /// but when <paramref name="beforeTaskId"/> is supplied the task is
    /// inserted immediately before that task (which must be another active
    /// task in the destination column, else <see cref="TaskValidationException"/>);
    /// it takes precedence over <paramref name="index"/>, which is ambiguous
    /// when the caller's view of the column is filtered. Moving a task to
    /// where it already is (same status, same position) is a no-op that
    /// writes nothing. The task's own current status (and any status some
    /// task already holds) is accepted as the destination even if it isn't
    /// one of the configured statuses.
    /// </summary>
    Task<TaskItem> MoveAsync(string id, string status, int? index, string? beforeTaskId, CancellationToken cancellationToken = default);

    /// <summary>Archives the task (moves its note under <c>&lt;Tasks:Folder&gt;/archive/</c>, rewriting incoming wikilinks).</summary>
    Task<TaskItem> ArchiveAsync(string id, CancellationToken cancellationToken = default);

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
