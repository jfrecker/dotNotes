using System.ComponentModel;
using DotNotes.Core.Notes;
using DotNotes.Core.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DotNotes.Api.Mcp;

/// <summary>
/// Implements every MCP tool listed in docs/features/tasks-kanban/PLAN.md
/// §6 / docs/05-MCP-SPEC.md's Tasks section. Hosted in-process at
/// <c>/mcp</c> alongside <see cref="DotNotesMcpTools"/> (a separate
/// <see cref="McpServerToolType"/> so the two features stay independently
/// registerable/testable), calling straight into the same
/// <see cref="ITaskService"/> the tasks REST endpoints use - there is only
/// ever one code path that reads or writes a task note.
/// </summary>
/// <remarks>
/// Task ids (not vault paths) are the caller-supplied identifier for every
/// tool here, so path-escape validation doesn't apply the way it does for
/// <see cref="DotNotesMcpTools"/>'s note-path tools; instead, every
/// <see cref="ITaskService"/> failure mode (unknown id, invalid
/// status/priority, etc) is funneled through <see cref="McpException"/> the
/// same way, so a calling assistant only ever sees a structured tool error,
/// never a raw .NET exception.
/// </remarks>
[McpServerToolType]
public sealed class DotNotesTaskMcpTools(ITaskService taskService)
{
    private const int DefaultSearchLimit = 10;

    [McpServerTool(Name = "list_tasks")]
    [Description("List tasks, optionally filtered. Valid status/priority values come from this instance's configuration - call get_board to see the current columns. Task ids look like TASK-12.")]
    public IReadOnlyList<TaskSummaryDto> ListTasks(
        [Description("Filter by exact status name.")] string? status = null,
        [Description("Filter by label.")] string? label = null,
        [Description("Filter by assignee, e.g. '@jonathan'.")] string? assignee = null,
        [Description("Filter by priority, e.g. 'high'.")] string? priority = null,
        [Description("Filter by milestone.")] string? milestone = null,
        [Description("Include archived tasks (default false).")] bool includeArchived = false,
        [Description("Maximum number of results to return.")] int? limit = null)
    {
        var filter = new TaskFilter
        {
            Status = status,
            Label = label,
            Assignee = assignee,
            Priority = priority,
            Milestone = milestone,
            IncludeArchived = includeArchived,
        };

        var tasks = taskService.List(filter);
        if (limit is > 0)
        {
            tasks = tasks.Take(limit.Value).ToArray();
        }

        return tasks.Select(ToSummary).ToArray();
    }

    [McpServerTool(Name = "get_task")]
    [Description("Get full details of one task by id (e.g. 'TASK-12'): description, acceptance criteria, implementation plan/notes, final summary.")]
    public TaskDto GetTask(
        [Description("Task id, e.g. 'TASK-12'.")] string id)
    {
        var task = taskService.GetById(id) ?? throw new McpException($"No task exists with id '{id}'.");
        return ToDto(task);
    }

    [McpServerTool(Name = "create_task")]
    [Description("Create a new task. Search with search_tasks first to avoid duplicates. Status/priority must match this instance's configured values (see get_board). Write description as the 'why' and acceptanceCriteria as testable outcomes - leave implementation detail for update_task's planSet once work starts.")]
    public async Task<TaskDto> CreateTask(
        [Description("Task title.")] string title,
        [Description("Why this task exists and what outcome is wanted - not implementation detail.")] string? description = null,
        [Description("Initial status; defaults to this instance's configured default status.")] string? status = null,
        [Description("Priority, e.g. 'high'/'medium'/'low' (configurable).")] string? priority = null,
        [Description("Assignees, e.g. ['@jonathan'].")] IReadOnlyList<string>? assignee = null,
        [Description("Labels.")] IReadOnlyList<string>? labels = null,
        [Description("Milestone.")] string? milestone = null,
        [Description("Ids of tasks this depends on, e.g. ['TASK-3'].")] IReadOnlyList<string>? dependencies = null,
        [Description("Acceptance criteria as testable outcome statements; all start unchecked.")] IReadOnlyList<string>? acceptanceCriteria = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await taskService.CreateAsync(
                new TaskCreateRequest
                {
                    Title = title,
                    Description = description,
                    Status = status,
                    Priority = priority,
                    Assignee = assignee,
                    Labels = labels,
                    Milestone = milestone,
                    Dependencies = dependencies,
                    AcceptanceCriteria = acceptanceCriteria,
                },
                cancellationToken).ConfigureAwait(false);

            return ToDto(task);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            throw ToMcpException(ex);
        }
    }

    [McpServerTool(Name = "update_task")]
    [Description("Patch an existing task by id. Only supplied fields change; passing \"\" for priority, milestone, description, planSet, notesSet or finalSummary clears it (title cannot be cleared), and supplying assignee/labels/dependencies replaces that whole list. Use the acceptanceCriteria* fields to edit the checklist rather than touching description.")]
    public async Task<TaskDto> UpdateTask(
        [Description("Task id, e.g. 'TASK-12'.")] string id,
        [Description("New title (cannot be empty).")] string? title = null,
        [Description("New status - must be a configured status (see get_board).")] string? status = null,
        [Description("New priority.")] string? priority = null,
        [Description("Replaces the whole assignee list.")] IReadOnlyList<string>? assignee = null,
        [Description("Replaces the whole labels list.")] IReadOnlyList<string>? labels = null,
        [Description("New milestone.")] string? milestone = null,
        [Description("Replaces the whole dependencies list, e.g. ['TASK-3'].")] IReadOnlyList<string>? dependencies = null,
        [Description("Replaces the description text.")] string? description = null,
        [Description("Appends new unchecked acceptance criteria after the existing ones.")] IReadOnlyList<string>? acceptanceCriteriaAdd = null,
        [Description("1-based acceptance criteria indexes to remove (applied after adds, before check/uncheck).")] IReadOnlyList<int>? acceptanceCriteriaRemove = null,
        [Description("1-based acceptance criteria indexes to check - only once you have real evidence they're true.")] IReadOnlyList<int>? acceptanceCriteriaCheck = null,
        [Description("1-based acceptance criteria indexes to uncheck.")] IReadOnlyList<int>? acceptanceCriteriaUncheck = null,
        [Description("Replaces the implementation plan text.")] string? planSet = null,
        [Description("Appends text to the implementation plan.")] string? planAppend = null,
        [Description("Replaces the implementation notes text.")] string? notesSet = null,
        [Description("Appends text to the implementation notes.")] string? notesAppend = null,
        [Description("Sets the final summary (PR-description style: what changed and why). Write this once work is verified, just before moving the task to Done.")] string? finalSummary = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var update = new TaskUpdate
            {
                Title = title,
                Status = status,
                Priority = priority,
                Assignee = assignee,
                Labels = labels,
                Milestone = milestone,
                Dependencies = dependencies,
                Description = description,
                AcceptanceCriteriaAdd = acceptanceCriteriaAdd,
                AcceptanceCriteriaRemove = acceptanceCriteriaRemove,
                AcceptanceCriteriaCheck = acceptanceCriteriaCheck,
                AcceptanceCriteriaUncheck = acceptanceCriteriaUncheck,
                ImplementationPlan = planSet,
                PlanAppend = planAppend,
                ImplementationNotes = notesSet,
                NotesAppend = notesAppend,
                FinalSummary = finalSummary,
            };

            var task = await taskService.UpdateAsync(id, update, cancellationToken).ConfigureAwait(false);
            return ToDto(task);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            throw ToMcpException(ex);
        }
    }

    [McpServerTool(Name = "move_task")]
    [Description("Move a task to a different status column, optionally at a specific position within it: pass beforeId (another task's id in the destination column - the task is inserted immediately before it) or a 0-based index among the column's other tasks; beforeId wins if both are given. Status must be a configured status (see get_board) or the task's current one; omit both to place the task at the end of the column.")]
    public async Task<TaskDto> MoveTask(
        [Description("Task id, e.g. 'TASK-12'.")] string id,
        [Description("Destination status - must be a configured status (see get_board).")] string status,
        [Description("0-based target position among the destination column's other tasks; omit for the end.")] int? index = null,
        [Description("Id of another task in the destination column to insert this one immediately before; takes precedence over index.")] string? beforeId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await taskService.MoveAsync(id, status, index, beforeId, cancellationToken).ConfigureAwait(false);
            return ToDto(task);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            throw ToMcpException(ex);
        }
    }

    [McpServerTool(Name = "archive_task")]
    [Description("Archive a task (moves its note under the tasks folder's archive subfolder). Use only for duplicates or cancelled work - completed work should be moved to a Done-like status with move_task instead.")]
    public async Task<TaskDto> ArchiveTask(
        [Description("Task id, e.g. 'TASK-12'.")] string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await taskService.ArchiveAsync(id, cancellationToken).ConfigureAwait(false);
            return ToDto(task);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            throw ToMcpException(ex);
        }
    }

    [McpServerTool(Name = "get_board")]
    [Description("Get the kanban board: one column per configured status, in configured order, plus a trailing column for any status in use that isn't configured. Use this to discover the valid status names for create_task/update_task/move_task.")]
    public BoardDto GetBoard(
        [Description("Filter by exact status name.")] string? status = null,
        [Description("Filter by label.")] string? label = null,
        [Description("Filter by assignee, e.g. '@jonathan'.")] string? assignee = null,
        [Description("Filter by priority.")] string? priority = null,
        [Description("Filter by milestone.")] string? milestone = null)
    {
        var filter = new TaskFilter
        {
            Status = status,
            Label = label,
            Assignee = assignee,
            Priority = priority,
            Milestone = milestone,
        };

        var board = taskService.GetBoard(filter);
        var columns = board.Columns
            .Select(c => new BoardColumnDto(c.Status, c.Tasks.Select(ToSummary).ToArray()))
            .ToArray();

        return new BoardDto(columns);
    }

    [McpServerTool(Name = "search_tasks")]
    [Description("Search tasks by id, title, description, labels or assignee. Call this (or list_tasks) before create_task to avoid creating a duplicate.")]
    public IReadOnlyList<TaskSummaryDto> SearchTasks(
        [Description("Search text.")] string query,
        [Description("Maximum number of results to return (default 10).")] int limit = DefaultSearchLimit)
    {
        var effectiveLimit = limit > 0 ? limit : DefaultSearchLimit;
        return taskService.Search(query, effectiveLimit).Select(ToSummary).ToArray();
    }

    [McpServerTool(Name = "get_task_workflow")]
    [Description("Return a markdown guide for how an assistant should use the task tools: when to create a task, how to write one, and how to execute and finalize it. Same content as the dotnotes://workflow/tasks resource, for clients that don't read MCP resources.")]
    public string GetTaskWorkflow() => TaskWorkflowGuide.Markdown;

    /// <summary>
    /// The failure modes <see cref="ITaskService"/> writes can raise, mirroring
    /// <c>TasksEndpoints</c>' HTTP mapping (404/400/409/503).
    /// </summary>
    private static bool IsExpectedFailure(Exception ex) => ex is
        TaskNotFoundException or
        TaskValidationException or
        DestinationAlreadyExistsException or
        InvalidNotePathException or
        VaultUnavailableException or
        IOException;

    private static McpException ToMcpException(Exception ex) => ex switch
    {
        TaskNotFoundException or TaskValidationException => new McpException(ex.Message),
        DestinationAlreadyExistsException => new McpException($"A note already exists at the destination path: {ex.Message}"),
        InvalidNotePathException => new McpException($"Invalid note path: {ex.Message}"),
        VaultUnavailableException => new McpException($"The vault is unavailable: {ex.Message}"),
        _ => new McpException($"File system error while updating the task: {ex.Message}"),
    };

    private static TaskSummaryDto ToSummary(TaskItem task) => new(
        task.Id,
        task.Title,
        task.Status,
        task.Assignee,
        task.Labels,
        task.Priority,
        task.Milestone,
        task.Dependencies,
        task.CreatedDate,
        task.UpdatedDate,
        task.Ordinal,
        task.Path,
        task.Archived,
        task.Excerpt,
        task.AcceptanceCriteria.Count,
        task.AcceptanceCriteria.Count(ac => ac.Checked));

    private static TaskDto ToDto(TaskItem task) => new(
        task.Id,
        task.Title,
        task.Status,
        task.Assignee,
        task.Labels,
        task.Priority,
        task.Milestone,
        task.Dependencies,
        task.CreatedDate,
        task.UpdatedDate,
        task.Ordinal,
        task.Path,
        task.Archived,
        task.Excerpt,
        task.AcceptanceCriteria.Count,
        task.AcceptanceCriteria.Count(ac => ac.Checked),
        task.Description,
        task.AcceptanceCriteria.Select(ac => new AcceptanceCriterionDto(ac.Index, ac.Text, ac.Checked)).ToArray(),
        task.ImplementationPlan,
        task.ImplementationNotes,
        task.FinalSummary,
        task.UpdatedAt);
}

/// <summary>Backs the acceptance-criteria entries in <see cref="TaskDto"/>: <c>{ index, text, checked }</c>.</summary>
public sealed record AcceptanceCriterionDto(int Index, string Text, bool Checked);

/// <summary>
/// Backs <c>list_tasks</c>/<c>search_tasks</c>/<c>get_board</c>'s per-task
/// entries, per docs/features/tasks-kanban/PLAN.md §6's <c>TaskSummary</c> shape.
/// </summary>
public sealed record TaskSummaryDto(
    string Id,
    string Title,
    string Status,
    IReadOnlyList<string> Assignee,
    IReadOnlyList<string> Labels,
    string? Priority,
    string? Milestone,
    IReadOnlyList<string> Dependencies,
    DateTimeOffset? CreatedDate,
    DateTimeOffset? UpdatedDate,
    double? Ordinal,
    string Path,
    bool Archived,
    string Excerpt,
    int AcTotal,
    int AcChecked);

/// <summary>
/// Backs <c>get_task</c>/<c>create_task</c>/<c>update_task</c>/<c>move_task</c>/<c>archive_task</c>'s
/// full task shape: every <see cref="TaskSummaryDto"/> field plus description,
/// acceptance criteria, implementation plan/notes, final summary and the
/// note's last-write timestamp.
/// </summary>
public sealed record TaskDto(
    string Id,
    string Title,
    string Status,
    IReadOnlyList<string> Assignee,
    IReadOnlyList<string> Labels,
    string? Priority,
    string? Milestone,
    IReadOnlyList<string> Dependencies,
    DateTimeOffset? CreatedDate,
    DateTimeOffset? UpdatedDate,
    double? Ordinal,
    string Path,
    bool Archived,
    string Excerpt,
    int AcTotal,
    int AcChecked,
    string Description,
    IReadOnlyList<AcceptanceCriterionDto> AcceptanceCriteria,
    string? ImplementationPlan,
    string? ImplementationNotes,
    string? FinalSummary,
    DateTimeOffset UpdatedAt);

/// <summary>Backs one <c>get_board</c> column: <c>{ status, tasks }</c>.</summary>
public sealed record BoardColumnDto(string Status, IReadOnlyList<TaskSummaryDto> Tasks);

/// <summary>Backs <c>get_board</c>'s <c>{ columns: [...] }</c> response.</summary>
public sealed record BoardDto(IReadOnlyList<BoardColumnDto> Columns);
