# 03 — Feature Spec

Checklist of features to replicate, grouped by priority. Tick boxes off
during Phase 8 (or as each phase completes them).

## Must-have (MVP — the app is unusable for daily notes without these)

- [x] Create, read, update, delete markdown notes as plain files, organized in folders
- [x] File-tree sidebar reflecting the on-disk folder structure
- [x] Collapsible sidebar (persisted collapsed/expanded state), editor/split area reflows to use freed width
- [x] Split-pane editor with live markdown preview, resizable via a draggable divider (ratio persisted), reflowing/wrapping content and recomputing proportionally on window resize or sidebar collapse
- [x] Autosave (debounced) — no explicit "save" button required
- [x] Syntax-highlighted fenced code blocks
- [x] Mermaid diagram rendering
- [x] LaTeX math rendering
- [x] `[[wikilink]]` parsing, with click-to-navigate and click-to-create
- [x] Backlinks panel per note
- [x] Full-text search across the vault
- [x] Runs self-hosted via Docker on WSL2/Linux, data persists across restarts

## Should-have (real quality-of-life gaps if missing)

- [x] Graph view visualizing note-to-note links
- [x] Interactive checkboxes in rendered preview (click to toggle done/undone)
- [x] Image/audio/video/PDF embedding with inline preview
- [x] MCP server so Claude Desktop/Claude Code/Cursor can search and edit notes directly
- [x] Folder & subfolder management: create folders, move/rename notes and folders via the UI
- [x] Delete a note or a folder from the sidebar's right-click menu (deleting a folder removes everything inside it, behind a confirmation prompt)
- [x] "New Subfolder" in a folder's right-click menu: prompts for a name (folder shown as a locked prefix), rejects empty/invalid/`.`/`..`/duplicate names, creates it inside that folder, then expands the parent and highlights the new folder
- [x] Editor view-mode toggle (Edit-only / Split / Preview-only), persisted for the session
- [x] Sidebar drag-and-drop: drag notes and folders onto a folder (or the vault root) to move them, with a keyboard-accessible right-click "Move to…" fallback
- [x] Rename notes and folders (right-click "Rename", inline-editable note title) with incoming `[[wikilinks]]` rewritten so backlinks never orphan
- [x] Single "+New" menu (New Note, New Folder; template/drawing entries present but disabled)
- [x] Home / folder-browse view: breadcrumb trail, app name + tagline at the root only, "X notes, Y folders" summary, folder card grid that navigates on click, **and note cards for any notes directly in the current folder**
- [x] Sidebar folder reorder (drag a folder among its siblings for a custom manual order, coexisting with drag-onto-a-folder to move/reparent) and a name-ascending/descending sort toggle
- [x] In-app themed modal (title, subtitle, labeled input, Cancel/primary action) replacing native `prompt()`/`confirm()` for New Note, New Folder, Rename, and delete confirmations
- [x] Note editor header: inline title, previous/next-note navigation within the current folder, delete, "Edited `<date>`", Edit/Split/Preview tabs, icon row (export, print, copy link, share, fullscreen)
- [x] Markdown formatting toolbar in Edit and Split modes (bold, italic, strikethrough, heading, link, image, inline code, code block, blockquote, bullet/numbered/task list, table)

## Could-have (nice, not essential for a single-user personal build)

- [x] Token-protected public share links with QR codes
- [ ] Drawing/sketch tool saved as PNG alongside notes
- [x] Light/dark theme toggle in the top nav

## Tasks & Kanban (Phase 12)

Design and decisions: `docs/features/tasks-kanban/PLAN.md`. Ticked only
after being exercised end to end (xUnit against a real temp vault, and/or
the Playwright suite in `tests/e2e/specs/tasks-kanban.spec.js`); leftovers
are in `docs/KNOWN-ISSUES.md`.

- [x] A task is a plain `.md` note recognised by YAML frontmatter (`id` + a `status` key, value may be empty) anywhere in the vault; Backlog.md-compatible frontmatter and section markers, unknown keys preserved, byte-stable re-serialisation, CRLF/BOM tolerated; notes without task frontmatter behave exactly as before
- [x] Sidebar `TASKS` section above "Folders & Notes" with "All Tasks" and "Kanban Board" rows, each with the active-task count
- [x] Kanban board: a "Backlog" column is always first (`Tasks:BacklogStatus`, prepended even if omitted from config), followed by one column per remaining configured status (`Tasks:Statuses`, env-overridable) with count badges; tasks with an empty, unrecognised, or Backlog status all land in Backlog rather than spilling into extra trailing columns (v0.2.1)
- [x] Cards show title, id, description excerpt, assignee, labels, priority, AC progress and created date; card click opens the task modal
- [x] Drag-and-drop between columns and reordering within a column, persisted to the files (ordinal), including while a filter hides some cards; dragging into/out of Backlog only changes `status` (never moves the file)
- [x] "+ New Task" (board header, list view and per-column "+" with the column's status preset); also available from the "+ New" sidebar/home menu and a folder's right-click menu, creating directly into that folder with it shown read-only (v0.2.1); title validation
- [x] Task modal: edit title, status, priority, assignee, labels, milestone, dependencies, description, plan/notes/final summary and an interactive acceptance-criteria checklist (add/edit/tick/remove); "Open note" link; green Complete button with confirmation (v0.2.1, replaces Archive)
- [x] Board filters (text, label, assignee, priority) and an All Tasks table that is sortable (numeric-aware ID sort) and filterable (text, status, label, assignee, priority, show completed)
- [x] Live refresh: the board/list pick up API, MCP and direct-file edits without a reload (revision polling + file watcher)
- [x] Pomodoro timer at the top right of the Kanban view: adjustable focus/short/long durations and cycles-before-long-break, start/pause/reset/skip, visible countdown + phase + cycle dots, chime and/or browser notification on phase change, settings persisted, keeps running across in-app navigation and reloads (top-nav indicator while the board is hidden), optional linked task
- [x] Light and dark styling for the board, modal and Pomodoro widget
- [x] "Convert to task" in the sidebar context menu (no auto-migration of existing notes); renaming a task's title from the modal, the API or the editor's inline title renames the file and rewrites incoming wikilinks; task note preview hides the YAML and shows a task header
- [x] Task-file sidebar context menu offers "Complete task" for a task note not already in a `Completed` folder (v0.2.1)
- [x] Concurrent-edit handling: the note editor sends `expectedUpdatedAt` and offers Reload latest / Overwrite on a 409; board edits are field-level patches that merge with whatever was saved elsewhere
- [x] Complete moves a task's note into a `Completed` subfolder next to it (e.g. `Task/ProjectX/Completed/`), created if missing, never overwriting, incoming wikilinks kept resolving (the reorganization service rewrites a link only when it would otherwise break); a note under any folder segment named exactly `Completed` is hidden from list/board/search unless explicitly included (v0.2.1, replaces the single vault-wide `<Folder>/archive/`)
- [x] `Tasks:Folder` default renamed `tasks` → `Task`; an idempotent, crash-proof startup migration merges a pre-existing lowercase `task`/`tasks` folder into `Task`, and `<Tasks:Folder>/archive` into `<Tasks:Folder>/Completed` for whatever folder is configured, automatically (v0.2.1)
- [x] REST `/api/tasks*` (config, list, board, revision, get, create, patch, move, complete, convert) with 400/404/409/503 error cases; `POST .../archive` and `includeArchived` kept as deprecated aliases
- [x] MCP tools `list_tasks`, `get_task`, `create_task` (optional `folder`), `update_task` (incl. AC check/uncheck/add/remove, plan/notes), `move_task`, `complete_task`, `get_board`, `search_tasks`, `get_task_workflow` plus the `dotnotes://workflow/tasks` resource, verified over the real `/mcp` HTTP transport; `archive_task` kept as a deprecated alias; existing MCP tools unchanged
- [ ] Subtasks, milestone entities, dependency cycle/readiness checks, multi-select move, Definition-of-Done/Comments editing (fields and sections are preserved on round-trip, just not editable) — deliberately later, see the plan

## Present in the UI but deliberately not implemented

These appear in the layout copied from NoteDiscovery so the structure
matches, but are rendered **disabled** rather than wired to anything.
Building any of them is a scoped decision, not a side effect of a UI
change.

- **New from Template** (`+New` menu) — no template support exists.
- **New Drawing** (`+New` menu) — see the unticked Could-have above.
- **Favorite / star** (note editor icon row) — would need a new
  persisted favorites store; not requested as a feature.

## Explicitly descoped for this build

- **Multi-language UI (i18n).** The original app supports many locales;
  this is a single-user build for one person, so ship English only.
  Revisit only if that stops being true.
- **Multi-user accounts / permissions.** Out of scope by design — see
  `docs/02-ARCHITECTURE.md` non-functional notes.
- **Any database.** By design — see `docs/02-ARCHITECTURE.md`.
