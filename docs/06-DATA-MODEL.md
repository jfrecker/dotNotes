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

## Share tokens

- `.nd-shares.json`: `{ "<token>": { "path": "...", "expiresAt": "..."|null } }`.
- Tokens are opaque random strings (e.g. 24 bytes, URL-safe base64) —
  never derived from the note path or any predictable value.
