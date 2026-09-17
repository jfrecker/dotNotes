# dotNotes

A self-hosted, single-user markdown note-taking and knowledge-base
app — a .NET/ASP.NET Core replica of
[NoteDiscovery](https://github.com/gamosoft/NoteDiscovery). No
database, no cloud dependency: every note is a plain `.md` file in a
vault directory on disk. Features a split-pane editor with live
preview (Mermaid diagrams, LaTeX math, syntax highlighting),
`[[wikilinks]]` with backlinks and a graph view, full-text search,
image/audio/video/PDF embedding, token-protected shareable read-only
links with QR codes, and an in-process MCP server so an AI assistant
(Claude Desktop, Claude Code, Cursor) can search and edit your notes
directly.

## Quick start

```bash
git clone https://github.com/jfrecker/dotNotes.git
cd dotNotes
cp .env.example .env
docker compose up -d --build
```

Then open <http://localhost:5175> (or whatever `HOST_PORT` you set in
`.env`). Every value in `.env` is optional and commented out by default,
so the copied file works unedited.

See **[`DEPLOYMENT.md`](DEPLOYMENT.md)** for the full deployment
guide — WSL2 vs. plain Linux, running natively without Docker (Windows
or Linux), every configuration option, updating, backup/restore, and
reverse-proxy/HTTPS guidance if you ever expose this beyond your own
LAN.

## Connecting an AI assistant via MCP

Once the app is running locally (`dotnet run --project src/DotNotes.Api`,
default port `5175`), an MCP-capable client can reach it over the
Streamable HTTP transport at `/mcp` — no separate process, and no
extra port to expose. Point Claude Desktop
(`claude_desktop_config.json`) or Claude Code at it with:

```json
{
  "mcpServers": {
    "dotnotes": {
      "url": "http://localhost:5175/mcp"
    }
  }
}
```

The endpoint is gated on the `Mcp:Enabled` config flag (on by default,
`appsettings.json` / `Mcp__Enabled` env var) — when disabled, `/mcp` is
not mapped at all. See `docs/05-MCP-SPEC.md` for the full tool contract
(`search_notes`, `get_note`, `create_note`, `update_note`,
`get_backlinks`, `get_recent_notes`, `get_config`).

## Project structure

```
CLAUDE.md                          # project rules & constraints for Claude Code
DEPLOYMENT.md                      # full deployment guide
docs/
  01-PROJECT-PLAN.md               # phased build order, tasks, exit criteria per phase
  02-ARCHITECTURE.md               # tech stack and system design
  03-FEATURE-SPEC.md               # feature checklist, MVP vs stretch (all Must-have items done)
  04-API-SPEC.md                   # REST contract
  05-MCP-SPEC.md                   # MCP server tool contract
  06-DATA-MODEL.md                 # vault layout, wikilinks, indexes
src/
  DotNotes.Api/                    # ASP.NET Core host: REST API, MCP endpoint, static frontend
  DotNotes.Core/                   # framework-free domain library (notes, links, search, sharing, media)
tests/                             # xUnit test projects (291 tests)
.claude/agents/                    # the subagents this project was built with, kept for future changes
```

`docs/` remains the source of truth for *why* things are shaped the way
they are — update the relevant doc first before changing behavior that
contradicts it (see `CLAUDE.md`).

## Notes

- Targets **.NET 10 (LTS)**.
- Deliberately **descopes multi-language UI and multi-user accounts** —
  see `docs/03-FEATURE-SPEC.md`'s "Explicitly descoped" section.
- The MCP server uses the official `ModelContextProtocol` C# SDK
  (currently pre-1.0, pinned at `2.2.0`) — `docs/05-MCP-SPEC.md` has the
  full tool contract.
