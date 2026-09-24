# 01 — Project Plan

Phased build order for dotNotes. Each phase has a goal, a task
list, an exit criteria checklist, and the subagent(s) to delegate to.
Work phases in order — later phases assume earlier contracts are frozen.

---

## Phase 0 — Solution scaffolding

**Goal:** an empty but runnable ASP.NET Core app with the project
structure, config system, and test project in place.

**Tasks**
- Create solution: `DotNotes.sln` with projects
  `src/DotNotes.Api` (ASP.NET Core 10 minimal API + static files
  host), `src/DotNotes.Core` (domain services, no ASP.NET
  dependency), `tests/DotNotes.Core.Tests` (xUnit).
- Wire up configuration: vault root path, port, share-link token, MCP
  enabled/disabled — via `appsettings.json` + environment variable
  overrides (so Docker `-e` flags work).
- Add health check endpoint `GET /healthz`.
- Add structured logging (built-in `ILogger`, console + rolling file).
- Set `Nullable=enable`, `ImplicitUsings=enable`, target
  `net10.0` in every project.

**Exit criteria**
- [ ] `dotnet run` starts the app and `/healthz` returns 200.
- [ ] Vault path is read from config and validated (created if missing).
- [ ] `dotnet test` runs (even with zero tests) with no errors.

**Delegate to:** `backend-architect`

---

## Phase 1 — Vault & note CRUD

**Goal:** notes can be created, read, updated, deleted, and listed as
plain files on disk, through a clean service layer.

**Tasks**
- Define `INoteRepository` in `DotNotes.Core` — path-based CRUD
  over the vault directory, folder listing, safe path resolution
  (reject `..` traversal, normalize separators, case handling).
- File format: raw markdown, UTF-8, no front-matter required for MVP
  (front-matter parsing can be added later without breaking this
  contract — see `docs/06-DATA-MODEL.md`).
- Implement `GET /api/notes`, `GET /api/notes/{**path}`,
  `PUT /api/notes/{**path}`, `DELETE /api/notes/{**path}` per
  `docs/04-API-SPEC.md`.
- Concurrency: last-write-wins is fine for a single-user local app;
  just make sure writes are atomic (write to temp file, then move).

**Exit criteria**
- [ ] Unit tests cover create/read/update/delete/list, including
      rejecting path traversal.
- [ ] Manual `curl` round-trip against a scratch vault works.

**Delegate to:** `backend-architect`, then `api-developer`

---

## Phase 2 — Frontend shell & markdown rendering

**Goal:** a working single-page editor in the browser: file tree,
markdown editor with live preview, rendered with the same client-side
libraries the original app uses.

**Tasks**
- Static frontend under `wwwroot/`: vanilla HTML/CSS/JS (no framework
  needed for a single-user tool), Tailwind (precompiled CSS, not the
  CDN build), `marked` for markdown → HTML, `highlight.js` for code
  blocks, `mermaid` for diagrams, `MathJax` for LaTeX.
- Split-pane editor: textarea/CodeMirror on the left, rendered preview
  on the right, debounced re-render on keystroke.
- Autosave: debounce writes to `PUT /api/notes/{path}` (~1–2s after
  the user stops typing); show a small "saved" indicator.
- Interactive checkboxes: clicking a rendered `- [ ]` toggles it and
  triggers a save.
- File tree sidebar backed by `GET /api/notes`.

**Exit criteria**
- [ ] Can open, edit, and see a note re-render live, with Mermaid and
      math blocks rendering correctly.
- [ ] Autosave round-trips to disk without user action beyond typing.
- [ ] Checkbox toggle updates the file on disk.

**Delegate to:** `frontend-integrator`

---

## Phase 3 — Wikilinks, backlinks, graph

**Goal:** `[[note name]]`-style links are parsed, backlinks are shown
per note, and a graph view visualizes the link network.

**Tasks**
- Implement the wikilink parser and link-resolution rules in
  `docs/06-DATA-MODEL.md`.
- Build the in-memory link index at startup (scan the vault once);
  keep it live with a `FileSystemWatcher` on the vault directory.
- `GET /api/notes/{path}?includeBacklinks=true` returns backlinks;
  `GET /api/graph` returns the full node/edge graph.
- Frontend: backlinks panel under the editor; a graph view page using
  a small client-side graph-drawing library (e.g. force-directed via
  `d3` or a lightweight canvas approach — frontend-integrator's call).
- Clicking `[[links]]` in the rendered preview navigates to that note
  (creating it if it doesn't exist yet, matching the original's
  "click a link to create the note" behaviour).

**Exit criteria**
- [ ] Renaming or deleting a note updates the backlink index without
      a restart.
- [ ] Graph view renders and is clickable to navigate.

**Delegate to:** `markdown-graph-engineer`, then `frontend-integrator` for the graph UI

---

## Phase 4 — Full-text search

**Goal:** fast search across the whole vault by content and title.

**Tasks**
- MVP: an in-memory inverted index (token → note paths), rebuilt on
  startup and updated incrementally on file-watcher events. This is
  sufficient for a personal vault (hundreds to a few thousand notes).
- `GET /api/search?q=...` returns ranked results with a short snippet.
- Stretch (only if MVP search feels too basic in practice): swap the
  index implementation for `Lucene.NET` behind the same interface —
  this should be a drop-in replacement, not a rewrite, if the interface
  boundary in `docs/06-DATA-MODEL.md` is respected.
- Frontend: search box with debounced queries and a results dropdown.

**Exit criteria**
- [ ] Search returns correct results across nested folders.
- [ ] Search index updates when a note is edited or created, without
      restarting the app.

**Delegate to:** `search-engineer`

---

## Phase 5 — Sharing & media

**Goal:** generate a public, token-protected read-only link (with QR
code) for a single note, and support embedding images/audio/video/PDF
in notes.

**Tasks**
- `POST /api/share/{path}` issues a random token, stored in a small
  JSON file (`.nd-shares.json` in the vault root, not a database) that
  maps token → path → optional expiry.
- `GET /shared/{token}` serves a read-only rendered view (reusing the
  same markdown renderer, no editor chrome), plus a generated QR code
  for the share URL (`QRCoder` NuGet package).
- `POST /api/upload` accepts an image/audio/video/PDF, stores it under
  a `_media/` folder in the vault, and returns a relative path to
  reference from markdown (`![alt](_media/file.png)`).

**Exit criteria**
- [ ] A shared link works from a private browser session with no
      other API access.
- [ ] Revoking a share token invalidates the link immediately.
- [ ] Embedded images/PDFs render in both the editor preview and the
      shared view.

**Delegate to:** `api-developer`, then `frontend-integrator`

---

## Phase 6 — MCP server

**Goal:** an AI assistant (Claude Desktop, Claude Code, Cursor) can
search, read, create, and update notes through MCP.

**Tasks**
- Add the `ModelContextProtocol` and `ModelContextProtocol.AspNetCore`
  NuGet packages to `DotNotes.Api`.
- Host the MCP server as an HTTP endpoint (`/mcp`) inside the same
  ASP.NET Core app — it calls straight into `DotNotes.Core`
  services, no HTTP hop to itself, unlike the original app's separate
  Python process.
- Implement the tools listed in `docs/05-MCP-SPEC.md`.
- Document the Claude Desktop / Claude Code config snippet needed to
  point at the local `/mcp` endpoint.

**Exit criteria**
- [ ] Claude Desktop (or Claude Code) can list and call every tool in
      `docs/05-MCP-SPEC.md` against a running local instance.
- [ ] Tool errors return structured MCP errors, not raw exceptions.

**Delegate to:** `mcp-server-engineer`

---

## Phase 7 — Docker & WSL2 self-host

**Goal:** `docker compose up` gives a running, persistent instance.

**Tasks**
- Multi-stage `Dockerfile`: `mcr.microsoft.com/dotnet/sdk:10.0` build
  stage, `mcr.microsoft.com/dotnet/aspnet:10.0` runtime stage.
- `docker-compose.yml` with a named volume (or bind mount) for the
  vault directory, port mapping, and environment variables for config.
- README section (see root `README.md`) with the exact WSL2 commands:
  installing Docker (or Docker Desktop's WSL2 integration), cloning,
  `docker compose up -d`, and where the vault lives on the Windows
  filesystem so it's easy to back up.

**Exit criteria**
- [ ] Fresh `docker compose up -d` on a clean WSL2 Ubuntu instance
      serves the app and persists notes across container restarts.

**Delegate to:** `devops-engineer`

---

## Phase 8 — Hardening & feature-parity QA

**Goal:** confidence that the app is solid enough for daily personal
use before you rely on it for real notes.

**Tasks**
- Full pass through `docs/03-FEATURE-SPEC.md`, ticking every box and
  filing a note against anything that doesn't match.
- Edge cases: empty vault, very large notes, note names with unicode/
  spaces/special characters, concurrent edits from two browser tabs,
  vault directory temporarily unavailable (e.g. WSL2 mount hiccup).
- Backup story: confirm the vault is just files, so any file-level
  backup tool (or `git init` inside the vault) works with zero extra
  code.

**Exit criteria**
- [x] Every "Must-have" item in `docs/03-FEATURE-SPEC.md` is checked.
- [x] No data loss in any of the edge cases above.

**Delegate to:** `qa-test-engineer`

---

## Phase 9 — Post-launch: folder management, editor view modes, theme toggle

**Goal:** three usability gaps found after real daily use of the
deployed app, per user request.

**Tasks**
- Folder & subfolder management — this needs real new domain logic, not
  just missing REST/frontend wiring: `INoteRepository` has no
  create-folder or move/rename capability at all today (confirmed by
  reading the interface before starting; don't assume otherwise). See
  `docs/06-DATA-MODEL.md`'s "Folder & note move/rename" section for the
  exact contract, including the file-watcher index-consistency gap for
  folder moves that must be closed as part of this, not shipped as a
  hidden bug. New REST endpoints per `docs/04-API-SPEC.md`. New MCP
  tools `create_folder`/`move_note` per `docs/05-MCP-SPEC.md`. Frontend:
  create-folder and move/rename affordances in the sidebar (the tree
  itself already renders nested folders correctly — confirmed by reading
  `tree.js` — the gap is purely the lack of interaction, not the
  rendering).
- Editor view-mode toggle: Edit-only / Split / Preview-only in the
  editor toolbar, replacing the always-on split pane. Persist the
  last-used mode for the session (in-memory/`sessionStorage` is enough
  for v1 — no server-side persistence needed).
- Light/dark theme toggle in the top nav. Client-side (CSS
  variables/classes + a persisted preference), no server config
  involved — ticks the "Light/dark theme toggle" Could-have box in
  `docs/03-FEATURE-SPEC.md`.

**Exit criteria**
- [x] A folder (including nested subfolders) can be created, and a note
      or folder moved/renamed, entirely through the UI, with the
      backlink/search/graph index never left stale afterward.
- [x] `create_folder` and `move_note` are listed and callable via MCP,
      with the same "never silently overwrite" and path-validation
      guarantees as every other tool.
- [x] The editor toolbar has a working Edit-only/Split/Preview-only
      toggle that persists for the session.
- [x] A light/dark theme toggle exists in the top nav and actually
      restyles the whole app, not just a stray element.

**Delegate to:** `backend-architect` (domain logic) → `api-developer`
(REST) → `mcp-server-engineer` (MCP tools) → `frontend-integrator`
(folder-tree UI). `frontend-integrator` alone for the view-mode and
theme toggles (no backend dependency, can run in parallel with the
folder-management chain above).

---

## Phase 10 — NoteDiscovery layout parity: navigation, editor header, rename with link rewriting

**Goal:** match NoteDiscovery's sidebar, home/folder-browse view, and note
editor header (spec: `assets/update.txt` plus the two screenshots in
`assets/`, which is gitignored local reference material), and stop
moves/renames from silently orphaning `[[wikilinks]]`.

**Premises corrected before planning** (checked against the code, not
assumed): rename is *not* missing — Phase 9 shipped a per-row "Move"
prompt that renames in place; folder move is *not* missing at the domain
or REST layer — `INoteRepository.MoveFolderAsync` and
`POST /api/folders/{path}/move` exist. What genuinely was missing: any
wikilink rewriting on move/rename (deliberately deferred since Phase 3,
now reversed — see `docs/06-DATA-MODEL.md`), a `move_folder` MCP tool,
and all of the UI below.

**Scope decisions**
- Build the six items enumerated in `assets/update.txt`. Don't add
  NoteDiscovery layout elements that front features dotNotes doesn't
  have (e.g. its left icon rail's tags/settings entries).
- "New from Template", "New Drawing" and the favorite/star icon are
  rendered **disabled** — see `docs/03-FEATURE-SPEC.md`'s "Present in
  the UI but deliberately not implemented". Export, print, fullscreen
  and copy-link need no backend and are fully wired; share reuses the
  existing share modal.
- Match NoteDiscovery's *structure*, not its hardcoded palette: every
  new component uses the Phase 9 theme CSS variables (an accent variable
  may be added to both themes) so light/dark keeps working.
- Frontend behavior that only a browser can exercise (drag-and-drop,
  `+New` menu wiring, tab switching and per-mode toolbar visibility)
  gets a committed Playwright suite under `tests/e2e/`. It's dev-only
  test tooling — not a frontend build step, not part of `dotnet test`,
  and nothing it installs ships in the app or the Docker image.

**Tasks**
1. `DotNotes.Core`: one orchestration service for note move, folder
   move, link rewriting and index consistency, used by both REST and
   MCP; span-aware wikilink parsing (the parser currently returns no
   source positions) with code-block awareness; name validation for new
   names. Contract: `docs/06-DATA-MODEL.md`'s "Folder & note
   move/rename".
2. REST: move endpoints call the service and return `rewrittenNotes`
   (`docs/04-API-SPEC.md`); `FoldersEndpoints`' inline index rebuild is
   replaced by the service.
3. MCP: `move_note` calls the service; new `move_folder` tool
   (`docs/05-MCP-SPEC.md`).
4. Frontend pass 1 — sidebar: native HTML5 drag-and-drop onto folders
   and a vault-root drop zone, right-click context menu (Rename, Move
   to…) as the keyboard-accessible fallback, remove the Phase 9 "Move"
   row button, single `+New` dropdown, "Drag=Move" hint.
5. Frontend pass 2 — main content: Home/folder-browse view (breadcrumb,
   root-only app name + tagline, summary line with `+New`, folder card
   grid); note editor header (inline title = rename, undo/redo, delete,
   "Edited `<date>`"), Edit/Split/Preview tab control replacing the
   Phase 9 toggle (reusing its session-persisted state), icon row,
   formatting toolbar hidden in Preview. After any move, reload an open
   note listed in `rewrittenNotes` so a stale buffer can't autosave over
   the rewrite.
6. Tests: rewrite correctness (rename keeps wikilinks/backlinks
   resolving; folder move with nested notes/subfolders; aliases kept;
   code blocks untouched; unaffected bare-title links byte-identical),
   rename-collision and invalid-name rejection, `move_folder` MCP
   parity, and the `tests/e2e/` browser suite.

**Exit criteria**
- [x] Renaming or moving a note or folder — via drag-and-drop, the
      context menu, the inline title, REST or MCP — leaves every
      `[[wikilink]]` that pointed at it resolving, and the graph,
      backlinks and search are correct in the very next request.
- [x] Every item in `assets/update.txt` §1–5 is visible and working,
      with disabled placeholders exactly where
      `docs/03-FEATURE-SPEC.md` says, in both light and dark themes.
- [x] `dotnet test` passes, and the `tests/e2e/` suite passes against a
      running instance.

**Delegate to:** `backend-architect` (task 1) in parallel with
`frontend-integrator` (task 4 — needs no backend change, the move
endpoints already exist) → `api-developer` (task 2) in parallel with
`mcp-server-engineer` (task 3) → `frontend-integrator` (task 5) →
`qa-test-engineer` (task 6, plus a final verification pass).

---

## Phase 11 — v0.1 stabilization pass

**Goal:** fix six bugs found in testing of the Phase 10 UI
(`assets/update.txt`, gitignored local reference material), then a full
regression pass before calling the build v0.1-ready. All six are
frontend-only; no REST/MCP contract changes.

1. Folder-browse view: render note cards (not just folder cards) for
   notes directly in the current folder; true empty-state only when a
   folder has zero notes and zero subfolders.
2. Split view: draggable resize divider between Edit/Preview panes,
   content wraps instead of horizontal-scrolling, proportional recompute
   on window resize and sidebar collapse (#4), ratio persisted in
   `localStorage` (`docs/02-ARCHITECTURE.md`'s new client-side
   UI-preferences note).
3. The two arrow icons beside the note title are previous/next-note
   navigation within the current folder's note list (per the active
   sort order, #5) — not undo/redo, correcting a mislabel carried over
   from the NoteDiscovery reference in Phase 10. Disabled at the first/
   last note; native browser undo/redo (ctrl+Z, already wired via
   `execCommand`) is unaffected.
4. Collapsible left sidebar, persisted state, editor/split area reflows
   into the freed width.
5. Drag-to-reorder folders among siblings (custom manual order) coexists
   with the existing drag-onto-a-folder move/reparent; a sort-toggle
   button cycles name-ascending/descending. A fresh manual drag switches
   that folder level into custom-order mode; the sort toggle overrides
   back to alphabetical until the user drags again. Sort mode and custom
   order persisted per folder level in `localStorage`.
6. One reusable themed modal component (title, subtitle, labeled input,
   Cancel/primary action) replacing every native `prompt()`/`confirm()`:
   New Note, New Folder, Rename, delete confirmations.

**Exit criteria**
- [x] All six items above are visible and working in both light and dark
      themes, matching `assets/update.txt`'s descriptions.
- [x] Regression pass confirms folder move/reparent, rename, theme
      toggle, Edit/Split/Preview switching, and share links still work.
- [x] `dotnet test` and the `tests/e2e/` suite both pass, with new
      coverage for: note cards in folder view, previous/next navigation
      respecting sort order, folder reorder + sort-toggle persistence,
      and the modal component's create/cancel paths.

**Delegate to:** `frontend-integrator` (all six fixes, since they share
the same files and a single pass avoids merge conflicts) →
`qa-test-engineer` (regression pass, new test coverage, final
verification).

---

## Phase 12 — Tasks & Kanban (v0.2.0)

**Goal:** a task tracker inside the vault, ported from Backlog.md's
file format: a task is a note with YAML frontmatter (`id`, `status`, …),
shown on a Kanban board and an All Tasks list, editable from a task
panel or the normal editor, and driveable by AI assistants over MCP.
Plus a client-side Pomodoro timer on the board. Full design, feature
inventory and every autonomous judgement call:
`docs/features/tasks-kanban/PLAN.md`.

**Exit criteria**
- [x] Task notes parse/serialize byte-stably with Backlog.md-compatible
      frontmatter and section markers; plain notes are unaffected.
- [x] `/api/tasks*` endpoints and the task MCP tools + workflow resource
      match `docs/04-API-SPEC.md` / `docs/05-MCP-SPEC.md`.
- [x] Sidebar TASKS section, Kanban board with persisted drag-and-drop
      ordering, task modal with AC checklist, All Tasks list, Pomodoro.
- [x] Note editor detects concurrent edits (409 on stale save).
- [x] Version 0.2.0; `dotnet test` green with zero warnings.

**Delegate to:** `backend-architect` (Core tasks domain, index,
service) → `api-developer` + `mcp-server-engineer` (in parallel);
`frontend-integrator` (UI, in parallel with the backend, against the
plan's contract) → `qa-test-engineer` (tests, parity, review).

---

## When to delegate — quick reference

| Concern | Subagent |
|---|---|
| Solution structure, DI, config, cross-cutting services | `backend-architect` |
| REST endpoints, DTOs, validation, error handling | `api-developer` |
| Wikilinks, backlink index, graph model/endpoint | `markdown-graph-engineer` |
| Search index and ranking | `search-engineer` |
| Static frontend, editor, rendering libraries, UI wiring | `frontend-integrator` |
| MCP tools and transport | `mcp-server-engineer` |
| Tests, edge cases, feature-parity verification | `qa-test-engineer` |
| Dockerfile, compose, WSL2 deployment docs | `devops-engineer` |
