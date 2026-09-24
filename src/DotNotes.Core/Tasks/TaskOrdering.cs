using System.Text.RegularExpressions;

namespace DotNotes.Core.Tasks;

/// <summary>
/// Sort order and ordinal-computation algorithm for kanban columns, per
/// docs/features/tasks-kanban/PLAN.md §2 ("Ordering"). Ported (not copied)
/// from Backlog.md's <c>reorder.ts</c> ordinal scheme: step 1000, midpoint
/// insert, rebalance on a too-small gap.
/// </summary>
public static class TaskOrdering
{
    /// <summary>Step used both for a brand-new task's ordinal and for rebalancing a whole column.</summary>
    public const double Step = 1000d;

    /// <summary>
    /// Gap between two ordinals below which a plain midpoint insert would
    /// lose precision, forcing a full-column rebalance instead.
    /// </summary>
    private const double MinimumGap = 1e-6;

    /// <summary>
    /// Sort comparer: <c>ordinal</c> ascending (missing/<see langword="null"/>
    /// sorts last), then <c>created_date</c> ascending (missing sorts
    /// last), then numeric id segments ascending (e.g. <c>TASK-9</c> before
    /// <c>TASK-10</c>; a non-numeric id segment falls back to ordinal string
    /// comparison).
    /// </summary>
    public static readonly IComparer<TaskItem> Comparer = System.Collections.Generic.Comparer<TaskItem>.Create(Compare);

    private static int Compare(TaskItem? a, TaskItem? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return 1;
        }

        if (b is null)
        {
            return -1;
        }

        var ordinalCompare = CompareNullableLast(a.Ordinal, b.Ordinal);
        if (ordinalCompare != 0)
        {
            return ordinalCompare;
        }

        var dateCompare = CompareNullableLast(
            a.CreatedDate?.UtcTicks,
            b.CreatedDate?.UtcTicks);
        if (dateCompare != 0)
        {
            return dateCompare;
        }

        return CompareIds(a.Id, b.Id);
    }

    private static int CompareNullableLast<T>(T? a, T? b) where T : struct, IComparable<T>
    {
        if (a.HasValue && b.HasValue)
        {
            return a.Value.CompareTo(b.Value);
        }

        if (!a.HasValue && !b.HasValue)
        {
            return 0;
        }

        // Missing sorts last.
        return a.HasValue ? -1 : 1;
    }

    /// <summary>
    /// Compares two ids numerically by their trailing digit run (e.g.
    /// <c>TASK-9</c> &lt; <c>TASK-10</c>), falling back to an ordinal
    /// string comparison when either id doesn't end in digits.
    /// </summary>
    public static int CompareIds(string idA, string idB)
    {
        var numA = TrailingNumber(idA);
        var numB = TrailingNumber(idB);

        if (numA.HasValue && numB.HasValue)
        {
            var prefixCompare = string.Compare(
                idA[..^numA.Value.DigitCount],
                idB[..^numB.Value.DigitCount],
                StringComparison.OrdinalIgnoreCase);
            if (prefixCompare != 0)
            {
                return prefixCompare;
            }

            return numA.Value.Value.CompareTo(numB.Value.Value);
        }

        return string.Compare(idA, idB, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex TrailingDigitsPattern = new(@"(\d+)$", RegexOptions.Compiled);

    private static (long Value, int DigitCount)? TrailingNumber(string id)
    {
        var match = TrailingDigitsPattern.Match(id);
        if (!match.Success)
        {
            return null;
        }

        return long.TryParse(match.Groups[1].Value, out var value) ? (value, match.Groups[1].Value.Length) : null;
    }

    /// <summary>
    /// Ordinal for a brand-new task appended to a column: max existing
    /// ordinal in the column + <see cref="Step"/> (or <see cref="Step"/>
    /// itself if the column has no ordinals yet).
    /// </summary>
    public static double NextOrdinalForNewTask(IEnumerable<TaskItem> columnTasks)
    {
        double? max = null;
        foreach (var task in columnTasks)
        {
            if (task.Ordinal is { } value && (max is null || value > max))
            {
                max = value;
            }
        }

        return (max ?? 0d) + Step;
    }

    /// <summary>
    /// Computes the ordinal(s) needed to move a task to 0-based
    /// <paramref name="targetIndex"/> among <paramref name="otherOrderedTasks"/>
    /// (the destination column's tasks, already sorted by <see cref="Comparer"/>,
    /// <b>excluding</b> the task being moved). Returns only the ordinals
    /// that actually need to change: normally just the moved task's own
    /// new ordinal, but a full-column rebalance (1000, 2000, ... for every
    /// task in <paramref name="otherOrderedTasks"/> plus the moved one, in
    /// final order) when neighbours lack ordinals or the gap between them
    /// is smaller than a usable midpoint allows.
    /// </summary>
    /// <param name="otherOrderedTasks">
    /// The target column's other tasks, in their current display order.
    /// </param>
    /// <param name="targetIndex">
    /// 0-based position among <paramref name="otherOrderedTasks"/> the
    /// moved task should end up at. Clamped to the end if absent or too
    /// large.
    /// </param>
    /// <param name="movedTaskId">
    /// The id of the task being moved (excluded from
    /// <paramref name="otherOrderedTasks"/> by the caller; used only to key
    /// the returned dictionary consistently with everything else's id).
    /// </param>
    /// <returns>Task id -&gt; new ordinal, for every task whose ordinal changed.</returns>
    public static IReadOnlyDictionary<string, double> ComputeMove(
        IReadOnlyList<TaskItem> otherOrderedTasks,
        int? targetIndex,
        string movedTaskId)
    {
        var index = targetIndex is null or < 0 || targetIndex > otherOrderedTasks.Count
            ? otherOrderedTasks.Count
            : targetIndex.Value;

        var hasPrevious = index > 0;
        var hasNext = index < otherOrderedTasks.Count;
        var previous = hasPrevious ? otherOrderedTasks[index - 1].Ordinal : null;
        var next = hasNext ? otherOrderedTasks[index].Ordinal : null;

        double? candidate = null;
        var needsRebalance = false;

        if (!hasPrevious && !hasNext)
        {
            // Inserting into a genuinely empty column - no neighbour on
            // either side (as opposed to a neighbour that exists but lacks
            // an ordinal, handled below).
            candidate = Step;
        }
        else if (!hasPrevious)
        {
            if (next is null)
            {
                needsRebalance = true;
            }
            else
            {
                // Inserting at the top: half of the next ordinal, unless
                // that's too small a gap from zero to be meaningful.
                var half = next.Value / 2d;
                if (half < MinimumGap || (next.Value - half) < MinimumGap)
                {
                    needsRebalance = true;
                }
                else
                {
                    candidate = half;
                }
            }
        }
        else if (!hasNext)
        {
            if (previous is null)
            {
                needsRebalance = true;
            }
            else
            {
                candidate = previous.Value + Step;
            }
        }
        else if (previous is null || next is null)
        {
            needsRebalance = true;
        }
        else
        {
            var gap = next.Value - previous.Value;
            if (gap < MinimumGap)
            {
                needsRebalance = true;
            }
            else
            {
                candidate = previous.Value + gap / 2d;
                if (candidate.Value - previous.Value < MinimumGap || next.Value - candidate.Value < MinimumGap)
                {
                    needsRebalance = true;
                    candidate = null;
                }
            }
        }

        if (!needsRebalance && candidate is not null)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [movedTaskId] = candidate.Value
            };
        }

        // Full-column rebalance: rebuild final order (other tasks with the
        // moved task inserted at `index`), then assign 1000, 2000, ... and
        // return only the ones that actually changed value.
        var finalOrder = new List<(string Id, double? OldOrdinal)>(otherOrderedTasks.Count + 1);
        for (var i = 0; i < otherOrderedTasks.Count; i++)
        {
            if (i == index)
            {
                finalOrder.Add((movedTaskId, null));
            }

            finalOrder.Add((otherOrderedTasks[i].Id, otherOrderedTasks[i].Ordinal));
        }

        if (index >= otherOrderedTasks.Count)
        {
            finalOrder.Add((movedTaskId, null));
        }

        var changed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < finalOrder.Count; i++)
        {
            var newOrdinal = (i + 1) * Step;
            var (id, oldOrdinal) = finalOrder[i];
            if (oldOrdinal is null || Math.Abs(oldOrdinal.Value - newOrdinal) > double.Epsilon)
            {
                changed[id] = newOrdinal;
            }
        }

        return changed;
    }

    /// <summary>Formats an ordinal for frontmatter: as an integer when whole, else the shortest round-trippable decimal.</summary>
    public static string FormatOrdinal(double ordinal)
    {
        return ordinal == Math.Floor(ordinal) && !double.IsInfinity(ordinal)
            ? ((long)ordinal).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : ordinal.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
