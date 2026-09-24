using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DotNotes.Api.Mcp;

/// <summary>
/// Exposes <see cref="TaskWorkflowGuide.Markdown"/> as the MCP resource
/// <c>dotnotes://workflow/tasks</c>, per docs/features/tasks-kanban/PLAN.md
/// §6. Registered separately from <see cref="DotNotesTaskMcpTools"/> via
/// <c>WithResources&lt;DotNotesTaskWorkflowResource&gt;()</c> in Program.cs -
/// the installed SDK (ModelContextProtocol 2.2.0) discovers resources the
/// same way it discovers tools: methods attributed with
/// <see cref="McpServerResourceAttribute"/> on a type added via
/// <c>WithResources&lt;T&gt;</c>. The <c>get_task_workflow</c> tool returns
/// the exact same string, for MCP clients that don't read resources.
/// </summary>
[McpServerResourceType]
public sealed class DotNotesTaskWorkflowResource
{
    [McpServerResource(UriTemplate = "dotnotes://workflow/tasks", Name = "tasks-workflow", MimeType = "text/markdown")]
    [Description("A markdown guide for how an AI assistant should use dotNotes' task tools: when to create a task, how to write one, and how to execute and finalize it.")]
    public string TasksWorkflow() => TaskWorkflowGuide.Markdown;
}
