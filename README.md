# dotNotes

A self-hosted, single-user markdown note-taking and knowledge-base
app — a .NET/ASP.NET Core replica of
[NoteDiscovery](https://github.com/gamosoft/NoteDiscovery). No
database, no cloud dependency: every note is a plain `.md` file in a
vault directory on disk. Features a split-pane editor with live
preview (Mermaid diagrams, LaTeX math, syntax highlighting),
`[[wikilinks]]` with backlinks and a graph view, full-text search,
image/audio/video/PDF embedding, token-protected shareable read-only
links with QR codes, a Kanban task board (tasks are notes too) with a
Pomodoro timer, and an in-process MCP server so an AI assistant
(Claude Desktop, Claude Code, Cursor) can search and edit your notes
and tasks directly.

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

Prefer Podman? See **[Running with Podman](#running-with-podman)**
below — same `Dockerfile`, same compose file.

See **[`DEPLOYMENT.md`](DEPLOYMENT.md)** for the full deployment
guide — WSL2 vs. plain Linux, running natively without Docker (Windows
or Linux), every configuration option, updating, backup/restore, and
reverse-proxy/HTTPS guidance if you ever expose this beyond your own
LAN.

## Running with Podman

Podman runs dotNotes from the same `Dockerfile` and the same
`docker-compose.yml` as Docker — there is no separate `Containerfile`
and no Podman-specific compose file to keep in sync. What differs is
SELinux labelling on the vault bind mount and file ownership under
rootless Podman; both are one-line settings in `.env`.

### Prerequisites

- **Podman 4.4+** (`podman --version`). Quadlet, used for the systemd
  service below, needs 4.4 or newer; 5.x is what this was written
  against.
- For the compose workflow, either:
  - **`podman compose`** (Podman 4.7+), which delegates to whichever
    compose provider is installed, or
  - **`podman-compose`** (`pip install podman-compose`, or
    `dnf install podman-compose`).
- `podman-compose` needs the Podman socket for some operations:
  `systemctl --user enable --now podman.socket`.

### Quick start (compose)

```bash
git clone https://github.com/jfrecker/dotNotes.git
cd dotNotes
cp .env.example .env
```

Then edit `.env` and uncomment the two Podman lines at the bottom:

```ini
# On Fedora / RHEL / CentOS Stream (SELinux enforcing):
VAULT_MOUNT_OPTS=:Z

# Rootless Podman only:
USERNS_MODE=keep-id
```

```bash
podman compose up -d --build     # or: podman-compose up -d --build
```

Open <http://localhost:5175>.

Both settings are empty by default and are ignored by Docker, so the
`.env` file stays valid for either runtime.

### Quick start (plain `podman run`, no compose)

```bash
podman build -t localhost/dotnotes:latest .

mkdir -p ~/dotnotes/vault
podman run -d \
  --name dotnotes \
  --userns=keep-id \
  -p 5175:5175 \
  -v ~/dotnotes/vault:/data/vault:Z,U \
  -e Vault__RootPath=/data/vault \
  --restart=unless-stopped \
  localhost/dotnotes:latest
```

Drop `--userns=keep-id` for a rootful (`sudo podman`) install. The image
is tagged `localhost/dotnotes:latest` rather than bare `dotnotes` so
Podman never has to resolve a short name (see *Troubleshooting*).

### Volumes, persistence, and SELinux

The vault is a **bind mount**, not a named volume: your notes are plain
`.md` files you can browse, `git init`, and back up directly on the host
(see `docs/02-ARCHITECTURE.md`). Nothing in the image holds note data,
so rebuilding or replacing the container never touches it.

On an SELinux-enforcing host the container is denied access to a bind
mount unless the mount is labelled. The symptom is the app starting
normally but every note read and write failing, with `avc: denied` in
`journalctl`. Which flag to use:

| Flag | Meaning | Use when |
|------|---------|----------|
| `:Z` | Relabel **privately** — only this container may access the path | Almost always. dotNotes is the only thing using its vault. |
| `:z` | Relabel **shared** — any container may access the path | Only if something else (a backup sidecar, another app) mounts the same directory. |

Both are accepted and silently ignored on non-SELinux hosts (Ubuntu,
Debian, WSL2), so they are safe to leave set. Relabelling **rewrites
SELinux labels on the directory you point at**, so keep `VAULT_PATH` on
a directory dedicated to dotNotes rather than, say, your whole home
folder.

### Rootless permissions

Rootless Podman runs the container inside a user namespace. By default
your host uid maps to `root` *inside* the container, and any other uid
the container writes as maps to a **subordinate uid** on the host (from
`/etc/subuid`) — which is not you. Files in the vault then appear owned
by something like `524287:524287`, and you cannot edit or delete your
own notes without `podman unshare`.

`USERNS_MODE=keep-id` (compose) / `--userns=keep-id` (`podman run`)
fixes this by mapping your host user straight through, so notes the
container writes are owned by you. With it set:

- `PUID`/`PGID` are unnecessary and ignored — the container already runs
  as your uid, and `docker-entrypoint.sh` detects that it did not start
  as root and skips its `chown`/privilege-drop step entirely.
- The `U` suffix on a `podman run` volume (`:Z,U`) makes Podman chown
  the mounted directory's existing contents to that uid, which is what
  you want the first time you point it at notes created some other way.

For **rootful** Podman (`sudo podman`), drop `keep-id` and use
`PUID`/`PGID` exactly as under Docker — the container starts as root and
drops privileges itself.

If you already have a vault owned by the wrong uid:

```bash
podman unshare chown -R "$(id -u):$(id -g)" ~/dotnotes/vault
```

### Running as a systemd service (Quadlet)

`deploy/podman/dotnotes.container` is a ready-to-edit Quadlet unit.
Quadlet generates a real systemd service from it at daemon-reload time,
so there is no `podman generate systemd` output to regenerate whenever
the container changes.

```bash
podman build -t localhost/dotnotes:latest .

mkdir -p ~/.config/containers/systemd
cp deploy/podman/dotnotes.container ~/.config/containers/systemd/
# edit the Volume= line for your vault path, then:
systemctl --user daemon-reload
systemctl --user start dotnotes
systemctl --user status dotnotes
journalctl --user -u dotnotes -f
```

**Auto-start on boot.** A `--user` service normally starts at first
login and stops at last logout. To have it start at boot and survive
logout:

```bash
loginctl enable-linger "$USER"
```

The unit's `[Install] WantedBy=default.target` is what enables it — with
Quadlet you do not run `systemctl --user enable dotnotes` (the unit is
generated, so there is no file for `enable` to symlink).

For a system-wide install, copy the file to `/etc/containers/systemd/`,
delete its `UserNS=keep-id` line, and use `sudo systemctl` without
`--user`.

### Updating

```bash
cd dotNotes
git pull

# compose:
podman compose up -d --build

# plain podman run:
podman build -t localhost/dotnotes:latest .
podman stop dotnotes && podman rm dotnotes
# ...then re-run the `podman run` command above

# Quadlet:
podman build -t localhost/dotnotes:latest .
systemctl --user restart dotnotes
```

Your notes are untouched by any of these — they live on the host, not in
the image or the container.

### Troubleshooting

**Permission denied reading or writing notes.** Either SELinux or
rootless uid mapping:

```bash
getenforce                                  # Enforcing → you need :Z
sudo ausearch -m avc -ts recent | tail      # confirms an SELinux denial
podman unshare ls -ln ~/dotnotes/vault      # uids as the container sees them
```

Set `VAULT_MOUNT_OPTS=:Z` for the first, `USERNS_MODE=keep-id` (plus the
`podman unshare chown` above, once) for the second.

**Podman prompts you to choose a registry**, or fails with
`short-name "dotnotes" did not resolve to an alias`. Podman refuses to
guess a registry for an unqualified image name. Every image dotNotes
references is already fully qualified (`mcr.microsoft.com/dotnet/...`),
so this only happens if you tag your own build as bare `dotnotes` —
tag it `localhost/dotnotes:latest` instead.

**Port already in use, or "permission denied" binding a port.** Rootless
Podman cannot bind host ports below 1024. dotNotes uses 5175 on both
sides for exactly this reason. To move the host side, set `HOST_PORT` in
`.env` (compose) or change the left-hand side of `-p`/`PublishPort=` —
keep it above 1024 for rootless.

**`podman compose` does nothing / is not found.** You are on Podman
older than 4.7, or no compose provider is installed. Install
`podman-compose` and call it directly:
`podman-compose up -d --build`.

**The service starts then immediately stops under systemd.** Check
`journalctl --user -u dotnotes -n 50`. The usual cause is the `Volume=`
host path not existing yet — Quadlet does not create it:
`mkdir -p ~/dotnotes/vault`.

## Tasks & Kanban

A task is just a note with YAML frontmatter, in the same format as
[Backlog.md](https://github.com/MrLesk/Backlog.md), so it stays a
plain, hand-editable `.md` file you can open in the normal editor:

```markdown
---
id: TASK-12
title: Fix login redirect
status: In Progress
assignee:
  - '@me'
created_date: '2026-09-24 10:30'
labels:
  - auth
priority: high
ordinal: 2000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Users land on / instead of the page they asked for.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Redirect preserves the original path
<!-- AC:END -->
```

- **Where they live:** new tasks go in the `tasks/` folder as
  `TASK-12 - Fix login redirect.md`. Any note anywhere in the vault with
  `id` and `status` in its frontmatter is recognised as a task.
- **Sidebar → TASKS:** *Kanban Board* and *All Tasks*, with a count of
  active tasks.
- **Kanban Board:** one column per status. Drag cards between columns
  or within a column; the order is saved in each file's `ordinal`.
  Filter by text, label, assignee and priority. Click a card to edit
  every field, including an interactive acceptance-criteria checklist
  and the implementation plan and notes. *Open note* jumps to the raw
  file.
- **All Tasks:** a sortable, filterable table, with an option to show
  archived tasks.
- **Convert to task:** right-click any note in the sidebar. The note
  gets task frontmatter and is renamed `TASK-N - <title>.md`, and links
  to it are rewritten. Existing notes are never converted automatically.
- **Renaming** a task (task panel or the editor's title field) renames
  its file and rewrites incoming `[[wikilinks]]`. Editing `title:` by
  hand in the YAML changes only the displayed title.
- **Archive** moves the file to `tasks/archive/`. Task IDs are never
  reused.
- **Concurrent edits:** if a task (or any note) changes on disk while
  it is open in the editor, e.g. from the board or an AI assistant, the
  next autosave is refused and you choose *Reload latest* or keep your
  version.
- **Configuration** (`appsettings.json` → `Tasks`, or env vars):

  | Setting | Default | Env var |
  |---|---|---|
  | `Folder` | `tasks` | `Tasks__Folder` |
  | `IdPrefix` | `TASK` | `Tasks__IdPrefix` |
  | `Statuses` | `To Do`, `In Progress`, `Done` | `Tasks__Statuses__0`, `Tasks__Statuses__1`, … |
  | `DefaultStatus` | first status | `Tasks__DefaultStatus` |
  | `Priorities` | `high`, `medium`, `low` | `Tasks__Priorities__0`, … |

  `Statuses`/`Priorities` are deliberately absent from `appsettings.json`
  (their defaults live in code), so setting them via env vars or an
  override file defines the *whole* list, e.g.
  `Tasks__Statuses__0=Backlog`, `Tasks__Statuses__1=Doing`,
  `Tasks__Statuses__2=Review`, `Tasks__Statuses__3=Done`. The board's
  columns follow that order.

### Pomodoro timer

The Kanban board has a Pomodoro timer at the top right. You can set the
focus, short-break and long-break lengths and the number of cycles
before a long break. It has start, pause, reset and skip controls, and
can play a chime and/or show a browser notification when a phase ends.
It can optionally be linked to a task. Settings and the running timer
are kept in the browser's `localStorage`. The timer keeps running while
you move around the app, and a compact countdown appears in the top bar
when the board isn't visible. It runs entirely in the browser and never
writes to your notes.

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
not mapped at all. See `docs/05-MCP-SPEC.md` for the full tool contract.

| Area | Tools |
|---|---|
| Notes | `search_notes`, `get_note`, `create_note`, `update_note`, `create_folder`, `move_note`, `move_folder`, `get_backlinks`, `get_recent_notes`, `get_config` |
| Tasks | `list_tasks`, `get_task`, `create_task`, `update_task` (fields, status, acceptance-criteria add/remove/check/uncheck, plan/notes set/append, final summary), `move_task` (status + position), `archive_task`, `get_board`, `search_tasks`, `get_task_workflow` |

The resource `dotnotes://workflow/tasks` (also available from the
`get_task_workflow` tool) tells an agent how to work with tasks:
search before creating one, write tasks as self-contained work orders,
record a plan before coding, check acceptance criteria only with
evidence, and finish with a summary.

## Project structure

```
CLAUDE.md                          # project rules & constraints for Claude Code
Directory.Build.props              # release <Version> (single source of truth)
NOTICE                             # third-party attribution (Backlog.md, MIT)
DEPLOYMENT.md                      # full deployment guide
docs/
  01-PROJECT-PLAN.md               # phased build order, tasks, exit criteria per phase
  02-ARCHITECTURE.md               # tech stack and system design
  03-FEATURE-SPEC.md               # feature checklist, MVP vs stretch (all Must-have items done)
  04-API-SPEC.md                   # REST contract
  05-MCP-SPEC.md                   # MCP server tool contract
  06-DATA-MODEL.md                 # vault layout, wikilinks, indexes, task frontmatter
  features/tasks-kanban/PLAN.md    # Tasks & Kanban design, feature inventory, decisions
src/
  DotNotes.Api/                    # ASP.NET Core host: REST API, MCP endpoint, static frontend
  DotNotes.Core/                   # framework-free domain library (notes, links, search, sharing, media, tasks)
tests/                             # xUnit test projects (291 tests)
deploy/
  podman/dotnotes.container        # Podman Quadlet unit (run as a systemd service)
.claude/agents/                    # the subagents this project was built with, kept for future changes
```

`docs/` remains the source of truth for *why* things are shaped the way
they are — update the relevant doc first before changing behavior that
contradicts it (see `CLAUDE.md`).

## Notes

- Targets **.NET 10 (LTS)**.
- Deliberately **descopes multi-language UI and multi-user accounts** —
  see `docs/03-FEATURE-SPEC.md`'s "Explicitly descoped" section.
- Version **0.2.0** — defined once in `Directory.Build.props`.
- The MCP server uses the official `ModelContextProtocol` C# SDK
  (currently pre-1.0, pinned at `2.2.0`) — `docs/05-MCP-SPEC.md` has the
  full tool contract.

## Acknowledgements

- [NoteDiscovery](https://github.com/gamosoft/NoteDiscovery) — the app
  whose layout and feature set dotNotes replicates.
- [Backlog.md](https://github.com/MrLesk/Backlog.md) (MIT, © 2025
  Backlog.md) — its task file format, section markers, ordinal-based
  board ordering and agent-workflow guidance are reimplemented here
  (no code copied). See [`NOTICE`](NOTICE).
