namespace DotNotes.Core.Tasks;

/// <summary>
/// Input to <see cref="ITaskService.CreateAsync"/>. Backs <c>TaskCreate</c>
/// in docs/04-API-SPEC.md's Tasks section / the MCP <c>create_task</c> tool.
/// </summary>
public sealed record TaskCreateRequest
{
    public required string Title { get; init; }
    public string? Status { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Assignee { get; init; }
    public IReadOnlyList<string>? Labels { get; init; }
    public string? Priority { get; init; }
    public string? Milestone { get; init; }
    public IReadOnlyList<string>? Dependencies { get; init; }
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    /// <summary>
    /// Vault-relative folder to create the task's note under. Defaults to
    /// the configured <c>Tasks:Folder</c> when omitted.
    /// </summary>
    public string? Folder { get; init; }
}
