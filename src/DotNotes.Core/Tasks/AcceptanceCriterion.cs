namespace DotNotes.Core.Tasks;

/// <summary>
/// One line of a task's <c>## Acceptance Criteria</c> checklist
/// (<c>- [ ] #1 text</c>), per docs/features/tasks-kanban/PLAN.md §2.
/// </summary>
/// <param name="Index">1-based display number, renumbered 1..n on every write.</param>
/// <param name="Text">The criterion's text (never includes the leading <c>#n</c>).</param>
/// <param name="Checked">Whether the box is checked (<c>- [x]</c>).</param>
public sealed record AcceptanceCriterion(int Index, string Text, bool Checked);
