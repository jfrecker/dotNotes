# 05 — MCP Server Spec

Hosted in-process inside `DotNotes.Api` at `/mcp`, using the
official `ModelContextProtocol` + `ModelContextProtocol.AspNetCore`
NuGet packages. Because it's in the same process as the REST API, tools
call `DotNotes.Core` services directly — there is no HTTP hop like
the original app's separate Python MCP process.

> The exact attribute names and registration calls below reflect the
> SDK's public samples at time of writing. The package is pre-1.0 and
> can change — when implementing, verify against whatever version is
> actually installed (`dotnet list package`) and adjust syntax, not the
> tool contract itself.

## Tools to expose

| Tool | Arguments | Returns | Behaviour |
|---|---|---|---|
| `search_notes` | `query: string`, `limit?: int` | list of `{ path, title, snippet }` | Same ranking as `/api/search` |
| `get_note` | `path: string`, `includeBacklinks?: bool` | `{ path, content, backlinks? }` | Mirrors `GET /api/notes/{path}` |
| `create_note` | `path: string`, `content: string` | `{ path }` | Fails if the note already exists (use `update_note` instead) |
| `update_note` | `path: string`, `content: string` | `{ path, updatedAt }` | Creates the note if missing (same semantics as `PUT /api/notes/{path}`) |
| `get_backlinks` | `path: string` | list of `{ path, title }` | |
| `get_recent_notes` | `limit?: int` | list of `{ path, title, updatedAt }` | Sorted by most recently modified |
| `get_config` | — | `{ name, version, features, autosaveDelayMs }` | Lets the calling LLM discover capabilities before guessing |
| `create_folder` | `path: string`, `failIfExists?: bool` | `{ path }` | `mkdir -p` semantics — idempotent if it already exists, unless `failIfExists` is true (then an existing folder is an error). Mirrors `POST /api/folders/{path}` (`?failIfExists=true`) |
| `move_note` | `path: string`, `destinationPath: string` | `{ path, updatedAt, rewrittenNotes }` | Moves or renames a note (rename = move within the same folder). Fails if `destinationPath` already exists — never overwrites. Rewrites incoming `[[wikilinks]]` that would otherwise break; `rewrittenNotes` lists every note whose content changed. Mirrors `POST /api/notes/{path}/move` |
| `move_folder` | `path: string`, `destinationPath: string` | `{ path, rewrittenNotes }` | Moves or renames a folder and everything inside it. Fails if `destinationPath` already exists, or is the folder itself or one of its descendants. Same wikilink rewriting as `move_note`, across every note in the moved subtree. Mirrors `POST /api/folders/{path}/move` |

`move_note` and `move_folder` go through the same `DotNotes.Core`
orchestration service as their REST counterparts, so link rewriting,
name validation and index consistency can't differ between the two
surfaces. See `docs/06-DATA-MODEL.md`'s "Folder & note move/rename"
section for the rewrite rules.

### Tasks & Kanban (docs/features/tasks-kanban/PLAN.md §6)

Implemented as a separate `DotNotesTaskMcpTools` (`McpServerToolType`),
calling straight into the same `ITaskService` the `/api/tasks/*` REST
endpoints use.

| Tool | Arguments | Returns | Behaviour |
|---|---|---|---|
| `list_tasks` | `status?, label?, assignee?, priority?, milestone?, includeCompleted?, limit?` (`includeArchived?` still accepted as a deprecated alias) | `TaskSummary[]` | Same sort as the board (status column order, Backlog first, then ordinal) |
| `get_task` | `id` | `Task` | Full task incl. description, acceptance criteria, plan, notes, final summary |
| `create_task` | `title, description?, status?, priority?, assignee?[], labels?[], milestone?, dependencies?[], acceptanceCriteria?[], folder?` | `Task` | Fails on an invalid status/priority (not in configuration); blank `status` defaults to the Backlog column's status; `folder` defaults to `Tasks:Folder` and is rejected if it's inside an existing `Completed` folder |
| `update_task` | `id` + optional `title, status, priority, assignee[], labels[], milestone, dependencies[], description, acceptanceCriteriaAdd[], acceptanceCriteriaRemove[] (1-based), acceptanceCriteriaCheck[], acceptanceCriteriaUncheck[], planSet, planAppend, notesSet, notesAppend, finalSummary` | `Task` | Patch semantics: only supplied fields change; `""` clears `priority`, `milestone`, `description`, `planSet`, `notesSet` or `finalSummary` (but **not** `title`, which can't be empty - that is an error); arrays replace the whole list |
| `move_task` | `id, status, index?, beforeId?` | `Task` | `beforeId` (another task's id in the destination column) inserts the task immediately before it and wins over `index`, a 0-based position among the column's *other* tasks; omit both for the end. Moving to where the task already is writes nothing. Moving to the Backlog column sets `status` to `Tasks:BacklogStatus` unless the task is already in Backlog (reorder-only) |
| `complete_task` | `id` | `Task` | Sets status to `Tasks:CompletedStatus` and moves the note into a `Completed` subfolder next to it (e.g. `Task/ProjectX/Completed/`), created if missing, never overwriting. Idempotent if already completed |
| `archive_task` | `id` | `Task` | **Deprecated** — calls the same behaviour as `complete_task`; use `complete_task` instead |
| `get_board` | `status?, label?, assignee?, priority?, milestone?` | `{ columns: [{ status, isBacklog, tasks: TaskSummary[] }] }` | Backlog column always first, followed by one column per remaining configured status, in order — no more trailing unknown-status columns |
| `search_tasks` | `query, limit?, includeCompleted?` (`includeArchived?` still accepted as a deprecated alias) | `TaskSummary[]` | Substring/token match over id, title, description, labels, assignee |
| `get_task_workflow` | — | markdown string | Same content as the `dotnotes://workflow/tasks` resource below, for clients that don't read resources |

`TaskSummary` = `{ id, title, status, assignee[], labels[], priority, milestone, dependencies[], createdDate, updatedDate, ordinal, path, completed, excerpt, acTotal, acChecked }` — `completed` (replaces v0.2.0's `archived`) is true when the task's path is under any folder segment named exactly `Completed` (case-sensitive).
`Task` = `TaskSummary` + `{ description, acceptanceCriteria: [{ index, text, checked }], implementationPlan, implementationNotes, finalSummary, updatedAt }`.

`create_task` fails if the request doesn't validate (empty title, or a
`status`/`priority` outside this instance's configured lists) — the
same `TaskValidationException` the REST endpoints translate to 400.
`get_task`/`update_task`/`move_task`/`complete_task`/`archive_task`
fail with a structured MCP error if `id` doesn't match any task (same
`TaskNotFoundException` the REST endpoints translate to 404).

## Resources

| Resource | MIME type | Content |
|---|---|---|
| `dotnotes://workflow/tasks` | `text/markdown` | The same task-workflow guide as the `get_task_workflow` tool: when to create a task, how to write it as a self-contained work order, the plan → implement → notes → verify → final-summary execution flow, the Backlog column (new/not-started work), and when to `complete_task` a task (only Done/genuinely finished work — moves the note into a `Completed` subfolder) vs. just `move_task` it to Done (status only, file stays put). Registered via `DotNotesTaskWorkflowResource` (`[McpServerResourceType]`/`[McpServerResource]`, added with `WithResources<T>()`) — see that section's implementation note on the installed SDK version. |

## Transport

HTTP transport at `/mcp` on the same host/port as the web app (so no
extra port to expose in Docker). Example client registration for
Claude Desktop (`claude_desktop_config.json`) or Claude Code, once the
app is running locally on port 5175:

```json
{
  "mcpServers": {
    "dotnotes": {
      "url": "http://localhost:5175/mcp"
    }
  }
}
```

## Error handling

Tool failures (note not found, invalid path, etc.) should return a
structured MCP tool error with a short human-readable message — never
let a raw .NET exception/stack trace reach the client.

As implemented (`ModelContextProtocol`/`ModelContextProtocol.AspNetCore`
2.2.0): throw `ModelContextProtocol.McpException` with the desired
message from a tool method — the SDK propagates its `Message` to the
client as `CallToolResult { IsError = true }`. Any other exception type
is replaced with a generic message before it reaches the client, so
every failure path a tool can hit must be funneled through
`McpException` explicitly rather than left to propagate.

## Return values

A tool method may return a plain record/DTO, not just `string` (the
sample below only shows a `string`-returning tool, which undersells
this) — the SDK auto-serializes any non-string/non-`ContentBlock`
return type to JSON (camelCase, matching the REST responses) with no
manual `JsonSerializer` calls needed in tool code.

## Sample code shape (verify against the installed SDK version)

```csharp
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<NoteTools>();
// ... register DotNotes.Core services as usual ...
var app = builder.Build();
app.MapMcp("/mcp");
app.Run();

[McpServerToolType]
public class NoteTools(INoteRepository notes, ISearchIndex search)
{
    [McpServerTool, Description("Search notes by keyword.")]
    public Task<IReadOnlyList<SearchHit>> SearchNotes(string query, int limit = 10)
        => search.SearchAsync(query, limit);

    // get_note, create_note, update_note, get_backlinks,
    // get_recent_notes, get_config follow the same pattern.
}
```

### Resources (2.2.0)

The installed SDK (`ModelContextProtocol` 2.2.0) supports MCP resources
the same way it supports tools — attribute a method, register the
containing type:

```csharp
[McpServerResourceType]
public sealed class TaskWorkflowResource
{
    [McpServerResource(UriTemplate = "dotnotes://workflow/tasks", MimeType = "text/markdown")]
    [Description("Guide for using dotNotes' task tools.")]
    public string TasksWorkflow() => TaskWorkflowGuide.Markdown;
}

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<NoteTools>()
    .WithResources<TaskWorkflowResource>();
```

A method returning a plain `string` is converted to a single
`TextResourceContents` automatically — no manual `ReadResourceResult`
construction needed, same "no manual JSON serialization" ergonomics
tools get (see "Return values" above).
