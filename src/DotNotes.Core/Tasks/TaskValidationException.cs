namespace DotNotes.Core.Tasks;

/// <summary>
/// A task create/update/move request failed validation (empty title,
/// status/priority not in the configured list, etc). Callers map this to
/// HTTP 400 <c>invalid_request</c> per docs/features/tasks-kanban/PLAN.md §3.
/// </summary>
public sealed class TaskValidationException : Exception
{
    public TaskValidationException(string message) : base(message)
    {
    }
}
