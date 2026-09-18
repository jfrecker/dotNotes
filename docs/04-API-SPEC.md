# 04 — REST API Spec

Base path: `/api`. All request/response bodies are JSON unless noted.
`{**path}` means a catch-all route parameter representing a note's
relative path inside the vault (e.g. `projects/idea.md`), URL-encoded
as needed by the frontend.

## Notes

| Method | Path | Body | Response | Notes |
|---|---|---|---|---|
| GET | `/api/notes` | — | Tree of `{ path, name, type: "file"\|"folder", children? }` | Full vault listing |
| GET | `/api/notes/{**path}` | — | `{ path, content, updatedAt, backlinks?: [...] }` | `?includeBacklinks=true` to populate `backlinks` |
| PUT | `/api/notes/{**path}` | `{ content }` | `{ path, updatedAt }` | Creates the note (and parent folders) if it doesn't exist |
| DELETE | `/api/notes/{**path}` | — | `204 No Content` | Does not delete now-empty parent folders |
| POST | `/api/notes/{**path}/move` | `{ destinationPath }` | `{ path, updatedAt, rewrittenNotes: [path] }` | Moves/renames a note (rename = move within the same folder). `404` if the source doesn't exist, `409` if `destinationPath` already exists (never overwrites), `400` for an invalid path or name |
| POST | `/api/folders/{**path}` | — | `{ path }` | Creates a folder (and missing parent folders), `mkdir -p`-style. Idempotent — `200` even if it already exists. `409` if a *note* already exists at that exact path, `400` for an invalid path or name |
| POST | `/api/folders/{**path}/move` | `{ destinationPath }` | `{ path, rewrittenNotes: [path] }` | Moves/renames a folder and everything inside it. `404` if the source doesn't exist, `409` if `destinationPath` already exists, `400` if the destination is the source itself or a descendant of it, or for an invalid path or name |

- Both move endpoints **rewrite incoming `[[wikilinks]]`** that would
  otherwise break, and return the vault-relative paths of every note
  whose content was rewritten as `rewrittenNotes` (empty array if none;
  the moved note itself is included if it contained a self-link that
  was rewritten). Rules — minimal, style-preserving, pipe aliases kept,
  code blocks untouched — are in `docs/06-DATA-MODEL.md`'s "Folder &
  note move/rename" section.
- Both endpoints return only after the link and search indexes reflect
  the move *and* the rewritten content, so an immediately-following
  `GET /api/graph`, `?includeBacklinks=true` or `/api/search` is never
  stale.
- Name rules (`400 invalid_path`): no empty segments, no leading/trailing
  whitespace, no control characters or host-invalid file-name
  characters, no `[`, `]` or `|`. They apply only to **new** names — a
  move's `destinationPath`, `POST /api/folders/{**path}`, and `PUT
  /api/notes/{**path}` when it *creates* a note. They never apply to a
  path that already exists: an existing note with such a name can still
  be read, updated (autosave), deleted, or moved *away* to a valid name.

## Search

| Method | Path | Response |
|---|---|---|
| GET | `/api/search?q={query}&limit={n}` | `[{ path, title, snippet, score }]` |

- `q` missing or blank returns `200` with an empty array (`[]`), not a
  `400` - simpler for a live "search as you type" frontend, with no error
  state to special-case when the search box is cleared.
- `limit` defaults to `20` when omitted or not a positive integer, and is
  clamped to a max of `100` when a caller asks for more.

## Links & graph

| Method | Path | Response |
|---|---|---|
| GET | `/api/notes/{**path}?includeBacklinks=true` | `{ ..., backlinks: [{ path, title }] }` |
| GET | `/api/graph` | `{ nodes: [{ id, label, exists }], edges: [{ source, target }] }` |

- Backlinks are returned via the `includeBacklinks` query param on the
  existing note-fetch endpoint (see the Notes table above), not a
  separate `/backlinks` route — one way to fetch the same data, not two.
- `exists` (bool, on each graph node) was added in Phase 3 as an additive
  field, not shown in this doc's original example: it flags an
  unresolved/not-yet-created wikilink target per docs/06-DATA-MODEL.md's
  "Graph model" section (`false`), vs. a note that actually exists on
  disk (`true`).

## Sharing

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/api/share/{**path}` | `{ expiresInDays?: number }` | `{ token, url, qrCodePngBase64 }` — 404 if the note doesn't exist |
| DELETE | `/api/share/{token}` | — | `204 No Content` — revokes the token (idempotent) |
| GET | `/api/share/{token}/content` | — | `{ path, content, updatedAt }` — token-scoped, read-only; backs `/shared/{token}`'s rendered view and does **not** expose the main `/api/notes/*` surface, only the one note tied to this specific token |
| GET | `/shared/{token}` | — | Rendered read-only HTML page (not JSON); `[[wikilinks]]` render as plain non-clickable text |

- All four endpoints above are gated on the `Sharing:Enabled` config flag
  (see `docs/06-DATA-MODEL.md`'s "Share tokens" section): when disabled,
  every one of them returns the same generic `404 { "error": "not_found" }`
  used for a missing/expired/revoked token, so a caller can't distinguish
  "sharing is off" from "this token never existed".
- A missing, expired, or revoked token is deliberately indistinguishable:
  `DELETE`, the content endpoint, and the rendered page all return the
  same 404 shape for every one of those three cases.

## Media

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/api/upload` | multipart form file (field name `file`) | `{ relativePath }` — under `_media/` in the vault |
| GET | `/media/{**path}` | — | Streams the file back with the correct `Content-Type` |

- `{**path}` in `GET /media/{**path}` is the suffix *after* `_media/`
  (e.g. `GET /media/photo.png` serves `<vault>/_media/photo.png`, not
  `<vault>/_media/_media/photo.png`).
- `POST /api/upload` accepts images, audio, video, and PDF only:
  `image/png`, `image/jpeg` (`.jpg`/`.jpeg`), `image/gif`, `image/webp`,
  `audio/mpeg` (`.mp3`), `audio/wav`, `audio/ogg`, `video/mp4`,
  `video/webm`, `application/pdf`. Anything else is rejected with
  `415 Unsupported Media Type`. Max upload size is 50 MB (`413 Payload
  Too Large` beyond that). The uploaded file's declared Content-Type is
  cross-checked against its original file extension, when one is
  supplied, as a defense-in-depth measure (not a full magic-byte sniff).
- `GET /media/{**path}` is a top-level route (not `/api`-prefixed, same
  as `/shared/{token}`) because `_media/` lives in the vault root,
  outside `wwwroot/`, and so isn't reachable through the static-file
  middleware. It is **not** token-gated — any request for a known/guessed
  media filename can fetch it, whether or not its owning note is shared.
  This is an accepted trade-off for a personal, single-user tool (see
  Phase 5's implementation notes); revisit if this app is ever exposed
  beyond a private/trusted network.
- A note references an uploaded file as `_media/<name>.<ext>` in
  markdown (`![alt](_media/photo.png)`); the frontend rewrites this to
  `/media/<name>.<ext>` when building the rendered preview's image `src`.

## Config

| Method | Path | Response |
|---|---|---|
| GET | `/api/config` | `{ name, version, features: { sharing, mcp, graph }, autosaveDelayMs }` |

## Conventions

- Errors: `{ error: string, detail?: string }` with an appropriate 4xx/5xx status.
- Timestamps: ISO 8601 UTC.
- No endpoint returns raw filesystem paths outside the vault root; every
  path in a response is vault-relative.
