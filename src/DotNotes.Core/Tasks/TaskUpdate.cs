namespace DotNotes.Core.Tasks;

/// <summary>
/// Patch semantics: every field is optional, <see langword="null"/>/absent
/// means "leave unchanged", an empty string clears a scalar field. Backs
/// <c>TaskPatch</c> in docs/04-API-SPEC.md's Tasks section / the MCP
/// <c>update_task</c> tool. See <see cref="ITaskService.UpdateAsync"/>.
/// </summary>
public sealed record TaskUpdate
{
    public string? Title { get; init; }
    public string? Status { get; init; }
    public IReadOnlyList<string>? Assignee { get; init; }
    public IReadOnlyList<string>? Labels { get; init; }
    public string? Priority { get; init; }
    public string? Milestone { get; init; }
    public IReadOnlyList<string>? Dependencies { get; init; }
    public string? Description { get; init; }

    /// <summary>Replaces the whole acceptance-criteria list, renumbered 1..n.</summary>
    public IReadOnlyList<(string Text, bool Checked)>? AcceptanceCriteria { get; init; }

    /// <summary>Appends new unchecked criteria after any existing ones.</summary>
    public IReadOnlyList<string>? AcceptanceCriteriaAdd { get; init; }

    /// <summary>1-based indexes to remove (applied after add, before check/uncheck).</summary>
    public IReadOnlyList<int>? AcceptanceCriteriaRemove { get; init; }

    /// <summary>1-based indexes to check.</summary>
    public IReadOnlyList<int>? AcceptanceCriteriaCheck { get; init; }

    /// <summary>1-based indexes to uncheck.</summary>
    public IReadOnlyList<int>? AcceptanceCriteriaUncheck { get; init; }

    public string? ImplementationPlan { get; init; }
    public string? PlanAppend { get; init; }
    public string? ImplementationNotes { get; init; }
    public string? NotesAppend { get; init; }
    public string? FinalSummary { get; init; }
    public double? Ordinal { get; init; }
}
