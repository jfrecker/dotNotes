namespace DotNotes.Core.Tasks;

/// <summary>
/// No task exists with the given id (or path, for <see cref="ITaskService.ConvertNoteAsync"/>'s
/// "note not found" case). Callers map this to HTTP 404 <c>not_found</c>
/// per docs/features/tasks-kanban/PLAN.md §3.
/// </summary>
public sealed class TaskNotFoundException : Exception
{
    public string TaskId { get; }

    public TaskNotFoundException(string taskId)
        : base($"No task exists with id '{taskId}'.")
    {
        TaskId = taskId;
    }

    public TaskNotFoundException(string taskId, string message)
        : base(message)
    {
        TaskId = taskId;
    }
}
