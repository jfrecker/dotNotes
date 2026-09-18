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
| `create_folder` | `path: string` | `{ path }` | `mkdir -p` semantics — idempotent if it already exists. Mirrors `POST /api/folders/{path}` |
| `move_note` | `path: string`, `destinationPath: string` | `{ path, updatedAt, rewrittenNotes }` | Moves or renames a note (rename = move within the same folder). Fails if `destinationPath` already exists — never overwrites. Rewrites incoming `[[wikilinks]]` that would otherwise break; `rewrittenNotes` lists every note whose content changed. Mirrors `POST /api/notes/{path}/move` |
| `move_folder` | `path: string`, `destinationPath: string` | `{ path, rewrittenNotes }` | Moves or renames a folder and everything inside it. Fails if `destinationPath` already exists, or is the folder itself or one of its descendants. Same wikilink rewriting as `move_note`, across every note in the moved subtree. Mirrors `POST /api/folders/{path}/move` |

`move_note` and `move_folder` go through the same `DotNotes.Core`
orchestration service as their REST counterparts, so link rewriting,
name validation and index consistency can't differ between the two
surfaces. See `docs/06-DATA-MODEL.md`'s "Folder & note move/rename"
section for the rewrite rules.

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
