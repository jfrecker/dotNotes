using DotNotes.Core.Vault;

namespace DotNotes.Core.Tasks;

/// <summary>
/// A derived-cache index of every task note in the vault, kept live the
/// same way <see cref="Links.ILinkIndex"/>/<see cref="Search.ISearchIndex"/>
/// are (fed by <see cref="Links.VaultWatcherService"/> via
/// <see cref="IVaultChangeListener"/>), per
/// docs/features/tasks-kanban/PLAN.md §2. Pure cache: safe to discard and
/// <see cref="RebuildAsync"/> from disk at any time.
/// </summary>
public interface ITaskIndex : IVaultChangeListener
{
    /// <summary>
    /// Monotonically increasing counter, bumped only when a task-affecting
    /// change is applied (a task note added/changed/removed - a plain
    /// note's <see cref="IVaultChangeListener.NoteChanged"/> call is a
    /// no-op and does not bump this). Backs the polling
    /// <c>GET /api/tasks/revision</c> live-refresh contract.
    /// </summary>
    long Revision { get; }

    /// <summary>Every currently-known task, in no particular order.</summary>
    IReadOnlyList<TaskItem> GetAll();

    /// <summary>Looks up a task by id, case-insensitively. <see langword="null"/> if not found.</summary>
    TaskItem? GetById(string id);

    /// <summary>Looks up a task by its note's vault-relative path. <see langword="null"/> if not found or not a task.</summary>
    TaskItem? GetByPath(string path);

    /// <summary>Full rebuild from disk via <see cref="Notes.INoteRepository"/>, discarding all current state.</summary>
    Task RebuildAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that <paramref name="path"/> was just written by
    /// <see cref="ITaskService"/> with <paramref name="content"/>, whose
    /// file mtime is precisely <paramref name="updatedAt"/> (the value the
    /// write itself already knows from <see cref="Notes.INoteRepository.SaveAsync"/>'s
    /// result). Equivalent to <see cref="IVaultChangeListener.NoteChanged"/>
    /// but with an accurate timestamp instead of the "just now" approximation
    /// that method uses - see its remarks.
    /// </summary>
    void NoteSaved(string path, string content, DateTimeOffset updatedAt);
}
