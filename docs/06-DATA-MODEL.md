# 06 — Data Model

## Vault layout

```
<VaultRoot>/
  daily/
    2026-09-16.md
  projects/
    idea.md
  _media/
    photo.png
  .nd-shares.json        # share-token store (generated, not user-edited)
```

- A note's identity **is** its vault-relative path (e.g. `projects/idea.md`).
  There is no separate ID/GUID — this keeps the vault fully portable and
  editable by hand or by any other tool.
- Folders are just directories; an empty folder is valid and preserved
  (don't auto-delete empty parent directories on note deletion — this
  matches the original app's behaviour of not surprising the user by
  silently removing folder structure).
- A note path must end in `.md` (case-insensitive); `INoteRepository`
  rejects any path that doesn't, via `InvalidNotePathException`. This
  keeps the repository scoped to notes only — binary assets live under
  `_media/` and are a separate concern (Phase 5 upload endpoint), not
  something `INoteRepository` reads/writes.
- `GET /api/notes` returns the vault root's direct children as the
  top-level list (files and folders), not a single wrapper node
  representing the root itself.
- `_media/` holds uploaded binary assets referenced from notes via
  relative markdown image/link syntax.
- `.nd-shares.json` is the only piece of generated state that isn't a
  note; treat it as a cache that could be regenerated as empty (existing
  share links would simply stop working, which is an acceptable
  trade-off for a personal tool).

## Folder & note move/rename (added post-launch, per user request)

- A folder can be created explicitly (`mkdir -p` semantics: also creates
  missing intermediate folders; idempotent if it already exists) via
  `INoteRepository.CreateFolderAsync`. Folder paths are validated the
  same way note paths are (no escaping the vault root, no `.`/`..`
  segments, no null bytes) minus the `.md`-extension requirement.
- A note or a folder can be moved/renamed via `INoteRepository.MoveAsync`/
  `MoveFolderAsync`. Both **refuse to overwrite an existing destination**
  (fail loudly rather than silently clobbering another note/folder — the
  same "never silently lose data" bar as everything else in this
  codebase) and auto-create missing destination parent folders, mirroring
  `SaveAsync`'s existing convention. A folder cannot be moved into itself
  or one of its own descendants (rejected as an invalid destination).
  Moving the last note out of a folder does **not** delete the
  now-emptied folder, consistent with `DeleteAsync`'s existing behavior.
- **Incoming `[[wikilinks]]` are rewritten on move/rename (Phase 10).**
  This reverses the original Phase 3 / Phase 9 "no automatic rewriting"
  decision: once rename and drag-and-drop made reorganizing cheap,
  silently orphaning backlinks on every move became the dominant way to
  corrupt a vault's link graph. Rules:
  - Every wikilink occurrence, in *any* note (including the moved note
    itself and notes inside a moved folder), that resolved to a moved
    note's old path *before* the move — per the existing
    `WikiLinkResolver` rules, against the pre-move set of known paths —
    is considered for rewriting.
  - **Minimal**: an occurrence that still resolves to the same note
    after the move (e.g. a bare `[[idea]]` title link when `idea.md` is
    moved to a different folder but keeps its unique title) is left
    byte-for-byte untouched. Only occurrences that would otherwise break
    are rewritten.
  - **Style-preserving**: a rewritten bare-title link stays a bare
    title if the new title resolves uniquely to the moved note;
    otherwise (and for any link that was already path-style) it's
    rewritten to the full new vault-relative path, without `.md`. A pipe
    alias (`[[old|Display Text]]`) keeps its display text verbatim —
    only the target portion changes.
  - **Code is not rewritten**: occurrences inside fenced code blocks
    (```` ``` ```` / `~~~`) or inline code spans are left alone —
    changing a code sample is never the user's intent. (Known,
    pre-existing inconsistency, deliberately not widened in this phase:
    the backlink index itself still counts `[[x]]` inside code as a link,
    so such an occurrence shows up as a stale backlink after a move.)
  - Nothing outside the link target spans of a rewritten note changes;
    rewrites go through `INoteRepository.SaveAsync` (atomic).
  - Best-effort per source note: if rewriting one note fails, the move
    itself is not rolled back, the failure is logged, and that note is
    simply omitted from the returned list of rewritten notes.
- **Rename and move are the same operation.** A rename is a move within
  the same folder; there is no separate rename code path at any layer.
- **Names**: every path segment of a created/moved note or folder is
  rejected if it is empty, has leading/trailing whitespace, contains a
  control character or a character invalid for a file name on the host
  OS (`Path.GetInvalidFileNameChars()`), or contains `[`, `]` or `|`
  (a note named that way can't be written as a wikilink, and a rewrite
  would produce broken link syntax). The frontend trims whitespace
  before sending; the server rejects rather than silently trimming, so
  a trimmed name can never collide with something unexpectedly.
  Characters valid on the host are otherwise allowed (e.g. `#`, `?`, `&`
  on Linux, which Phase 8 explicitly verified). These rules apply only
  to **new** names (create, or a move's destination) — never to a path
  that already exists, so a pre-existing note with a now-disallowed name
  can still be opened, autosaved, deleted, or renamed to a valid name.
- **One orchestration point, index never stale.** Move + link rewrite +
  index update lives in a single `DotNotes.Core` service used by *both*
  the REST endpoints and the MCP tools, so the two surfaces can't
  diverge. When it returns, the link and search indexes already reflect
  the moved paths *and* the rewritten content — no async watcher window.
  This matters for folder moves in particular: `VaultWatcherService`'s
  `Renamed` handler only ever sees one event for a moved *directory*,
  never one per nested note (a pre-existing limitation — see that type's
  comments), so the service captures the full old→new path mapping
  *before* moving and updates the indexes itself. The watcher still
  fires its own events afterward; index updates are idempotent, so
  that's redundant work, not a correctness problem.
- The move operations report which other notes had links rewritten
  (`rewrittenNotes`), so a client that has one of those notes open can
  reload it instead of letting a stale editor buffer autosave over the
  rewrite.

## Wikilink syntax & resolution

- Syntax: `[[note name]]` or `[[folder/note name]]`. Match case-
  insensitively against existing note titles/paths; if no match exists,
  clicking the link creates a new note at a sensible default location
  (vault root, or the same folder as the note containing the link —
  pick one and document it in code comments).
- A link's *label* is its filename without the `.md` extension unless a
  pipe alias is used: `[[actual-path|Display Text]]`.
- Store links as `(sourcePath, targetPath)` edges; unresolved links
  (pointing at a note that doesn't exist yet) are still tracked so the
  graph view can optionally show them as "missing" nodes.

## Backlink index

- In-memory `Dictionary<string targetPath, List<string> sourcePaths>`,
  built by parsing every note's wikilinks once at startup.
- Kept live by a single `FileSystemWatcher` on the vault root:
  - **Changed**: re-parse that one file, diff its old vs. new outgoing
    links, update the index incrementally.
  - **Deleted**: remove the file's outgoing links from the index; leave
    it resolvable as a "missing" backlink target if other notes still
    link to it.
  - **Renamed** (a raw filesystem rename the watcher observes, e.g. a
    file moved outside dotNotes): treat as delete + create, with **no**
    link rewriting — the watcher can't tell a user's intentional rename
    in another tool from a delete-then-unrelated-create. Link rewriting
    only happens for moves made *through* dotNotes (REST/MCP/UI), per
    "Folder & note move/rename" above.

## Graph model

- Nodes: one per known note path (including unresolved link targets,
  flagged as `exists: false`).
- Edges: one per `(sourcePath, targetPath)` pair from the backlink
  index, deduplicated.
- Serialized as-is for `GET /api/graph`; layout (force-directed
  positioning) happens client-side, not on the server.

## Search index

- MVP: token → notePath inverted index, implemented as two dictionaries
  (a `Dictionary<token, Dictionary<notePath, termFrequency>>` for note
  bodies, and a separate `Dictionary<token, HashSet<notePath>>` for note
  titles/filenames) rather than a plain `HashSet<notePath>` per token, so
  the per-token term frequency this section already requires for scoring
  is available without a second pass over each note. Tokenize on
  whitespace/punctuation, lowercase, strip a small stopword list.
  Score by term frequency plus a small title-match boost (implemented as
  `InMemorySearchIndex`: `+1` per body occurrence, `+10` if the token
  appears in the title, per query token - see that type's remarks).
- Same file-watcher lifecycle as the backlink index: re-tokenize a note
  on change, remove its tokens on delete. As of Phase 4, this lifecycle
  is shared with the backlink index via a small `IVaultChangeListener`
  interface (`NoteChanged`/`NoteDeleted`) that both `ILinkIndex` and
  `ISearchIndex` extend, so `VaultWatcherService` fans one watcher pass
  out to both instead of each index owning its own `FileSystemWatcher` or
  independently re-scanning the vault at startup.
- Interface boundary (`ISearchIndex`) must not leak the token data
  structure to callers, so swapping in `Lucene.NET` later only touches
  the implementation, not `SearchEndpoints.cs` or the MCP tool.

## Tasks

Full contract: docs/features/tasks-kanban/PLAN.md §2/§3. Summary:

- **A task is a note**, recognised by frontmatter, not location: the file
  starts (optionally after a BOM, CRLF-tolerant) with a `---` line, a
  closing `---` line, the block parses as a YAML mapping, and it has a
  non-empty scalar `id` **and** a `status` key present (its value may be
  empty/`null` — v0.2.1 relaxed this from requiring a non-empty `status`,
  to match Backlog.md's own format; an empty status means "Backlog").
- **Frontmatter schema** (Backlog.md's field names/order): `id, title,
  status, assignee (list), reporter, created_date, updated_date, labels
  (list), milestone, dependencies (list), priority, ordinal`. Unknown keys
  are preserved verbatim (captured as their original YAML source text) and
  re-emitted after the known keys, in original order. Dates are
  `'yyyy-MM-dd HH:mm'` UTC, single-quoted; `ordinal` is a plain number
  (integer text when whole). `DotNotes.Core.Tasks.TaskMarkdown` parses with
  YamlDotNet but always *writes* frontmatter with its own emitter so the
  layout matches Backlog.md and stays diff-minimal; the body is edited by
  character span (never rebuilt), so unrecognised content round-trips
  byte-for-byte.
- **Tolerant reading, faithful writing.** Each unknown key's raw text is
  sliced from its key's line to the next top-level entry, so block/flow
  mappings, nested sequences, block/multi-line-quoted scalars and interleaved
  comments survive every rewrite; line endings are normalised to `\n`. A
  scalar where a list is expected (`labels: bug`, `assignee: '@me'`) is a
  one-item list (never comma-split); a null/empty list key is an empty list.
  A known key whose value can't be represented (`created_date: yesterday`,
  non-numeric `ordinal`, a mapping where a list belongs) is preserved verbatim
  under its original key and re-emitted in its normal slot until a real edit
  supplies a typed value.
- **Single-line values.** The task service collapses line-break/tab/control
  runs in `title`, `labels`, `assignee`, `milestone` and `dependencies` to one
  space (trimmed; empty list items dropped). As a second line of defence the
  emitter double-quotes (YAML `\n`/`\t`/`\uXXXX` escapes) any scalar with a
  control character or `:` + non-space whitespace, and every write is
  serialised and re-parsed *before* touching disk - a value that wouldn't
  round-trip raises an error instead of leaving an unparseable file.
- **Duplicate ids.** If two files carry the same task id (e.g. a copied
  file), the index resolves the id to the lowest path (ordinal,
  case-insensitive) and falls back to the other file when one is deleted or
  changes id; both files stay visible as separate tasks in lists/boards.
- **Body sections** use Backlog.md's sentinel-comment markers: `##
  Description` (`<!-- SECTION:DESCRIPTION:BEGIN/END -->`), `## Acceptance
  Criteria` (`<!-- AC:BEGIN/END -->`, items `- [ ] #n text`, renumbered on
  every write), `## Implementation Plan`/`## Implementation Notes`/`##
  Final Summary` (their own `SECTION:*` markers). `## Definition of Done`
  and `## Comments` are recognised only enough to preserve their position
  and content verbatim - v1 never edits them. A task with no Description
  markers uses the free body text outside any other recognised section as
  its description (so a converted note keeps its existing text); setting a
  description on such a note wraps that text in markers, replacing it.
- **Filename:** `<ID> - <Title>.md` in the task's current folder (title
  sanitised per dotNotes' existing name rules, capped at 80 characters).
  Changing a task's title through the task API renames the file via
  `IVaultReorganizationService` (wikilinks rewritten) - but only when the
  current file name is still id-derived; a manually-renamed file's title
  is updated in frontmatter only, per usual "one code path" rules.
- **IDs:** `<Tasks:IdPrefix>-<N>` (default prefix `TASK`), `N` = 1 + the
  max numeric top-level id over *all* tasks including completed
  (case-insensitive prefix match; dotted subtask ids like `TASK-5.1` are
  ignored for the max). IDs are never reused.
- **Ordering:** `ordinal` (double) ascending, missing last, then
  `created_date` ascending, then numeric id. A new task's ordinal is the
  max in its column + 1000 (or 1000 if the column is empty). Moving to
  index *i* uses the midpoint of its new neighbours (top = next/2, bottom
  = prev+1000); if a neighbour lacks an ordinal or the gap is smaller than
  `1e-6`, the whole column is renumbered 1000, 2000, ... and only the
  files whose ordinal actually changed are written. Ordering within the
  Backlog column spans every raw status that maps there (empty,
  unrecognised, or the Backlog status itself); column membership, not raw
  status equality, drives the ordinal math.
- **Complete** (v0.2.1, replaces Archive): `CompleteAsync` sets a task's
  status to `Tasks:CompletedStatus` (default `Done`) and moves its note
  to `<directory of its current path>/Completed/<file name>` via
  `IVaultReorganizationService` (wikilinks rewritten) — e.g.
  `Task/ProjectX/TASK-3 - My Task.md` becomes
  `Task/ProjectX/Completed/TASK-3 - My Task.md`; a root-level task goes
  to `Completed/...`. The `Completed` folder is created if missing; a
  name collision appends a numeric suffix before `.md` (`(2)`, `(3)`, …),
  never overwriting. Already being under a `Completed` folder makes this
  a no-op beyond ensuring the status (no nested `Completed/Completed`).
  `TaskItem.Completed` (renamed from `TaskItem.Archived`) is true when
  *any* segment of the task's vault-relative path is exactly `Completed`
  (ordinal/case-sensitive comparison — `completed`/`COMPLETED` don't
  count), regardless of which top-level folder the task lives under;
  such tasks are excluded from list/board/search results unless
  explicitly included. Dependency lists are left untouched (ids are
  never reused, so they stay meaningful). Dragging a card to the column
  matching `Tasks:CompletedStatus` only changes `status` via `move_task`/
  `POST .../move` — it does not move the file; only Complete does that.
  `archive_task`/`POST .../archive` are kept as deprecated aliases
  calling the same code path.
- **Backlog column:** `Tasks:BacklogStatus` (default `Backlog`) is always
  the first effective status, prepended to `Tasks:Statuses` if a custom
  configuration omits it. A task's raw `status` maps to the Backlog
  column when it's empty, doesn't case-insensitively match any other
  effective status, or matches `Tasks:BacklogStatus` itself — there are
  no more trailing "unknown status" columns. Cards keep their original
  raw status value even while shown in Backlog. A blank `status` on
  create defaults to `Tasks:DefaultStatus` if set, else the Backlog
  status.
- **Task discovery is folder-agnostic**: `Tasks:Folder` (default `Task`,
  renamed from `tasks` in v0.2.1) is only where a *new* task is created
  by default — any note anywhere in the vault with task frontmatter is
  a task regardless of its folder, and `TaskCreateRequest.Folder` lets a
  caller (UI folder-context-menu "New Task", REST `folder`, MCP
  `create_task`'s `folder`) create straight into any folder; creating
  into a folder already inside a `Completed` folder is rejected.
- **Startup migration (v0.2.1, idempotent):** runs after the vault root
  is confirmed to exist and before the watcher's initial scan; never
  crashes startup (logs and continues on any failure). No marker file is
  written (the app keeps no state outside the vault), so the exact-name
  matches below are simply re-checked each start. Step 1 — only when
  `Tasks:Folder` is left at its default `Task`: a root-level folder
  named exactly (ordinal, case-sensitive) `task` or `tasks` (v0.2.0's
  default) is renamed/merged into `Task` using
  `IVaultReorganizationService.MoveFolderAsync` so wikilinks are
  rewritten; a rename that differs only by case (`task` → `Task`) always
  hops through a temporary unique name (two `MoveFolderAsync` calls, raw
  `Directory.Move` only as a fallback) so it works on case-insensitive
  filesystems; on a name collision inside a merge, the legacy item is
  left in place and a warning is logged rather than overwriting
  anything; non-`.md` files in a merged folder are moved directly with
  the filesystem; the legacy folder is removed only once it's empty. A
  folder named `Tasks`/`TASKS` is never matched. Step 2 — for whatever
  `Tasks:Folder` is configured (not gated on it being `Task`):
  `<Folder>/archive` (exact name, v0.2.0's archive folder) is merged
  into `<Folder>/Completed` with the same no-clobber rules.
- **Index:** `InMemoryTaskIndex : ITaskIndex : IVaultChangeListener`, fed
  by the same `VaultWatcherService`/`VaultReorganizationService` fan-out as
  the link and search indexes. A monotonically increasing `Revision` bumps
  only on a task-affecting change; pure cache, rebuildable from disk.

## Share tokens

- `.nd-shares.json`: `{ "<token>": { "path": "...", "expiresAt": "..."|null } }`.
- Tokens are opaque random strings (e.g. 24 bytes, URL-safe base64) —
  never derived from the note path or any predictable value.
