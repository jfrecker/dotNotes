# Changelog

All notable changes to dotNotes are recorded here, most recent first.

## v0.2.0 — Tasks & Kanban, Pomodoro, task MCP tools (2026-09-24)

A task tracker inside the vault, ported from
[Backlog.md](https://github.com/MrLesk/Backlog.md)'s task model (MIT —
see `NOTICE`): a task **is a note** with YAML frontmatter, so it stays a
plain, hand-editable `.md` file. Full design, feature inventory and every
autonomous judgement call: `docs/features/tasks-kanban/PLAN.md`
(Phase 12 in `docs/01-PROJECT-PLAN.md`).

### Added — tasks as notes

- **Task files** use Backlog.md-compatible frontmatter (`id`, `title`,
  `status`, `assignee`, `labels`, `priority`, `milestone`,
  `dependencies`, `created_date`, `updated_date`, `ordinal`) and section
  markers (`<!-- SECTION:DESCRIPTION:BEGIN -->`, `<!-- AC:BEGIN -->`, plan,
  notes, final summary). Unknown keys and unrecognised body content are
  preserved verbatim. A note is a task when its frontmatter has `id` and
  `status`; notes without task frontmatter behave exactly as before.
- **New tasks** are written to the configurable `tasks/` folder as
  `TASK-12 - Title.md`. Ids are never reused (archived tasks count).
  Renaming a task from the panel or the editor's title field renames the
  file and rewrites incoming `[[wikilinks]]`.
- **`DotNotes.Core.Tasks`**: `TaskMarkdown` (parse/serialize, YamlDotNet
  for reading, own emitter for writing), `InMemoryTaskIndex` (a derived
  cache fed by the existing file watcher, like the link/search indexes),
  `TaskService` (create/update/move/archive/convert, all writes
  serialised, patch semantics so board edits merge with editor edits),
  `TaskOrdering` (Backlog.md's 1000-step ordinals with midpoint insert and
  rebalance).
- **Configuration** (`Tasks` section / `Tasks__*` env vars):
  `Folder`, `IdPrefix`, `Statuses`, `DefaultStatus`, `Priorities`;
  passthrough for folder and prefix in `docker-compose.yml`,
  `.env.example`, the Podman unit and `DEPLOYMENT.md`.

### Added — UI

- **TASKS** sidebar section above *Folders & Notes* with **All Tasks** and
  **Kanban Board** and an active-task count.
- **Kanban Board**: columns from the configured statuses with count
  badges, cards (title, id, excerpt, assignee, labels, priority,
  acceptance-criteria progress, created date), drag-and-drop between and
  within columns (persisted in `ordinal`), text/label/assignee/priority
  filters, `+ New Task`, live refresh (revision polling).
- **Task panel**: every field, interactive acceptance-criteria checklist,
  plan/notes/final summary, *Open note*, Archive.
- **All Tasks** table: sortable, filterable, optional archived rows.
- **Convert to task** in the note right-click menu (never automatic).
  Task notes preview without their raw YAML and with a compact task
  header.
- **Pomodoro timer** (top right of the board): adjustable focus/short/long
  durations and cycles before a long break; start/pause/reset/skip;
  countdown and phase; chime and/or browser notification; optional linked
  task; settings and state persist in `localStorage`; keeps running across
  in-app navigation with a compact top-bar indicator.

### Added — REST and MCP

- **`/api/tasks`** endpoints (config, list, board, revision, get, create,
  patch, move, archive, convert) — `docs/04-API-SPEC.md`.
- **MCP tools** `list_tasks`, `get_task`, `create_task`, `update_task`
  (fields, status, acceptance-criteria add/remove/check/uncheck,
  plan/notes set/append, final summary), `move_task` (status + position),
  `archive_task`, `get_board`, `search_tasks`, `get_task_workflow`, plus
  the resource `dotnotes://workflow/tasks` — `docs/05-MCP-SPEC.md`. The
  existing MCP tools are unchanged.

### Changed — concurrent edits

- **`PUT /api/notes/{path}`** accepts an optional `expectedUpdatedAt`; a
  stale value returns `409 conflict` with `currentUpdatedAt`. The editor
  sends it and offers *Reload latest* or *Keep mine* instead of silently
  overwriting a change made from the board or an MCP client. Omitting it
  keeps last-write-wins, so `update_note` and older clients are unaffected.
  The check is best-effort (check-then-write).

### Changed — version

- Version is now defined once, in the new `Directory.Build.props`
  (`0.2.0`); `GET /api/config` and `get_config` report the real version
  (previously the SDK default `1.0.0.0`) and add `features.tasks`. The
  sidebar footer shows it and the Docker image carries an OCI version
  label. Compose/Podman image tags are unchanged.

### Fixed during development

- Configured `Tasks:Statuses` are bound as a replacement for, not an
  append to, the built-in list (a .NET configuration-binder gotcha).
- Task timestamps from the startup scan use the file's real modification
  time.

## Unreleased — folder delete, sidebar drag fix, Podman support (2026-09-19)

Two bug fixes from real use plus Podman as a first-class runtime
alongside Docker.

### Fixed — deleting a folder from the UI did nothing

The feature did not exist at any layer: `INoteRepository` had no folder
delete, `FoldersEndpoints` mapped only `POST /api/folders/{**path}`
(create and `/move`), and the sidebar's right-click menu offered only
*Rename* and *Move to…*.

- **`DELETE /api/folders/{**path}`** (new row in `docs/04-API-SPEC.md`)
  — removes a folder and everything inside it at any depth. `204` on
  success, `404 not_found` if no folder is there (including when a
  *note* occupies that path — deleting that stays
  `DELETE /api/notes/{**path}`'s job), `400 invalid_path`, `503
  vault_unavailable`.
- **`INoteRepository.DeleteFolderAsync`** routes through the same
  `ResolveFolderPath` choke point every other folder operation uses, so
  traversal/escape protection is written once. Because `IsWithinVaultRoot`
  compares against the root *with* a trailing separator, the vault root
  can never resolve there — deleting it is structurally impossible
  rather than a special case, and `DELETE /api/folders/` answers `400`.
  No `IVaultReorganizationService` orchestration is needed: a recursive
  directory delete removes each file individually, so `VaultWatcherService`
  sees a per-file `Deleted` event and reconciles the link/search indexes
  exactly as it does for a single-note delete.
- **Sidebar right-click menu gains a danger-styled *Delete*** (notes and
  folders), behind the existing themed confirmation modal. `js/menu.js`
  grew a `danger: true` item flag for it. The tree and wikilink cache
  refresh afterwards, and if the open note was inside the deleted
  folder the editor navigates Home — clearing `currentPath` *before* the
  request so a pending autosave cannot write the note back.

### Fixed — dragging a folder in the sidebar froze the page

`Tree.reorderFolder()` redrew the whole tree (`container.innerHTML = ''`)
*synchronously inside* the `drop` event dispatch, detaching the drag
source mid-gesture. The browser then never fired `dragend` on that
detached node — confirmed in Chromium: `dragstart` and `drop` fire,
`dragend` never does. So `draggedEntry` stayed non-null for the life of
the page, every drag-feedback class stayed applied (the root drop zone
was stuck reading "Drop here to move to root"), and the browser's own
drag session was never terminated, leaving the page unresponsive to the
mouse until a reload.

- **`afterDragEvent(fn)`** (`setTimeout`, a macrotask — a microtask
  still runs before `dragend`) now wraps every drop branch that touches
  tree DOM: the reorder redraw, the folder-row move, and the root
  drop-zone move.
- **`endDrag()`** is the one teardown for a drag however it ended,
  clearing `draggedEntry` and stripping every feedback class via a
  tracked `dragFeedbackNodes` set. Called from the row's `dragend`, from
  document-level `dragend` *and* `drop` (bubble phase, so row handlers
  still see `draggedEntry`), and defensively at the next `dragstart`.
- Drop handlers capture the dragged entry locally and pass it to
  `isInvalidDropTarget(entry, dragged)` rather than reading module state
  teardown may have cleared. `dragleave` now ignores moves onto a row's
  own child spans, so the highlight no longer flickers.
- Client- *and* server-side refusal of a folder dropped onto itself or a
  descendant is unchanged (`isInvalidDropTarget` / `MoveFolderAsync`'s
  `400`), as is the `409` on a name collision; both are now pinned by
  browser tests.

### Added — Podman support (rootless and rootful)

Same `Dockerfile`, same `docker-compose.yml` — no `Containerfile` and no
Podman-specific compose file to keep in sync. All new settings are empty
by default, so the Docker workflow is byte-for-byte unchanged (verified
by building and running the image end-to-end).

- **`docker-entrypoint.sh` handles a non-root start.** Under rootless
  Podman with `--userns=keep-id` the container begins as an unprivileged
  uid, where neither `chown` to another uid nor `setpriv`'s uid/group
  change is permitted — the old script died on `chown: Operation not
  permitted`. It now detects `id -u != 0`, skips the chown/privilege
  drop, and execs the app directly; `PUID`/`PGID` are simply unnecessary
  in that mode.
- **`VAULT_MOUNT_OPTS`** (empty by default) appends mount options to the
  vault bind mount — set to `:Z` on SELinux-enforcing hosts.
- **`USERNS_MODE`** (empty by default) maps to the service's
  `userns_mode`; `keep-id` under rootless Podman keeps vault files owned
  by the host user instead of a subordinate uid.
- **`deploy/podman/dotnotes.container`** — a Quadlet unit for running
  dotNotes as a (rootless or rootful) systemd service.
- **README.md gains a "Running with Podman" section**: prerequisites,
  compose and plain `podman run`, `:Z` vs `:z`, rootless permissions,
  Quadlet plus auto-start on boot, updating, and troubleshooting.
  `DEPLOYMENT.md` points at it and documents the two new `.env`
  variables.
- The `Dockerfile` already used fully-qualified image names and an
  unprivileged port (5175); both now carry comments explaining why they
  matter for Podman, so neither is "simplified" away later.

## Phase 11 — v0.1 stabilization pass (2026-09-17)

Frontend-only bug-fix pass over the Phase 9/10 UI, from real-use testing
(`assets/update.txt`, gitignored). No REST/MCP contract changes.

- **Home / folder-browse view now renders note cards, not just folder
  cards** (`js/app.js`'s `renderFolderGrid`) — a folder containing only
  notes and no subfolders used to render as empty even though the
  summary line above it correctly counted them. True empty-state only
  when a folder has zero notes *and* zero subfolders.
- **Split-mode draggable resize divider** (`#split-divider`) between the
  editor and preview panes, driven by pointer events. The ratio is a
  `flex-basis` *percentage* of the shared flex container rather than a
  pixel width, so both panes reflow proportionally on a window resize or
  a sidebar collapse for free, with no resize listener needed. Ratio
  persisted in `localStorage`.
- **Note editor header's two arrow icons are previous/next-note
  navigation**, not undo/redo — a mislabel carried over from the
  NoteDiscovery reference in Phase 10. They step to the adjacent note
  within the current note's folder, in the sidebar's active display
  order (`js/tree.js`'s `getSortedChildren`, the one shared source of
  truth for sort order), and disable at the first/last note. Native
  browser undo/redo (ctrl+Z) in the textarea needed no JS and is
  untouched.
- **Collapsible left sidebar** (`#sidebar-toggle-btn` in the top bar),
  collapsed/expanded state persisted in `localStorage`. A pure CSS width
  toggle on `#sidebar` — the main content area is already a `flex-1`
  flex item, so it reflows into the freed width automatically.
- **Sidebar folder reorder + sort toggle** (`js/tree.js`): dragging a
  folder to a different position among its siblings sets that parent's
  custom manual order (persisted in `localStorage`, keyed by parent
  path), distinguished from the pre-existing drag-onto-a-folder
  move/reparent by drop zone (top/bottom ~30% of a sibling row reorders,
  the middle ~40% reparents). A new sort-toggle button in the sidebar
  cycles name-ascending/descending, which overrides every folder level
  back to alphabetical in the *view* (without discarding any level's
  saved custom order) until the user next drags to reorder.
- **One reusable themed modal** (`js/app.js`'s `Modal.prompt`/
  `Modal.confirm`, built on the single `#rename-modal` markup that used
  to be rename-only) replacing every remaining `window.prompt()`/
  `window.confirm()` call: New Note, New Folder, Rename (already
  modal-based since Phase 10, now sharing the same component), the
  "create this note?" wikilink-click flow, and note deletion (with a
  danger-styled primary button).
- `tests/e2e/specs/new-menu.spec.js`'s New Note test updated to drive the
  themed modal instead of listening for a native `dialog` event.
- QA pass: added five new `tests/e2e/specs/` files covering the six fixes
  above end-to-end — `home-note-cards.spec.js` (note cards in a
  notes-only folder, mixed folder+note contents, and the true
  zero-notes-zero-subfolders empty state), `prev-next-navigation.spec.js`
  (stepping through a folder's notes in sort order and disabling at both
  ends), `folder-reorder-sort.spec.js` (drag-reorder vs. the pre-existing
  drag-onto reparent, order surviving a page reload, and the sort-toggle
  overriding a custom order back to alphabetical), `modal.spec.js` (New
  Folder's create path plus both cancel paths — Escape and the Cancel
  button — leaving nothing behind), and
  `split-divider-sidebar-collapse.spec.js` (bonus coverage for the
  resize divider and sidebar-collapse persistence). Full suite: 29/29
  passing. `dotnet test` (backend, untouched by this phase): 422/422
  passing. Regression pass additionally spot-checked share-link
  creation/access/revocation via `curl` against a scratch vault (no
  e2e spec exists for it) — create, `GET /shared/{token}`,
  `GET /api/share/{token}/content`, idempotent `DELETE`, and the
  expected 404s after revocation and for a non-existent note all
  behaved per `docs/04-API-SPEC.md`.

## Phase 10 — NoteDiscovery layout parity, link-rewriting moves (2026-09-16)

Reverses the Phase 3/9 "no automatic wikilink rewriting" decision:
requested after real use showed that cheap reorganization (rename,
drag-and-drop, coming later in this phase) makes silently orphaning
backlinks on every move the dominant way to corrupt a vault's link
graph. See `docs/06-DATA-MODEL.md`'s "Folder & note move/rename"
section for the exact rules (minimal, style-preserving, alias-keeping,
code-block-excluded).

- New `DotNotes.Core.Reorganization.IVaultReorganizationService`
  (`MoveNoteAsync`/`MoveFolderAsync`), the single place both the REST
  endpoints and MCP tools will call for a move — so the two surfaces
  can't diverge in rewrite/index behavior. Finds candidates for
  rewriting by scanning every note's content fresh off disk rather than
  via `ILinkIndex.GetBacklinks`, specifically because the frontend
  flushes autosave immediately before a move and the in-memory index
  can still be a debounce window behind — trusting it would silently
  orphan a link the user just typed.
- New span-aware `WikiLinkParser.ParseWithPositions` (additive; the
  existing `Parse`/`WikiLinkOccurrence` are untouched) — locates exactly
  the target-text span inside `[[...]]` so a rewrite touches nothing
  else, and flags whether an occurrence sits inside a fenced or inline
  code span (never rewritten).
- Name validation for **new** names only (create, or a move's
  destination) — rejects empty/whitespace-padded segments, control
  characters, host-invalid filename characters, and `[`, `]`, `|`.
  Never applied to a path that already exists, so a pre-existing oddly
  named note stays fully usable (open, autosave, delete, move away).
- Two correctness issues found and fixed during independent review
  before this landed, both regression-tested:
  - **Lost-edit race**: an early draft rewrote notes from content
    captured during the pre-move scan, so a concurrent save (a
    debounced autosave landing mid-move, or a parallel MCP
    `update_note`) to a note about to be rewritten would be silently
    overwritten by the stale scan copy. Fixed by re-reading each note
    fresh from disk immediately before computing and saving its
    rewrite, shrinking the window to read→compute→save.
  - **Incomplete re-indexing**: `InMemoryLinkIndex.NoteChanged` resolves
    a note's links once, at call time, and never re-resolves other
    notes later — so a move that changes how an *unrelated* note's
    existing link resolves (a previously-missing bare-title link that
    now matches the moved note's new name; a rename that creates a
    title collision and flips an unrelated tie-break) left the index
    stale for that note even though its own text never changed. Fixed
    by re-indexing every link-bearing note after a move, not just the
    ones whose content was actually rewritten.
- Index updates (delete old paths, then two passes registering/
  re-resolving every new and rewritten path) happen synchronously
  before either method returns — the graph, backlinks and search are
  correct in the very next request, same guarantee Phase 9's folder
  move already gave, now extended to note moves and to rewritten
  content.
- REST: `POST /api/notes/{**path}/move` and `POST /api/folders/{**path}/move`
  now call `IVaultReorganizationService` instead of `INoteRepository`
  directly, and both return `rewrittenNotes` per `docs/04-API-SPEC.md`.
  `FoldersEndpoints`' old inline `linkIndex.RebuildAsync()`/
  `searchIndex.RebuildAsync()` calls (and the now-unused parameters that
  carried them) are gone — the service already leaves both indexes
  correct before returning.
- MCP: `move_note` calls the same service; new `move_folder` tool with
  the same `{ path, destinationPath }` → `{ path, rewrittenNotes }`
  shape, funneling the same four exception types into `McpException`.
- Sidebar drag-and-drop and the "+New" menu (built in the prior
  frontend pass, handling `rewrittenNotes` defensively before this
  landed) are now fully live end-to-end: a rename or move that breaks a
  bare-title wikilink is rewritten and reflected in the graph, search
  and backlinks in the very next request, with no polling.
- 417 tests passing (up from 344 at the start of this phase). Manually
  verified end to end (REST via curl, MCP via a real
  `@modelcontextprotocol/sdk` client): rewrite happy paths, 404/409/400
  error mapping unchanged, and a folder-into-its-own-descendant move
  correctly rejected for both surfaces.
- Frontend main-content pass: a Home/folder-browse view (breadcrumb,
  app name + tagline at the root only, "X notes, Y folders" summary
  with "+New", a folder-only card grid — deliberately never cards
  individual notes, per `assets/update.txt` §3); a note editor header
  with an inline title that *is* the rename control (funnels into the
  exact same `performMove` the sidebar's right-click Rename uses, not
  a second implementation), undo/redo, delete, an "Edited `<date>`"
  label, and an icon row (favorite/star disabled per
  `docs/03-FEATURE-SPEC.md`; export/print/copy-link/fullscreen fully
  wired; share reuses the existing modal); the Edit/Split/Preview
  toggle is now a 3-way tab control reusing Phase 9's session-persisted
  state; a 13-button formatting toolbar (bold through table), hidden
  in Preview, built on the pre-existing `insertAtCursor` helper.
- Two real bugs found and fixed during live verification: a CSS
  specificity clash (`.note-editor-view{display:flex}` vs. Tailwind's
  `.hidden{display:none}` at equal specificity) left the editor
  visible underneath the new Home view; and toolbar/upload insertions
  spliced `editorEl.value` directly, which never touched the
  textarea's native undo/redo history, so the new undo/redo buttons
  did nothing for them — fixed by routing every insertion through
  `document.execCommand('insertText', ...)` instead.
- 417 tests passing, unchanged (frontend-only). Independently verified
  live (headless Chromium) end to end: Home navigation, inline-title
  rename correctly rewriting an incoming wikilink elsewhere in the
  vault, undo/redo genuinely restoring/reapplying a toolbar edit, and
  the toolbar hiding in Preview.
- Final QA pass: audited existing move/rewrite test coverage against
  every rule in `docs/06-DATA-MODEL.md` and filled the genuine gaps
  found — a bare-title link that becomes ambiguous post-move (must
  fall back to an explicit path, not silently resolve to the wrong
  note), multiple occurrences of the same link in one note (all
  rewritten, not just the first), a moved note's own unrelated
  outgoing links (byte-identical, untouched), and REST's missing `503`
  coverage for both move endpoints (MCP already had it). Everything
  else audited — alias-preserving rewrites, code-block exclusion, and
  name-validation edge cases at every entry point — was already
  correctly covered; no production bug found.
- New `tests/e2e/` Playwright suite (dev-only, not part of `dotnet
  test`, nothing it installs ships in the app or Docker image — see
  `tests/e2e/README.md`): 17 tests covering sidebar drag-and-drop, the
  right-click/keyboard context menu, the "+New" dropdown (including
  that Template/Drawing stay inert), Home-view navigation, inline-title
  rename propagating a wikilink rewrite into a second, currently-open
  note, Edit/Split/Preview tab + toolbar visibility, and the light/dark
  theme toggle actually repainting the new Phase 10 UI.
- 422 tests passing (was 417; +3 `DotNotes.Core.Tests`, +2
  `DotNotes.Api.Tests`), plus the new e2e suite (17/17, run
  independently and confirmed green outside the reporting agent).
- Phase 10 is now complete — every exit criterion and
  `docs/03-FEATURE-SPEC.md` Should-have checklist item for this phase
  is met and independently verified.

## Phase 9 — folder management, editor view modes, theme toggle (2026-09-16)

Post-launch feature request from real daily use. Two premises in the
original request were checked against the code before starting and
turned out to be wrong: there was no existing `create_folder`/`move_note`
MCP tool (only the 7 from Phase 6), and the sidebar already rendered a
proper nested folder tree, not a flat list — the actual gap was pure
interaction (create/move/rename), not rendering or an existing-but-
unwired domain layer. See `docs/06-DATA-MODEL.md`'s "Folder & note
move/rename" section for the full design.

- **`DotNotes.Core`**: three new `INoteRepository` methods —
  `CreateFolderAsync` (mkdir -p semantics, idempotent), `MoveAsync`
  (note move/rename), `MoveFolderAsync` (folder move/rename, recursive).
  All three refuse to silently overwrite an existing destination
  (`File.Move`/`Directory.Move` with `overwrite: false`, translated into
  a new `DestinationAlreadyExistsException` rather than a raw
  `IOException`), auto-create missing destination parent folders, and
  are guarded against a vanished vault root exactly like every existing
  method. A new `SourceNotFoundException` covers moving something that
  doesn't exist. `MoveFolderAsync` explicitly rejects moving a folder
  into itself or one of its own descendants. The existing note-path
  validation logic was refactored into a shared helper so folder-path
  validation can't drift from it — the single most safety-critical code
  in the project per `CLAUDE.md`. 27 new `DotNotes.Core.Tests`.
- **REST**: `POST /api/notes/{path}/move`, `POST /api/folders/{path}`,
  `POST /api/folders/{path}/move` per `docs/04-API-SPEC.md`. Routing
  note: ASP.NET Core's Minimal API router rejects a literal segment
  after a catch-all parameter (`{**path}/move` can't be registered as
  its own route), so both `/move` variants are dispatched internally by
  the single existing catch-all POST route detecting a trailing `/move`
  segment — the external contract (URL/verb/body/response) is exactly as
  documented, this is purely an internal technique. **A folder move
  synchronously rebuilds both the link and search indexes before
  responding** — `VaultWatcherService`'s file-watcher can only raise one
  event for a renamed directory itself, never one per nested note, so
  relying on it would leave backlinks/search/graph silently stale for
  the whole moved subtree until a restart. Verified live: moved a folder
  containing two wikilinked notes and confirmed `GET /api/graph` and
  search reflected the new paths in the very next request, zero delay.
  17 new `DotNotes.Api.Tests`.
- **Frontend**: editor view-mode toggle (Edit-only / Split /
  Preview-only) in the editor toolbar, `sessionStorage`-persisted,
  defaults to Split (today's prior behavior). Light/dark theme toggle in
  the top nav, `localStorage`-persisted, defaults to
  `prefers-color-scheme` when unset — implemented via CSS custom
  properties under a `[data-theme="dark"]` attribute selector (the
  precompiled Tailwind build has no `dark:` variants to hook into), with
  every Tailwind utility class actually in use re-declared under that
  selector. Verified live in a real headless-Chromium session, including
  screenshots of both themes across all three view modes and the share
  modal. Code blocks/Mermaid diagrams intentionally stay light-themed
  even in dark mode (no dark `highlight.js` theme is vendored) — a
  deliberate "light card on a dark page" pattern, not a bug.
- **MCP**: `create_folder` and `move_note` tools added to
  `DotNotesMcpTools.cs`, matching every existing tool's convention of
  funneling `InvalidNotePathException`/`DestinationAlreadyExistsException`/
  `SourceNotFoundException`/`VaultUnavailableException` into a specific
  `McpException` message rather than a raw exception. Verified live
  against a real MCP client (`@modelcontextprotocol/sdk`'s
  `StreamableHTTPClientTransport`): both tools listed with correct
  schemas, and a rejected `move_note` overwrite attempt confirmed to
  leave both the source and the existing destination file untouched.
  No `move_folder` MCP tool — stays REST-only for now, per
  `docs/05-MCP-SPEC.md`.
- **Frontend folder-tree UI**: a "+ New folder" button, and a
  hover-revealed "Move" button on every tree row (file or folder) that
  prompts for a new vault-relative path — a single mental model covers
  both "rename in place" and "move to a different folder", since the
  backend doesn't distinguish the two either. Deliberately a plain
  `window.prompt`, not a context menu or drag-and-drop, matching every
  other tree/file interaction already in this app. Flushes any
  in-flight autosave before a move (so a stale debounced save can't
  silently resurrect a note at its old path afterward), follows the
  editor to a moved note's new path if it was the one open, and
  refreshes the wikilink-resolution cache after every move. A real
  layout bug was caught only by live-screenshotting the page, not by
  reading the CSS: adding a third sidebar button caused even the
  pre-existing "+ New note" label to start wrapping mid-word — fixed by
  splitting the sidebar toolbar into two rows.
- 344 tests passing (up from 291 at the start of this phase: +27 Core
  for the domain layer, +17 Api for the REST endpoints, +9 Api for the
  MCP tools). Both the folder-management REST/MCP layer and the
  view-mode/theme toggles were verified against a real running instance
  (curl for the API/MCP layers, a real headless-Chromium session via
  Playwright for every frontend interaction, including full create →
  rename → move-into-folder → move-folder-itself → error-message
  round-trips and a regression pass over the previously-shipped
  view-mode/theme toggles to confirm the tree/toolbar markup changes
  didn't break them).
- All four Phase 9 exit criteria met — see `docs/01-PROJECT-PLAN.md`.
  "Folder & subfolder management", "Editor view-mode toggle", and
  "Light/dark theme toggle" are now ticked in
  `docs/03-FEATURE-SPEC.md`.

## Deployment polish — full guide, native build path, `.env` overrides (2026-09-16)

Follow-up to Phase 7 now that the whole project is done: the user asked
for a ready-to-use build, a full deployment guide, and confirmation of
the Docker Compose setup.

- Added `DEPLOYMENT.md` — the canonical, standalone deployment guide
  (previously a ~140-line section buried in `README.md`, now expanded
  significantly): prerequisites per path, quick start, every deployment
  mode (Docker Compose on WSL2 with Docker Desktop or plain Docker
  Engine, Docker Compose on plain Linux, native on Windows, native on
  Linux with an example systemd unit), a full configuration reference
  table (every `appsettings.json` key, its env var override, and its
  Docker `.env` variable if any — including the `Server:Port` vs.
  `ASPNETCORE_URLS` distinction, which is easy to get wrong), updating
  without touching the vault, backup/restore (`rsync`/`tar` and `git`,
  both proven out in Phase 8's QA pass), and reverse-proxy/HTTPS
  guidance with a minimal Caddy example — paired with an explicit
  security caveat: the app has no authentication on `/api/*` or `/mcp`
  beyond the per-note share-token mechanism, so exposing the whole app
  (not just one share link) beyond a trusted LAN needs an added auth
  layer, which is out of scope for dotNotes itself.
- Verified a **native (non-Docker) build path** for the first time —
  required since `CLAUDE.md` mandates native Windows support, not just
  Docker: `dotnet publish` both framework-dependent (~13 MB, recommended
  default) and self-contained (~120 MB, for a target machine with no
  .NET runtime installed), both built and run successfully against a
  scratch vault. Framework-dependent runs need `ASPNETCORE_URLS` set
  explicitly (a native publish otherwise falls back to Kestrel's default
  `http://localhost:5000`, not `5175` — a real gotcha, now documented).
- `docker-compose.yml` gained `.env`-file variable substitution
  (`HOST_PORT`, `VAULT_PATH`, `PUID`, `PGID`, `SHARING_ENABLED`,
  `MCP_ENABLED`) plus a new `.env.example` template, so common overrides
  are a one-line edit instead of hand-editing YAML. Confirmed
  backward-compatible: with no `.env` file, `docker compose config`
  resolves to byte-identical defaults as before.
- Re-verified the full Docker Compose cycle end-to-end (fresh
  `--no-cache` build → healthy → write a note → restart → note
  survives), plus the `.env` override path specifically (custom
  `HOST_PORT`/`VAULT_PATH`/`PUID`/`PGID` all confirmed to take effect).
- `README.md` rewritten: the stale "planning bundle, not a codebase"
  opening (left over from before the project was built) is gone,
  replaced with an actual description of the finished app, a quick
  start pointing at `DEPLOYMENT.md`, and a "Project structure" section
  in place of the old from-scratch "How to hand this over" build
  instructions.
- No C# source changed; all 291 tests unaffected.

## Phase 8 — Hardening & feature-parity QA (2026-09-16)

This is the last phase in `docs/01-PROJECT-PLAN.md` — both exit criteria
are now met: every Must-have in `docs/03-FEATURE-SPEC.md` is checked,
and there is no known data-loss edge case.

- **Feature-parity pass**: every Must-have/Should-have/ticked Could-have
  feature was manually re-exercised against a live instance (not just
  re-trusted from its checkbox) — note CRUD, file tree, editor/preview,
  autosave, wikilinks/backlinks/graph, full-text search, all 7 MCP tools
  (via a real `@modelcontextprotocol/sdk` client over the actual `/mcp`
  HTTP transport), sharing + QR codes, media embedding, and a real
  Docker Compose build/run/restart cycle. Nothing contradicted its
  checked-off status. (Click-driven UI interactions — wikilink
  navigation, checkbox-toggle-in-preview — were verified by exercising
  their underlying REST calls plus source review, not literally clicked
  in a browser: this sandbox has no working headless-browser runtime.)
- **Edge cases exercised live**: empty vault (graceful everywhere, no
  crashes); an 11 MB / 60,000-line note (sub-second search, no
  slowdowns); unicode/space/special-character (`#`, `?`, `&`) note
  names end-to-end through REST, wikilinks, MCP, and sharing (regression
  test added: `FileSystemNoteRepositoryTests.SaveAsync_UnicodeSpaceAndSpecialCharacterPaths_RoundTripCorrectly`);
  25 concurrent writes to the same note (atomic temp-file-then-move
  means the file on disk is always exactly one writer's complete
  content, never a corrupt interleaving — regression test added:
  `SaveAsync_ConcurrentWritesToSameNote_NeverProducesCorruptedContent`;
  last-write-wins is a real, silent, and accepted characteristic of a
  single-user app with no cross-tab sync, not a corruption bug).
- **Backup story confirmed for real**: `git init`'d a scratch vault,
  edited/created notes through the app, confirmed `git status`/`git
  diff` behave exactly like any ordinary plain-text repo with zero
  app-specific handling — `.nd-shares.json` is human-readable/git-friendly
  too.
- **Found and fixed a real data-loss-risk bug**: a vault directory that
  transiently vanishes (an unmounted WSL2/network mount, a Docker
  bind-mount host directory momentarily gone) was silently treated by
  `FileSystemNoteRepository.SaveAsync` as "nothing here yet" —
  `Directory.CreateDirectory` fabricated a brand-new, empty vault root
  and wrote the new note into it, returning a normal `200 OK` while
  every pre-existing note became invisible to the app. Fixed:
  - New `VaultUnavailableException` (`DotNotes.Core.Notes`); every
    `FileSystemNoteRepository` method now checks the vault root still
    exists before touching disk, and refuses (rather than fabricating)
    when it doesn't.
  - REST endpoints (`NotesEndpoints.cs`, `SharingEndpoints.cs`) map it
    to `503 { "error": "vault_unavailable", "detail": "..." }`, matching
    the app's existing error-shape convention rather than a generic
    ASP.NET Core ProblemDetails body. This also fixed a sibling gap
    where `GetTreeAsync` had no error handling at all and leaked a raw
    500.
  - A second instance of the *same* underlying bug was found and fixed
    one layer down: `IShareTokenStore`/`IMediaStore` are lazily
    constructed singletons whose constructors also call
    `VaultPathValidator.EnsureVaultRootExists` — so the very first
    `/api/share`/`/api/upload` request landing during an outage could
    trigger the same silent fabrication, bypassing `FileSystemNoteRepository`'s
    new guard entirely. `Program.cs` now forces both to construct
    eagerly at startup, in the same legitimate "first run creates the
    vault" window `VaultWatcherService` already forces for
    `INoteRepository`.
  - MCP tools (`DotNotesMcpTools.cs`) now also catch
    `VaultUnavailableException` and surface it as a specific
    `McpException` message, matching how `InvalidNotePathException` was
    already handled there, instead of falling through to the SDK's
    generic replacement error message.
  - Reproduced live (moved the vault directory away while the app kept
    running) both before the fix (confirmed the bug) and after (confirmed
    a clean 503, nothing written, zero directory fabrication, and full
    recovery with no restart once the real directory was restored).
  - 291 tests passing (up from 279 at the start of this phase: +11 from
    the QA pass, +1 from the MCP consistency fix).
- Minor, non-blocking items noted but deliberately left unfixed (cosmetic,
  no data-loss risk): an inconsistent `413` error message for an upload
  right at Kestrel's raw body-size ceiling vs. the app's own size-limit
  check (`MediaEndpoints.cs`); verbose `ERR`-level server logging for
  expected/structured MCP tool errors (log noise only).
- **Go**: the app is solid enough for daily personal use. No known
  data-loss scenario remains open.

## Phase 7 — Docker & WSL2 self-host (2026-09-16)

- Added a multi-stage `Dockerfile` (repo root): `mcr.microsoft.com/dotnet/sdk:10.0`
  build stage publishes `DotNotes.Api` in Release config; the runtime
  stage (`mcr.microsoft.com/dotnet/aspnet:10.0`) contains only the
  published output plus a small entrypoint script. `ASPNETCORE_URLS=http://+:5175`
  binds Kestrel to all interfaces (not just loopback) so the host can
  reach it. A `HEALTHCHECK` hits `GET /healthz` via bash's `/dev/tcp`
  (no curl/wget installed in the image, keeping it dependency-free).
- Added `docker-compose.yml`: one service, a bind mount `./vault:/data/vault`
  (chosen over a named volume specifically so the vault stays directly
  browsable/backupable from the host — a WSL2 `\\wsl$\...` path or any
  Linux backup tool — matching CLAUDE.md's "notes are just files, no
  database" hard rule), port `5175:5175`, and env vars for every
  overridable config value (`Vault__RootPath`, `Sharing__Enabled`,
  `Mcp__Enabled`).
- Added `docker-entrypoint.sh`: starts as root just long enough to
  `chown` the (possibly freshly auto-created, root-owned) bind-mounted
  vault directory, then drops privileges via `setpriv` before exec'ing
  `dotnet` — the app itself never runs as root. Supports optional
  `PUID`/`PGID` environment variables (default: the image's built-in
  `app` user, uid/gid `1654`) so the container can instead write vault
  files owned by the *host* user's own uid/gid. **This was added after
  finding a real gap while independently verifying the phase**: with a
  fresh bind-mounted vault and no `PUID`/`PGID` override, files end up
  owned by uid `1654` regardless of who's running Docker, so a host
  user with a different uid can't `rm -rf`/edit/`git init` their own
  vault directory without `sudo` — directly undermining the "vault is
  just files you own" backup story Phase 8 depends on. Reproduced (`rm
  -rf ./vault` failing with "Permission denied") and fixed before
  calling this phase done, rather than leaving it for Phase 8 to find.
- Added `.dockerignore` (excludes `bin/`, `obj/`, `vault/`, `logs/`,
  `.git/`, docs, etc. from the build context).
- `README.md`: new "Self-hosting with Docker" section — installing
  Docker via Docker Desktop's WSL2 integration or plain Docker Engine,
  cloning onto the WSL2-native filesystem (not `/mnt/c`), `docker
  compose up -d --build`, the exact `\\wsl$\...` Windows Explorer path
  the bind-mounted vault resolves to for backups, the `PUID`/`PGID`
  ownership option, a plain-Linux-host variant, and a config-override
  subsection.
- Verified end-to-end (both by the implementing pass and independently
  re-verified afterward): fresh `docker compose up -d --build`,
  `GET /healthz` reachable from the host on the published port, a note
  written via the REST API survives both `docker compose restart` and a
  full `down`+`up` (container recreated from scratch), and a **freshly
  auto-created** bind-mounted vault directory is correctly owned and
  writable with zero manual `chmod`/`chown` steps — for both the default
  uid/gid and a custom `PUID`/`PGID`.
- No C# source changed; all 274 tests unaffected and still passing.

## Phase 6 — MCP server (2026-09-16)

- Added `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` 2.2.0
  to `DotNotes.Api`, hosted in-process at `/mcp` on the same Kestrel host
  as the REST API and static frontend — no separate process, no HTTP hop.
  Gated on `Mcp:Enabled` (default `true`): when disabled, `/mcp` is not
  mapped at all (same "route doesn't exist" shape as Sharing's endpoints
  when `Sharing:Enabled=false`).
- Added `DotNotes.Api/Mcp/DotNotesMcpTools.cs` implementing all 7 tools
  from `docs/05-MCP-SPEC.md`: `search_notes`, `get_note`, `create_note`,
  `update_note`, `get_backlinks`, `get_recent_notes`, `get_config`. Every
  tool calls straight into the same `DotNotes.Core` services the REST
  endpoints use, and every caller-supplied path goes through the same
  vault-root validation an HTTP request would
  (`InvalidNotePathException` → a structured `McpException`, never a raw
  exception reaching the client). `get_recent_notes` walks the note tree
  and reads each file's `UpdatedAt` (no new `INoteRepository` method
  added — an acceptable trade for a single-user vault's scale).
- Added `GET /api/config` (`DotNotes.Api/Endpoints/ConfigEndpoints.cs`):
  documented in `docs/04-API-SPEC.md` since Phase 0 but never actually
  implemented until now. Shares its field-sourcing logic with the MCP
  `get_config` tool via a new `DotNotes.Api/AppInfo.cs`, so the two
  surfaces can't drift apart. `autosaveDelayMs` (`1500`) is a named
  constant there with a comment pointing at `wwwroot/js/app.js`'s
  `AUTOSAVE_DEBOUNCE_MS` — the frontend still hardcodes this value
  rather than fetching it, which is fine for now but the two must be
  kept in sync by hand if either changes.
- `docs/05-MCP-SPEC.md` updated with two implementation notes the
  pre-1.0 SDK's actual API surface didn't make obvious from its own
  samples: tool errors are structured MCP errors by throwing
  `McpException` specifically (any other exception type becomes a
  generic message before reaching the client), and a tool method can
  return a plain record (not just `string`) — the SDK auto-serializes it
  to camelCase JSON.
- `README.md`: new "Connecting an AI assistant via MCP" section with the
  Claude Desktop/Code config snippet for `http://localhost:5175/mcp`.
- 22 new tests (`ConfigEndpointsTests`, `Mcp/DotNotesMcpToolsTests`) —
  274 total, up from 252, all passing. Manually verified end-to-end
  against a running instance: `initialize`, `tools/list` (all 7 tools,
  correct schemas), and `tools/call` for every tool including the
  create-duplicate and path-traversal error cases, both returning
  `isError: true` with a specific message and no stack trace.

## Phase 5 frontend — media rendering, sharing, and upload UI (2026-09-16)

This completes Phase 5 (backend was already done — see "Phase 5 — Sharing
& media" below).

- Fixed broken `_media/...` rendering in the preview pane
  (`js/markdown.js`): after `marked.parse()` runs, every rendered `src`/
  `href` starting with `_media/` is rewritten to `/media/<rest>`, since
  that folder lives outside `wwwroot/` and is only reachable through the
  backend's `GET /media/{**path}` route. One rewrite pass covers markdown
  images, plain links to non-image files under `_media/` (e.g. a linked
  PDF), and raw-HTML `<audio>`/`<video src="_media/...">` embeds alike.
- Added a "Share" button (toolbar, enabled once a note is open) and a
  share modal (`index.html`/`css/app.css`) that calls `POST
  /api/share/{path}`, shows the returned URL (with copy-to-clipboard) and
  QR code, and revokes via `DELETE /api/share/{token}`. A 404 (missing
  note or `Sharing:Enabled=false` — indistinguishable per
  docs/04-API-SPEC.md) shows one generic "couldn't share this note"
  message rather than guessing which case it was.
- Added an "Upload file" button (editor toolbar, enabled once a note is
  open) that posts to `POST /api/upload` as multipart form data and
  inserts markdown/HTML for the returned `relativePath` at the cursor.
  Embed syntax by content-type (a judgment call, documented in
  `js/app.js`'s `buildMediaMarkdown`): images → `![alt](path)`; audio →
  `<audio controls src="path"></audio>`; video → `<video controls
  src="path"></video>`; anything else (PDF, etc.) → a plain
  `[filename](path)` link.
- Fixed a CSS specificity bug found while building the share modal: a
  same-specificity `.hidden { display: none }` (Tailwind, loaded first)
  vs. `.share-modal-overlay { display: flex }` (app.css, loaded after)
  was won by source order regardless of which class was toggled last, so
  a "hidden" modal still intercepted clicks. Scoped the flex rule to
  `.share-modal-overlay:not(.hidden)` so it wins on specificity
  regardless of stylesheet load order.
- Manually verified end-to-end (upload → preview renders the image;
  share → link/QR appear and revoke works; PDF/audio/video links all
  rewrite correctly) against the running app; all 252 backend tests
  (unaffected — frontend-only change) still pass.
- Ticked "Image/audio/video/PDF embedding with inline preview" and
  "Token-protected public share links with QR codes" in
  `docs/03-FEATURE-SPEC.md` — Phase 5 is now fully done.

## Fix — missed live-index update for the first note in a new folder (2026-09-16)

- `VaultWatcherService` (`DotNotes.Core/Links/`) now scans a newly
  created directory for markdown files already on disk as soon as its
  own `Created` event arrives, instead of relying solely on
  `FileSystemWatcher`'s recursive-watch support. On Linux, that
  recursive support only registers the OS-level (inotify) watch for a
  new subdirectory *after* processing the directory's own `Created`
  event — so creating a folder and immediately saving the first note
  into it (exactly what "new note in a new folder" does through the
  API) could have that note's own `Created` event missed entirely, not
  just delayed, leaving it invisible to search/backlinks/graph until
  the next app restart. Confirmed via a standalone repro before fixing
  it, and re-verified after: all 9 previously-intermittent
  `VaultWatcherService*`/backlinks tests were failing consistently (not
  flaking) before this change and pass consistently after it, at a
  fraction of the previous wall-clock time (no more timeout-driven
  retries).
- No public contract changed — `ILinkIndex`/`ISearchIndex`/`IVaultChangeListener`
  are untouched; this is purely a watcher-internals fix.

## Phase 5 — Sharing & media (2026-09-16)

- Added `DotNotes.Core/Sharing/`: `IShareTokenStore`/`ShareTokenStore` —
  persists token -> `{ path, expiresAt }` to `.nd-shares.json` in the
  vault root (docs/06-DATA-MODEL.md), atomically (temp file + `File.Move`,
  same pattern as `FileSystemNoteRepository`). Tokens are 24 random
  bytes, URL-safe base64 (never derived from the note path). A missing
  or corrupt `.nd-shares.json` is treated as "no shares yet", never an
  error. `ResolveAsync` returns `null` — indistinguishably — for a
  missing, revoked, *or* expired token (expired entries are pruned
  lazily on resolve).
- Added `DotNotes.Core/Media/`: `IMediaStore`/`FileSystemMediaStore` —
  saves uploaded binary files under `_media/` in the vault root using a
  freshly-generated GUID-based file name (the caller's original file
  name is never trusted/used), mirroring `FileSystemNoteRepository`'s
  path-safety discipline (rejects absolute paths, drive-relative paths,
  `..`/`.` segments, null bytes, or anything resolving outside
  `_media/`). Also added `MediaContentTypes`, the shared
  content-type/extension allowlist (images, audio, video, PDF) used by
  both the upload and media-serving endpoints.
- Added `SharingEndpoints.cs` (`MapSharingEndpoints`):
  `POST /api/share/{**path}` (creates a token + QR code PNG, 404 if the
  note doesn't exist), `DELETE /api/share/{token}` (idempotent revoke),
  `GET /shared/{token}` (serves `wwwroot/shared.html` after confirming
  the token resolves), and a new `GET /api/share/{token}/content}`
  endpoint (not in the original API spec table — added and documented in
  `docs/04-API-SPEC.md` in this same change) that returns
  `{ path, content, updatedAt }` scoped to exactly the one note tied to
  that token, so the read-only shared view never needs access to the
  main (unauthenticated) `/api/notes/*` surface. All four endpoints
  return the same generic `404 { "error": "not_found" }` when
  `Sharing:Enabled` is `false`, indistinguishable from an invalid token.
- QR code generation (`QRCoder` 1.8.0, via `DotNotes.Api/Sharing/QrCodeGenerator.cs`)
  lives in `DotNotes.Api`, not `DotNotes.Core` — a presentation-layer
  concern tied to the HTTP response, not vault/domain logic. Uses
  `PngByteQRCode` specifically (not QRCoder's `System.Drawing`-based
  renderers) to avoid a `libgdiplus` runtime dependency on Linux/WSL2.
- Added `MediaEndpoints.cs` (`MapMediaEndpoints`): `POST /api/upload`
  (multipart, field name `file`; allowlist of `image/png`, `image/jpeg`
  [`.jpg`/`.jpeg`], `image/gif`, `image/webp`, `audio/mpeg` [`.mp3`],
  `audio/wav`, `audio/ogg`, `video/mp4`, `video/webm`,
  `application/pdf`; 50 MB max; declared Content-Type cross-checked
  against the original file extension as defense-in-depth) and
  `GET /media/{**path}` (top-level, not `/api`-prefixed — `_media/` lives
  outside `wwwroot/` so the static-file middleware can't reach it;
  **not** token-gated, an accepted trade-off for a personal tool —
  flagged in the phase report for reconsideration if this app is ever
  exposed beyond a trusted network).
- Configured Kestrel's `MaxRequestBodySize` and
  `FormOptions.MultipartBodyLengthLimit` (both `Program.cs`) to
  `MediaEndpoints.MaxUploadSizeBytes` (50 MB) plus 1 MB of multipart
  overhead slack; `MediaEndpoints` itself still enforces the real 50 MB
  file-size limit and returns a clean `{ error, detail }` `413` rather
  than relying on the raw ceiling alone.
- Added `wwwroot/shared.html` + `wwwroot/js/shared.js`: a minimal,
  read-only rendered-note page (no file tree, no editor, no autosave, no
  upload UI) that reads the token from `window.location.pathname` and
  renders via the existing `js/markdown.js` pipeline. `markdown.js`
  gained a `render(container, markdown, { plainWikilinks })` option that
  renders `[[wikilinks]]` as plain non-clickable text (no `<a>`, no
  exists/missing styling) so a shared-link visitor can't browse or probe
  the rest of the private vault. `shared.html` deliberately omits
  `js/wikilinks.js` and any `/api/notes/*`/`/api/graph` calls.
- **Not implemented in this pass** (left for `frontend-integrator`'s
  follow-up, per the phase brief): rewriting `_media/…` image/link paths
  to `/media/…` in the rendered preview (so `![alt](_media/photo.png)`
  actually displays instead of a broken image), a "share this note" UI
  affordance, and an "upload media" UI. The endpoints themselves were
  verified directly (`curl`), not through the rendered preview.
- 58 new `DotNotes.Core.Tests` (206 total, up from 148): `ShareTokenStore`
  (create/resolve/revoke, expiry, persistence round-trip, corrupt/missing
  file handling, token shape/uniqueness), `FileSystemMediaStore` (save +
  safe-name generation, path-traversal rejection, unsafe-extension
  rejection), and `MediaContentTypes` (allowlist/extension-matching
  logic).
- 20 new `DotNotes.Api.Tests` (46 total, up from 26): full share
  lifecycle (create -> token-scoped content -> rendered page -> revoke
  -> both now 404), upload happy path + unsupported content-type +
  content-type/extension mismatch + oversized file, media-serving happy
  path + traversal rejection, and every sharing endpoint 404ing when
  `Sharing:Enabled` is `false`.
- Manually verified end-to-end against a scratch vault: uploaded a real
  PNG via `POST /api/upload`, referenced it from a note, confirmed
  `GET /media/{path}` streamed it back with `Content-Type: image/png`,
  shared the note (confirmed the QR PNG's base64 decodes to valid PNG
  magic bytes), fetched both `GET /shared/{token}` (HTML) and
  `GET /api/share/{token}/content` (JSON), revoked the token, and
  confirmed both now 404. Scratch vault removed afterward.

## Phase 4 (frontend) — Search box (2026-09-16)

- Added a debounced search box to `index.html`'s header (between the
  current-note label and the Graph view link), backed by
  `GET /api/search`. Results render as a dropdown (title, snippet,
  path); clicking a result reuses the same note-loading path as the
  file tree and wikilink navigation. Dismisses on Escape or an
  outside click; stale responses are guarded against with a request
  sequence counter.
- Note: `wwwroot/lib/tailwind/output.css` is a purged precompiled
  build (no Tailwind CLI/config vendored in the repo), so it only
  contains utility classes already in use when it was generated. The
  search dropdown's positioning/shadow/divider styles were hand-written
  in `app.css` rather than assumed-available Tailwind utilities — this
  will recur for any future UI needing new utility classes not already
  compiled in.

## Phase 4 (core + API) — Full-text search (2026-09-16)

- Added `DotNotes.Core/Search/`: `ISearchIndex`/`InMemorySearchIndex` (an
  in-memory inverted index over both note bodies and note titles/
  filenames), `Tokenizer` (whitespace/punctuation split, lowercase, a
  short built-in English stopword list), `SnippetGenerator` (a short,
  whole-note-body-collapsed excerpt around the first matched query term,
  with leading/trailing `…` when truncated), and `SearchResult`
  (`{ path, title, snippet, score }`).
- Scoring: for each distinct query token, `+1` per body occurrence
  (term frequency), plus `+10` if the token appears in the note's title
  (bare filename, no `.md`) - large enough that a title match always
  outranks a realistic body-only match for the same term without a full
  tf-idf model. Notes matching no query token at all are excluded from
  results entirely, not scored `0`.
- **Shared vault-watcher refactor** (was Phase 3's sole responsibility of
  `ILinkIndex`): added `DotNotes.Core/Vault/IVaultChangeListener.cs`
  (`NoteChanged(path, content)` / `NoteDeleted(path)`), which
  `ILinkIndex` and `ISearchIndex` both now extend instead of separately
  declaring those two members - so neither interface's own member list
  changed shape, only which base interface declares them.
  `VaultWatcherService` now depends on `IEnumerable<IVaultChangeListener>`
  instead of `ILinkIndex` directly, and fans every settled file-watcher
  event out to all of them. Its initial full-vault startup scan no longer
  calls each index's own `RebuildAsync` (which would each independently
  walk the tree and re-read every file - double disk I/O); instead it
  does one tree walk, reads each file's content once, and hands that
  content to every listener via `NoteChanged` - exactly equivalent to a
  rebuild since every listener starts empty at process startup.
  `RebuildAsync` remains on both `ILinkIndex` and `ISearchIndex` for
  direct/isolated use (unit tests, or a future manual "rebuild index"
  recovery action).
- Added `GET /api/search?q={query}&limit={n}` (`SearchEndpoints.cs`,
  `MapSearchEndpoints`) -> `[{ path, title, snippet, score }]`. Judgment
  calls (docs/04-API-SPEC.md didn't specify): a missing/blank `q` returns
  `200` with an empty array rather than `400`; `limit` defaults to `20`
  when omitted/non-positive and is clamped to a max of `100`.
- Registered `ISearchIndex -> InMemorySearchIndex` as a singleton in
  `Program.cs`, alongside `ILinkIndex`; both are additionally registered
  as `IVaultChangeListener` (resolving to the same singleton instances,
  not separate ones) so `VaultWatcherService` fans out to both from one
  shared watcher pass.
- 45 new `DotNotes.Core.Tests` (148 total, up from 103): tokenization
  (punctuation/whitespace splitting, lowercasing, stopword removal,
  duplicates/order preserved), snippet generation (short excerpt, whole
  word boundaries, case-insensitive, ellipsis truncation, fallback when
  no matched token occurs in the body), and `InMemorySearchIndex`
  correctness (rebuild, incremental update-on-change/remove-on-delete,
  an explicit title-match-outranks-body-only-match ranking test, query/
  limit edge cases). Added 5 new watcher fan-out tests confirming a
  single `VaultWatcherService` keeps *both* an `InMemoryLinkIndex` and an
  `InMemorySearchIndex` live (initial scan, live create/edit/delete) -
  Phase 3's existing `InMemoryLinkIndexTests`/`VaultWatcherServiceTests`
  needed only a constructor-argument update (`ILinkIndex` ->
  `IVaultChangeListener[]`) and otherwise still pass unchanged, confirming
  no regression to `ILinkIndex`'s own behavior.
- 7 new `DotNotes.Api.Tests` (26 total, up from 19) covering
  `GET /api/search` end-to-end: empty vault, missing query, ranking,
  `limit`, and live edit/delete without an app restart.
- Frontend search box with debounced queries is a separate
  `frontend-integrator` follow-up pass, not included here - the
  `docs/03-FEATURE-SPEC.md` "Full-text search across the vault" box is
  left unticked pending that pass.

## Phase 3 (core) — Wikilinks, backlinks, graph (2026-09-16)

- Added `DotNotes.Core/Links/`: `WikiLinkParser` (recognizes
  `[[path]]` / `[[path|Display Text]]`), `WikiLinkResolver` (resolves a
  raw link target against known note paths - exact full-path match,
  then unique bare-title match, then a documented deterministic
  tie-break for an ambiguous shared title, then "unresolved/missing" if
  nothing matches at all - see that type's XML doc comments for the
  full rule), `ILinkIndex`/`InMemoryLinkIndex` (the in-memory
  `targetPath -> sourcePaths` backlink index plus `GetGraph()`),
  `LinkGraph`/`LinkGraphNode`/`LinkGraphEdge` (nodes flag
  `exists: false` for unresolved link targets), `FileChangeDebouncer`
  (generic per-path debounce/coalesce helper, framework- and
  filesystem-independent), and `VaultWatcherService` (a
  `BackgroundService` that performs the initial full-vault scan at
  startup, then keeps the index live via a single `FileSystemWatcher`
  on the vault root - re-checking "does this path exist right now"
  once a burst of events settles, so a rename or a delete+recreate save
  collapses into one reconciliation rather than several).
- Renames are treated as delete (old path) + create (new path) per
  docs/06-DATA-MODEL.md - no automatic link rewriting in other notes.
  A deleted note stays a resolvable, `exists: false` backlink target as
  long as something still links to it; once nothing does, it's pruned
  from the index entirely.
- Added `Microsoft.Extensions.Hosting.Abstractions` and
  `Microsoft.Extensions.Logging.Abstractions` package references to
  `DotNotes.Core.csproj` (both framework-agnostic, no ASP.NET Core
  dependency) so `VaultWatcherService` can live in `DotNotes.Core`.
- 103 unit tests passing in `DotNotes.Core.Tests` (up from 40), covering
  parser edge cases (aliases, nested folders, case-insensitivity,
  self-links, duplicates), the resolver's ambiguous-title rule, the
  index's incremental update logic on change/delete/rename (driven
  directly, no watcher needed), the debounce helper in isolation, and
  an end-to-end pass driving a real `FileSystemWatcher` against a real
  temp vault.

## Phase 3 (API) — Backlinks & graph endpoints (2026-09-16)

- Extended `GET /api/notes/{**path}` (`NotesEndpoints.cs`) so
  `?includeBacklinks=true` populates `backlinks: [{ path, title }]`
  (title = filename without `.md`); omitted, the field stays `null`.
- Added `GET /api/graph` (`GraphEndpoints.cs`, `MapGraphEndpoints`) ->
  `{ nodes: [{ id, label, exists }], edges: [{ source, target }] }`.
  `exists` is an additive field beyond docs/04-API-SPEC.md's original
  example, required by docs/06-DATA-MODEL.md's graph model for
  unresolved link targets - see that doc's updated Links & graph
  section.
- Registered `ILinkIndex -> InMemoryLinkIndex` as a singleton and
  `VaultWatcherService` as a hosted service in `Program.cs`; the hosted
  service's `StartAsync` performs the initial `RebuildAsync` scan
  before the host reports "started", so the very first request never
  sees a transiently-empty index.
- Frontend backlinks panel, graph view page, and clickable-wikilink
  navigation are a separate `frontend-integrator` follow-up pass in
  this same phase - not included here.

## Phase 3 (frontend) — Backlinks panel, clickable wikilinks, graph view (2026-09-16)

- Added `wwwroot/js/wikilinks.js`: a small client-side mirror of
  `WikiLinkResolver`'s resolution rule (exact path match, then unique
  bare-title match, then a shallowest/alphabetical tie-break, else
  unresolved), built from a cached `GET /api/graph` response
  (`WikiLinks.refresh()`). Not perfectly live (no push channel) — "good
  enough to navigate correctly in the common case," per the phase brief.
- `wwwroot/js/markdown.js`: added a `marked` inline extension recognizing
  `[[path]]` / `[[path|Display Text]]` and rendering each as
  `<a class="wikilink">`, styled with a dashed amber underline
  (`.wikilink-missing`) when `WikiLinks.resolve()` says the target
  doesn't exist yet.
- `wwwroot/js/app.js`: clicking a rendered wikilink navigates to the
  resolved note, or — if unresolved — confirms and creates an empty note
  at a documented default location: the raw link's own folder if it
  named one (`[[projects/idea]]`), otherwise *alongside the note
  containing the link* (not the vault root) — see
  `defaultCreatePathFor`'s doc comment for the reasoning. Extracted
  `createNoteAtPath` so the "+ New note" button and the wikilink
  click-to-create flow share one code path. `selectFile` now always
  requests `?includeBacklinks=true` and renders the result as clickable
  pills in a new backlinks footer bar (empty state: "No backlinks yet.");
  a manual "Refresh" button re-fetches it (see note below on why this
  isn't automatic). `index.html` also now honours a `?note=<path>` query
  param on load, so other pages can link straight into a note.
- Added `wwwroot/graph.html` + `wwwroot/js/graph.js`: a dedicated graph
  view page (separate static page, not a tab, to avoid reworking
  `index.html`'s fixed split-pane layout) rendering `GET /api/graph` via
  a hand-rolled canvas force-directed layout (all-pairs repulsion +
  per-edge spring + light centering force + damped Euler integration,
  ~200 lines, no vendored graph library — see that file's header comment
  for why hand-rolling won out over vendoring something like d3 here).
  Nodes are draggable; click a node to navigate to it in the editor
  (`index.html?note=...`), or, if `exists: false`, confirm-and-create
  first, consistent with the preview's wikilink click-to-create flow.
- Regenerated `wwwroot/lib/tailwind/output.css` (still Tailwind 3.4.x
  standalone CLI, bumped 3.4.17 -> 3.4.19 — no functional changes
  expected, just picking up the latest 3.4 patch) by re-running the
  Phase 2 build with `graph.html` added to the content globs, so the new
  markup's utility classes (`cursor-grab`, `pointer-events-none`,
  `inset-0`, `relative`, etc.) are actually present in the precompiled
  file rather than silently no-op'ing.
- Backlinks panel and graph view are refreshed on note navigation / page
  load, plus an explicit "Refresh" button in both, rather than polling or
  pushing updates live across tabs — acceptable for a single-user,
  personal tool per the Phase 3 exit-criteria discussion; verified by
  editing/deleting a linking note in one browser context and confirming
  the other picks up the change after re-navigating or clicking Refresh,
  without a full page reload.

## Phase 2 — Frontend shell & markdown rendering (2026-09-16)

- Added the static frontend under `DotNotes.Api/wwwroot/`: `index.html`,
  `css/app.css`, and `js/{api,markdown,tree,app}.js` — plain HTML/CSS/JS,
  no build step required to run.
- Vendored `marked` 12.0.2, `highlight.js` 11.9.0, `mermaid` 10.9.1, and
  `MathJax` 3.2.2 (SVG output bundle, to avoid any runtime web-font CDN
  requests) under `wwwroot/lib/`, plus a Tailwind CSS 3.4.17 output
  precompiled once via the standalone CLI (`wwwroot/lib/tailwind/output.css`)
  — no CDN requests at runtime, no npm/webpack/vite project in the repo.
- Split-pane editor: textarea + live-rendering preview (marked pipeline
  with a custom code renderer routing fenced ` ```mermaid ` blocks to
  Mermaid and everything else to highlight.js; MathJax typesets
  `$...$`/`$$...$$` afterward).
- Autosave: 1.5s debounce on typed edits, `PUT`s to `/api/notes/{path}`,
  with a small saved/saving status indicator; checkbox clicks in the
  rendered preview flip the underlying `- [ ]`/`- [x]` in the raw
  markdown and save immediately (bypassing the typing debounce).
- File tree sidebar from `GET /api/notes`, with expand/collapse and a
  minimal "new note" flow (prompts for a vault-relative path).
- Added `app.UseDefaultFiles()` ahead of `app.UseStaticFiles()` in
  `Program.cs` so `GET /` resolves to `index.html`.
- Wikilinks, backlinks panel, search, and graph view are not part of
  this phase — see Phase 3/4.

## Phase 1 (API) — Notes REST endpoints (2026-09-16)

- Added `src/DotNotes.Api/Endpoints/NotesEndpoints.cs`
  (`MapNotesEndpoints`), implementing the Notes section of
  `docs/04-API-SPEC.md` as thin adapters over `INoteRepository`:
  `GET /api/notes` (tree), `GET/PUT/DELETE /api/notes/{**path}`.
- Errors follow the `{ error, detail? }` shape: 400 for
  `InvalidNotePathException` (path traversal / invalid paths), 404 for
  a missing note on `GET`. `DELETE` is idempotent — 204 whether or not
  the note existed. `PUT` returns 200 for both create and update (the
  spec doesn't distinguish).
- `Program.cs` now exposes a `public partial class Program` marker so
  `WebApplicationFactory<Program>` can see it from the separate test
  assembly.
- Added `tests/DotNotes.Api.Tests` (`WebApplicationFactory`-based
  integration tests, 16 passing), each run against an isolated scratch
  vault via a `Vault__RootPath` environment-variable override (config
  is read before `WebApplicationBuilder.Build()`, so `ConfigureAppConfiguration`
  hooks are too late — the environment variable is what's honored).

## Phase 1 (core) — Vault & note CRUD service layer (2026-09-16)

- Added `INoteRepository` in `DotNotes.Core/Notes/` — the framework-free
  contract for note CRUD + tree listing that REST endpoints (and later
  MCP tools) build against. Models: `NoteTreeEntry`, `NoteEntryType`,
  `NoteContent`, `NoteWriteResult`.
- Added `FileSystemNoteRepository : INoteRepository` — disk-backed
  implementation. Raw UTF-8 markdown, no front-matter parsing. Every
  caller-supplied path is resolved via `Path.GetFullPath` and validated
  against the vault root before any file I/O; unsafe paths (absolute,
  drive-relative, `..`/`.` segments, null bytes, wrong extension, or
  anything that resolves outside the vault root) throw
  `InvalidNotePathException`. Writes are atomic (temp file +
  `File.Move` with overwrite). Deleting a note never removes now-empty
  parent folders. Tree listing excludes dotfiles/dot-directories and
  the reserved `_media/` folder, and only surfaces `.md` files.
- Registered `INoteRepository -> FileSystemNoteRepository` as a
  singleton in `DotNotes.Api/Program.cs`. Moved vault-root resolution
  (content-root-relative path handling + directory creation) to run
  *before* `WebApplicationBuilder.Build()`, then overwrote the bound
  `VaultOptions.RootPath` with the resolved absolute path via
  `Configure<VaultOptions>`, so every `IOptions<VaultOptions>` consumer
  — including `DotNotes.Core` services — always sees an absolute,
  already-existing path.
- Added `Microsoft.Extensions.Options` package reference to
  `DotNotes.Core.csproj` (framework-agnostic, no ASP.NET Core
  dependency) so Core services can consume `IOptions<T>`.
- 35 unit tests passing in `DotNotes.Core.Tests` (up from 5), covering
  CRUD happy paths, create-vs-update semantics, empty-folder
  preservation on delete, tree listing/exclusions, and path-traversal
  rejection.
- REST endpoints (`GET/PUT/DELETE /api/notes/{**path}`) on top of this
  interface are a separate, `api-developer` pass within the same phase
  — not included here.

## Phase 0 — Solution scaffolding (2026-09-16)

- Created `DotNotes.sln` with three projects: `src/DotNotes.Api`
  (ASP.NET Core 10 minimal API host + static file serving),
  `src/DotNotes.Core` (framework-free domain library), and
  `tests/DotNotes.Core.Tests` (xUnit).
- All projects target `net10.0` with `Nullable` and `ImplicitUsings`
  enabled.
- Added strongly-typed configuration in `DotNotes.Core.Config`:
  `VaultOptions`, `ServerOptions`, `SharingOptions`, `McpOptions`,
  bound from `appsettings.json` sections `Vault`, `Server`, `Sharing`,
  `Mcp` via `IOptions<T>`, overridable with the standard ASP.NET Core
  double-underscore environment variable convention.
- Added `VaultPathValidator` (`DotNotes.Core.Config`) — resolves and
  creates the vault root directory at startup; framework-free and unit
  tested directly (no web host required).
- Added `GET /healthz` via the built-in ASP.NET Core health checks
  middleware.
- Added structured logging via Serilog (`Serilog.AspNetCore`,
  `Serilog.Sinks.File`, `Serilog.Settings.Configuration`): console +
  rolling daily file sink under `logs/`, configured from the `Serilog`
  section of `appsettings.json`.
- Added a generic exception-handling middleware (`AddProblemDetails` +
  `UseExceptionHandler`) as a cross-cutting concern; no feature-specific
  error handling yet.
- No note CRUD, search, wikilinks, sharing, or MCP code yet — those are
  explicitly out of scope until later phases.
