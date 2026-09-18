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
