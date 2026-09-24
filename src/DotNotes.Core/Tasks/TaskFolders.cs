namespace DotNotes.Core.Tasks;

/// <summary>
/// Single shared definition of the "Completed" subfolder convention used by
/// <see cref="ITaskService.CompleteAsync"/>, <see cref="ITaskIndex"/> and
/// <see cref="TaskFolderMigration"/>: a task note is considered completed
/// when any directory segment of its vault-relative path is exactly
/// <see cref="Completed"/> (ordinal, case-sensitive comparison - a folder
/// named <c>completed</c> or <c>COMPLETED</c> does not count).
/// </summary>
public static class TaskFolders
{
    /// <summary>The exact, case-sensitive folder name a completed task's note is moved under.</summary>
    public const string Completed = "Completed";

    /// <summary>
    /// <see langword="true"/> if any '/'-or-'\'-separated segment of
    /// <paramref name="path"/> is exactly <see cref="Completed"/> (ordinal
    /// comparison). Works for a full note path (e.g.
    /// <c>Task/ProjectX/Completed/TASK-3 - X.md</c>) or a bare folder path.
    /// </summary>
    public static bool IsInCompletedFolder(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, Completed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
