# 02 — Architecture

## Stack

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS, supported to Nov 2028), C# 14 | Odd-numbered .NET 9/8 are heading out of support Nov 2026 — build on 10 from day one |
| Web host | ASP.NET Core minimal APIs | Serves REST API, static frontend, and the MCP endpoint from one process |
| Storage | Flat files on disk (a "vault" directory) | No SQL/NoSQL database — see below |
| Search index | In-memory inverted index (MVP); `Lucene.NET` as a drop-in upgrade | Rebuilt at startup, updated live via file watcher |
| MCP | `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` (official Microsoft/Anthropic C# SDK) | Hosted in-process at `/mcp` |
| Frontend | Static HTML/CSS/JS in `wwwroot/`, no SPA framework | `marked`, `mermaid`, `MathJax`, `highlight.js`, Tailwind (precompiled) |
| Tests | xUnit | Core logic (parsing, indexing, repository) is framework-free and easy to unit test |
| Packaging | Docker, multi-stage build | Runs identically on Windows, WSL2, or bare Linux |

## Why no database

The whole point of this app is "my notes are just markdown files I own."
Every index (search, backlinks, graph, share tokens) is a **derived
cache**: computed from the files on disk, safe to delete and rebuild,
and never the only copy of anything. This mirrors the original app's
design and is what makes the vault trivially backup-able with any
generic file backup tool, or just `git init` inside it.

## Solution layout

```
DotNotes.sln
src/
  DotNotes.Api/            # ASP.NET Core host: endpoints, MCP, static files, Program.cs
    Endpoints/                  # one file per resource: NotesEndpoints.cs, SearchEndpoints.cs, ...
    Mcp/                        # MCP tool classes
    wwwroot/                    # frontend: index.html, app.js, editor.js, graph.js, lib/ (vendored JS libs), css/
    appsettings.json
  DotNotes.Core/           # no ASP.NET dependency — pure domain logic
    Notes/                      # INoteRepository + FileSystemNoteRepository
    Links/                      # wikilink parser, backlink index, graph model
    Search/                     # ISearchIndex + InMemorySearchIndex ( + optional LuceneSearchIndex)
    Sharing/                    # share token store
    Tasks/                      # Phase 12: Tasks & Kanban (ITaskIndex/ITaskService, TaskMarkdown frontmatter+section parsing)
    Config/                    # strongly-typed options classes + VaultPathValidator (resolves/creates the vault root at startup, framework-free)
tests/
  DotNotes.Core.Tests/
Dockerfile
docker-compose.yml
```

## Request flow

1. Browser loads `wwwroot/index.html`, which loads `app.js` and the
   vendored rendering libraries.
2. `app.js` talks to the REST API (`docs/04-API-SPEC.md`) for all note
   operations — same-origin, no CORS complexity needed for a local app.
3. `DotNotes.Core` services are registered via DI and are the only
   thing that touches the filesystem; endpoints are thin adapters.
4. A single `FileSystemWatcher` on the vault root feeds both the link
   index and the search index so external edits (e.g. editing a note
   directly in a text editor, or restoring from backup) are picked up
   without a restart.
5. The MCP endpoint at `/mcp` calls into the exact same
   `DotNotes.Core` services as the REST endpoints — there is only
   ever one code path that reads or writes a note.

## Configuration

Environment-variable-overridable `appsettings.json`, e.g.:

```json
{
  "Vault": { "RootPath": "/data/vault" },
  "Server": { "Port": 5175 },
  "Sharing": { "Enabled": true },
  "Mcp": { "Enabled": true }
}
```

Docker overrides these with `Vault__RootPath`, `Server__Port`, etc.
(ASP.NET Core's double-underscore convention for nested config keys).

Phase 12 (Tasks & Kanban) adds a `Tasks` section (`Folder` - default
`Task` since v0.2.1, was `tasks` -, `IdPrefix`, `Statuses`,
`DefaultStatus`, `BacklogStatus`, `CompletedStatus`, `Priorities` - see
docs/06-DATA-MODEL.md's "Tasks" section for the full schema) and a
`YamlDotNet` dependency in
`DotNotes.Core` used strictly to *parse* a task note's YAML frontmatter;
the on-disk layout is always written by `DotNotes.Core.Tasks.TaskMarkdown`'s
own small emitter (matching Backlog.md's field order/quoting), never by
YamlDotNet's emitter, so `DotNotes.Core` still has zero ASP.NET Core
dependency and diffs stay minimal.

## Non-functional notes

- **Single user, trusted network.** No auth is required on the main
  API for a purely local/self-hosted single-user setup. If you later
  expose this beyond your own machine/LAN, that's a deliberate follow-up
  phase (basic auth or a reverse proxy with auth), not part of this plan.
- **Cross-platform paths.** Always resolve note paths through
  `Path.Combine`/`Path.GetFullPath` and validate the resolved path stays
  under the vault root — never string-concatenate user-supplied paths.
- **Client-side UI display preferences live in browser `localStorage`,
  never on disk/server.** Theme, view-mode (Edit/Split/Preview), the
  Split-mode divider ratio, sidebar collapsed/expanded state, and the
  per-folder sort mode (name asc/desc vs. custom manual order) plus any
  custom order itself are all cosmetic, per-browser display state, not
  vault data — they don't belong in a note's frontmatter or a sidecar
  file. Same pattern as the existing theme toggle. This means these
  preferences don't roam across browsers/devices for the same vault;
  that's an acceptable tradeoff for a single-user local build.
