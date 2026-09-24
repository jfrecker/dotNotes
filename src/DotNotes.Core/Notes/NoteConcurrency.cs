namespace DotNotes.Core.Notes;

/// <summary>
/// Optimistic-concurrency helper for <c>PUT /api/notes/{**path}</c>'s
/// opt-in <c>expectedUpdatedAt</c> parameter, per
/// docs/features/tasks-kanban/PLAN.md §3 ("Concurrent edits"). Kept as a
/// tiny, framework-free <c>DotNotes.Core</c> helper - rather than adding a
/// new parameter/overload to <see cref="INoteRepository.SaveAsync"/> -
/// so the endpoint layer (an api-developer's concern) can implement the
/// read-compare-then-write itself without changing this stable interface's
/// signature, while the actual 1&#160;ms-tolerance comparison lives in one
/// place both REST and any future caller share.
/// </summary>
/// <remarks>
/// Usage from an endpoint: <see cref="INoteRepository.GetAsync"/> the note
/// first, call <see cref="HasConflict"/> with the caller-supplied
/// <c>expectedUpdatedAt</c> (or <see langword="null"/> to skip the check
/// entirely - today's last-write-wins behaviour, e.g. MCP's
/// <c>update_note</c> and older clients) against the note's current
/// <see cref="NoteContent.UpdatedAt"/>; if it reports a conflict, respond
/// <c>409 conflict</c> with the current <c>updatedAt</c> instead of calling
/// <see cref="INoteRepository.SaveAsync"/>. This is a plain read-then-write
/// check (there is no cross-process file lock), which is an accepted
/// trade-off for a single-user, personal vault - see this feature's plan
/// for why.
/// </remarks>
public static class NoteConcurrency
{
    /// <summary>
    /// Two timestamps within this tolerance of each other are treated as
    /// "the same version" - filesystem timestamp precision and
    /// serialization round-trips (e.g. through JSON) can otherwise differ
    /// by sub-millisecond amounts even when nothing actually changed.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// <see langword="true"/> if <paramref name="expectedUpdatedAt"/> was
    /// supplied and differs from <paramref name="currentUpdatedAt"/> by
    /// more than <see cref="Tolerance"/> - i.e. the caller's copy is stale
    /// and the write should be rejected with <c>409 conflict</c> rather
    /// than proceeding. Always <see langword="false"/> when
    /// <paramref name="expectedUpdatedAt"/> is <see langword="null"/>
    /// (concurrency checking is opt-in).
    /// </summary>
    public static bool HasConflict(DateTimeOffset? expectedUpdatedAt, DateTimeOffset currentUpdatedAt)
    {
        if (expectedUpdatedAt is null)
        {
            return false;
        }

        return (currentUpdatedAt - expectedUpdatedAt.Value).Duration() > Tolerance;
    }
}
