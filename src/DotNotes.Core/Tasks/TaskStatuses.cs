using DotNotes.Core.Config;

namespace DotNotes.Core.Tasks;

/// <summary>
/// Single place that computes a vault's <i>effective</i> board columns from
/// <see cref="TasksOptions"/> - used by <see cref="TaskService"/> (list
/// ordering, move/create validation), <see cref="ITaskService.GetBoard"/>,
/// and (via the API layer) the <c>GET /api/tasks/config</c> endpoint, so all
/// three can never drift apart on what a "status" or "column" means.
/// </summary>
public static class TaskStatuses
{
    /// <summary>
    /// <paramref name="options"/>'s configured <see cref="TasksOptions.Statuses"/>,
    /// guaranteed to start with a Backlog entry: if a configured status
    /// case-insensitively matches <see cref="TasksOptions.BacklogStatus"/> it
    /// is moved to the front (its on-disk/configured casing is kept);
    /// otherwise <see cref="TasksOptions.BacklogStatus"/> itself is
    /// prepended. The rest of the configured order is preserved.
    /// </summary>
    public static IReadOnlyList<string> GetEffectiveStatuses(TasksOptions options)
    {
        var statuses = options.Statuses ?? new List<string>();
        var backlogConfigured = string.IsNullOrWhiteSpace(options.BacklogStatus) ? "Backlog" : options.BacklogStatus;

        var backlogLabel = statuses.FirstOrDefault(s => string.Equals(s, backlogConfigured, StringComparison.OrdinalIgnoreCase))
            ?? backlogConfigured;

        var result = new List<string>(statuses.Count + 1) { backlogLabel };
        foreach (var status in statuses)
        {
            if (!string.Equals(status, backlogLabel, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(status);
            }
        }

        return result;
    }

    /// <summary>
    /// The effective status a newly-completed task's frontmatter <c>status</c>
    /// is set to: whichever effective status case-insensitively matches
    /// <see cref="TasksOptions.CompletedStatus"/> (in its configured casing),
    /// or the last effective status if none matches.
    /// </summary>
    public static string GetEffectiveCompletedStatus(TasksOptions options)
    {
        var effective = GetEffectiveStatuses(options);
        var match = effective.FirstOrDefault(s => string.Equals(s, options.CompletedStatus, StringComparison.OrdinalIgnoreCase));
        return match ?? effective[^1];
    }

    /// <summary>
    /// The effective status - always the first entry of
    /// <see cref="GetEffectiveStatuses"/> - the Backlog board column is
    /// keyed by.
    /// </summary>
    public static string GetBacklogColumnName(TasksOptions options) => GetEffectiveStatuses(options)[0];

    /// <summary>
    /// The board column a task with <paramref name="rawStatus"/> belongs in:
    /// the effective status it case-insensitively matches, or the Backlog
    /// column (see <see cref="GetBacklogColumnName"/>) for an empty or
    /// unrecognised raw status.
    /// </summary>
    public static string ClassifyColumn(TasksOptions options, string? rawStatus)
    {
        var effective = GetEffectiveStatuses(options);
        if (!string.IsNullOrWhiteSpace(rawStatus))
        {
            var match = effective.FirstOrDefault(s => string.Equals(s, rawStatus, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return effective[0];
    }

    /// <summary>
    /// <see langword="true"/> if <paramref name="columnName"/> is (a
    /// case-insensitive match for) the Backlog column.
    /// </summary>
    public static bool IsBacklogColumnName(TasksOptions options, string columnName) =>
        string.Equals(columnName, GetBacklogColumnName(options), StringComparison.OrdinalIgnoreCase);
}
