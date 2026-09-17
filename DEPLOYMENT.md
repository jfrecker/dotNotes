# Deploying dotNotes

dotNotes is a single-user, self-hosted app with no database and no
cloud dependency — everything it needs is the compiled app plus a
directory of plain `.md` files (the "vault"). This is the canonical,
complete guide to getting it running, whichever way you prefer:

- **Docker Compose** (recommended default) — one container, a
  bind-mounted vault directory, `docker compose up -d` and you're done.
- **Native, no Docker** — a plain `dotnet publish` output you run
  directly, on Windows or Linux, for anyone who doesn't want a
  container runtime at all (e.g. running it as a systemd service).

Both paths run the exact same code and read the exact same
configuration (`docs/02-ARCHITECTURE.md`'s Configuration section) — the
only difference is how the process is started and how config gets to
it.

---

## Prerequisites

| Path | You need |
|---|---|
| Docker Compose | [Docker Engine](https://docs.docker.com/engine/install/) or [Docker Desktop](https://www.docker.com/products/docker-desktop/) with the Compose plugin (`docker compose version` works). Nothing else — the .NET SDK/runtime is never installed on your host, only inside the image. |
| Native (framework-dependent) | The [.NET 10 ASP.NET Core Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or the full SDK, which includes it) installed on the machine that will *run* the app. Publishing also needs the .NET 10 SDK, either on that machine or on any machine you copy the published output from. |
| Native (self-contained) | The .NET 10 SDK only on whichever machine *builds* the publish output — the machine that *runs* it needs nothing at all, not even the runtime. |

---

## Quick start (Docker Compose — recommended)

```bash
git clone https://github.com/jfrecker/dotNotes.git
cd dotNotes
docker compose up -d --build
curl http://localhost:5175/healthz
```

That's it — no manual folder creation, no config file to edit first.
`./vault` (created automatically on first run) is where your notes end
up on the host filesystem; see [Backup & restore](#backup--restore) for
what to do with it. Skip to [All deployment
modes](#all-deployment-modes) below for WSL2-specific notes, or
[Full configuration reference](#full-configuration-reference) to
change the port, vault location, or feature flags before you start it.

---

## All deployment modes

### Docker Compose on WSL2 (Ubuntu) — the common path

1. **Install Docker.** Two options, pick one:
   - **Docker Desktop's WSL2 integration** (simplest if you're on
     Windows): install
     [Docker Desktop](https://www.docker.com/products/docker-desktop/)
     on Windows, then in Docker Desktop go to Settings → Resources →
     WSL Integration and enable it for your Ubuntu distro. `docker` and
     `docker compose` then work directly from an Ubuntu WSL2 shell with
     no extra install inside the distro.
   - **Plain Docker Engine inside the WSL2 distro** (no Docker Desktop):
     from an Ubuntu WSL2 shell:
     ```bash
     curl -fsSL https://get.docker.com | sudo sh
     sudo usermod -aG docker "$USER"
     ```
     Then close and reopen your WSL2 shell (or run `newgrp docker`) so
     the group membership takes effect, and start the Docker daemon if
     it isn't already running:
     ```bash
     sudo service docker start
     ```
     (WSL2 distros don't run systemd by default on every Windows
     build, hence `service` rather than `systemctl`; if your distro has
     systemd enabled, `sudo systemctl enable --now docker` also works
     and persists across reboots.)

2. **Get the project onto the WSL2 filesystem** (not `/mnt/c/...` — the
   Linux-native filesystem is what Docker bind mounts and file
   watching perform best against):
   ```bash
   cd ~
   git clone https://github.com/jfrecker/dotNotes.git dotnotes
   cd dotnotes
   ```
   (Or `cp -r` a copy of the project in, if you're not cloning from a
   remote.)

3. **Start it:**
   ```bash
   docker compose up -d --build
   ```
   The app is now reachable at `http://localhost:5175` from Windows
   (WSL2 forwards `localhost` ports to Windows automatically) and from
   inside the WSL2 distro.

4. **Check it's healthy / watch the logs:**
   ```bash
   docker compose ps
   curl http://localhost:5175/healthz
   docker compose logs -f
   ```
   (Ctrl+C exits `logs -f` without stopping the container.)

5. **Where your notes end up, for backup.** `docker-compose.yml` bind-mounts
   `./vault` (relative to wherever you cloned the project) to `/data/vault`
   inside the container. From the WSL2 shell that's just
   `~/dotnotes/vault/`; from **Windows Explorer** the same files are visible
   at:
   ```
   \\wsl$\Ubuntu\home\<your-wsl-username>\dotnotes\vault
   ```
   (substitute your actual distro name and username — run `whoami` and
   `wsl -l -v` from PowerShell if unsure). Since notes are just plain
   `.md` files plus a `_media/` folder and a small `.nd-shares.json`,
   any Windows-side backup tool (File History, a synced OneDrive/Dropbox
   folder, a scheduled robocopy, etc.) can point straight at that
   `\\wsl$\...` path, or you can `git init` inside the vault itself for
   version-controlled backups — no export step, no `docker cp` needed.

   By default the container writes vault files as its own built-in
   `app` user (uid/gid `1654`), not as you — fine from Windows Explorer,
   but on a plain Linux host (or if you ever want to edit/`git init` the
   vault directly as your own WSL2 Linux user) that ownership mismatch
   means you can't touch those files without `sudo`. Set `PUID`/`PGID`
   (see [Full configuration reference](#full-configuration-reference))
   to your own `id -u`/`id -g` to have the container write files you
   actually own.

### Docker Compose on a plain Linux host

Same commands, no WSL2-specific caveats:

```bash
curl -fsSL https://get.docker.com | sudo sh   # or your distro's package manager
sudo usermod -aG docker "$USER"               # then re-login/newgrp docker
git clone https://github.com/jfrecker/dotNotes.git dotnotes
cd dotnotes
docker compose up -d --build
curl http://localhost:5175/healthz
docker compose logs -f
```

The vault ends up at `./vault` relative to wherever you cloned the
project (e.g. `/home/<user>/dotnotes/vault`) — back it up with whatever
file-level tool you'd already use for the rest of `/home`. Set
`PUID`/`PGID` (below) to your own `id -u`/`id -g` so the container
writes files you own, since there's no Windows Explorer to smooth over
the ownership mismatch here.

### Native on Windows (no Docker)

Requires the .NET 10 SDK to publish (the ASP.NET Core Runtime alone is
enough to *run* the published output — see
[Prerequisites](#prerequisites)). From PowerShell, in the cloned repo:

```powershell
dotnet publish src\DotNotes.Api -c Release -o C:\dotnotes\publish
```

Then run it, pointing it at wherever you want the vault to live and
which port to listen on (both via environment variables — see the
[configuration reference](#full-configuration-reference)):

```powershell
cd C:\dotnotes\publish
$env:Vault__RootPath = "C:\dotnotes\vault"
$env:ASPNETCORE_URLS = "http://localhost:5175"
.\DotNotes.Api.exe
```

Browse to `http://localhost:5175`. To keep it running in the
background rather than tied to a PowerShell window, wrap it as a
Windows service (e.g. with [NSSM](https://nssm.cc/)) or a Scheduled
Task set to run at logon — there's no built-in `systemctl` equivalent
on Windows, so a small wrapper tool is the standard approach.

If you'd rather not install the .NET runtime on the machine that runs
it at all, publish self-contained instead (larger output, zero runtime
dependency on the target machine):

```powershell
dotnet publish src\DotNotes.Api -c Release -r win-x64 --self-contained -o C:\dotnotes\publish
```

The rest is identical — same `DotNotes.Api.exe`, same environment
variables.

### Native on Linux (no Docker) — e.g. as a systemd service

Framework-dependent publish (recommended default — see
[the note below](#framework-dependent-vs-self-contained-which-to-use)):

```bash
git clone https://github.com/jfrecker/dotNotes.git dotnotes
cd dotnotes
dotnet publish src/DotNotes.Api -c Release -o /opt/dotnotes/publish
mkdir -p /opt/dotnotes/vault
```

Run it directly to try it out first:

```bash
cd /opt/dotnotes/publish
Vault__RootPath=/opt/dotnotes/vault ASPNETCORE_URLS=http://localhost:5175 ./DotNotes.Api
```

For anything longer-lived than a foreground shell, run it as a systemd
service instead. Example unit file
(`/etc/systemd/system/dotnotes.service`):

```ini
[Unit]
Description=dotNotes
After=network.target

[Service]
WorkingDirectory=/opt/dotnotes/publish
ExecStart=/opt/dotnotes/publish/DotNotes.Api
Restart=on-failure
User=dotnotes
Environment=Vault__RootPath=/opt/dotnotes/vault
Environment=ASPNETCORE_URLS=http://localhost:5175
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

(Create a dedicated `dotnotes` system user first — `sudo useradd -r -s
/usr/sbin/nologin dotnotes` — and `chown -R dotnotes:dotnotes
/opt/dotnotes` so it can write the vault and its own `logs/`
directory.) Then:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now dotnotes
sudo systemctl status dotnotes
curl http://localhost:5175/healthz
```

To use a self-contained publish instead (zero .NET runtime dependency
on the host running the service):

```bash
dotnet publish src/DotNotes.Api -c Release -r linux-x64 --self-contained -o /opt/dotnotes/publish
```

— same systemd unit, same everything else.

#### Framework-dependent vs. self-contained: which to use

**Framework-dependent** (`dotnet publish -c Release -o <dir>`, no
`-r`/`--self-contained`) is the recommended default: the publish
output is small (roughly 13 MB vs. ~120 MB self-contained, confirmed
while writing this guide) and easy to re-publish/update, at the cost of
needing the .NET 10 ASP.NET Core Runtime installed once on the machine
that runs it. For anyone self-hosting on a machine they already
maintain (which describes most of this project's actual audience),
installing the runtime once is a non-issue and the smaller, simpler
output wins.

**Self-contained** (`-r win-x64|linux-x64 --self-contained`) bundles
the entire .NET runtime into the publish output — much larger, but the
target machine needs absolutely nothing pre-installed. Use this if
you're deploying to a machine you don't want to (or can't) install the
.NET runtime on, or you want the publish output to be fully
self-sufficient for an air-gapped/offline copy.

Both were built and run successfully against a scratch vault while
verifying this guide.

---

## Full configuration reference

Every setting lives in `src/DotNotes.Api/appsettings.json` and is
overridable via an environment variable using ASP.NET Core's standard
double-underscore convention for nested keys (`Section:Key` →
`Section__Key`). `docker-compose.yml` sets the app-level ones for you
already, with sensible defaults; override any of them by copying
`.env.example` to `.env` and editing it (see the comments in that
file), or by editing `docker-compose.yml`'s `environment:` block
directly for anything not covered by `.env.example`.

| Key (`appsettings.json`) | Env var override | Default | Docker `.env` variable | Notes |
|---|---|---|---|---|
| `Vault:RootPath` | `Vault__RootPath` | `vault` (resolved relative to the app's content root) | `VAULT_PATH` (host-side bind-mount source; the container-side path is fixed at `/data/vault` by the image) | The single source of truth — every note lives here as a plain file. Created automatically on startup if missing (`VaultPathValidator`), no manual step required. |
| `Server:Port` | `Server__Port` | `5175` | — | **Informational only** — reserved for future features that need to know the app's own port (e.g. building absolute share URLs). It does **not** control which port Kestrel actually binds to; that's `ASPNETCORE_URLS` (below). Changing this alone will not move the app to a different port. |
| `Sharing:Enabled` | `Sharing__Enabled` | `true` | `SHARING_ENABLED` | Enables/disables the public, token-protected share-link endpoints entirely (not mapped at all when `false`). |
| `Mcp:Enabled` | `Mcp__Enabled` | `true` | `MCP_ENABLED` | Enables/disables the `/mcp` endpoint entirely (not mapped at all when `false`). |
| `Serilog:MinimumLevel:Default` | `Serilog__MinimumLevel__Default` | `Information` (`Debug` in the `Development` environment) | — | Baseline log verbosity, written to both console and a rolling daily file under `logs/` (14-day retention). |
| `Serilog:MinimumLevel:Override:Microsoft.AspNetCore` | `Serilog__MinimumLevel__Override__Microsoft.AspNetCore` | `Warning` (`Information` in `Development`) | — | Quiets ASP.NET Core's own framework-level request logging separately from the app's own log level. |
| `AllowedHosts` | `AllowedHosts` | `*` | — | Standard ASP.NET Core host-header filter. Irrelevant for a purely localhost/LAN setup; only matters if you put a reverse proxy in front with a specific hostname and want to restrict which `Host:` headers are accepted. |
| — | `ASPNETCORE_URLS` | `http://+:5175` baked into the Docker image; unset by default for a native `dotnet publish` output (Kestrel then falls back to `http://localhost:5000`) | not exposed directly — the image's Kestrel bind is fixed; remap the **host-side** port instead (below) | This is what actually controls the address/port Kestrel listens on. For native runs, always set this explicitly (e.g. `ASPNETCORE_URLS=http://localhost:5175`) rather than relying on the default. |
| — | `PUID` / `PGID` | `1654` / `1654` (the image's built-in `app` user) | `PUID` / `PGID` | **Docker-only**, not part of the app's own config at all — read by `docker-entrypoint.sh` to decide which uid/gid the containerized `dotnet` process runs (and therefore writes vault files) as. Set to your own `id -u`/`id -g` so you own your notes on the host. Native deployments don't need this — the process already runs as whatever OS user started it. |
| — | `HOST_PORT` (Docker Compose only) | `5175` | `HOST_PORT` | The host-side half of the port mapping in `docker-compose.yml` (`${HOST_PORT:-5175}:5175`). Change this instead of `Server:Port`/`ASPNETCORE_URLS` if `5175` is already taken on your machine — the container's internal port stays `5175` either way. |

---

## Updating the app

**Docker Compose.** There is no published container registry for this
project (it's built from source, not pulled), so `docker compose pull`
is a no-op here (confirmed: it reports "No image to be pulled" for a
build-only service) — the actual update path is pulling new source and
rebuilding locally:

```bash
cd dotnotes
git pull
docker compose up -d --build
```

`docker compose up -d --build` rebuilds the image from the current
source and recreates the container, but never touches the bind-mounted
`./vault` directory — your notes are untouched by design (the vault is
never baked into the image; see `Dockerfile`'s comments and
`docs/02-ARCHITECTURE.md`). If you'd rather force a completely clean
rebuild (e.g. after a base-image security update), add `--no-cache` to
`docker compose build` first:

```bash
docker compose build --no-cache
docker compose up -d
```

**Native.** Re-publish over (or alongside) the existing publish
directory and restart the process/service — the vault lives outside
the publish directory entirely, so re-publishing never touches it:

```bash
git pull
dotnet publish src/DotNotes.Api -c Release -o /opt/dotnotes/publish
sudo systemctl restart dotnotes   # or just re-run the executable if you're not using systemd
```

---

## Backup & restore

The vault is just files — `docs/02-ARCHITECTURE.md`'s whole point is
that no index or database is ever the source of truth, so any
file-level backup approach works with zero app-specific tooling. Two
concrete options, both proven out during this project's QA pass:

### Option A — plain file backup (`rsync`/`tar`)

A daily cron job that mirrors the vault to another disk or a remote
host via `rsync`:

```bash
# crontab -e
0 3 * * * rsync -a --delete /home/<you>/dotnotes/vault/ /mnt/backup/dotnotes-vault/
```

Or a dated, compressed snapshot instead of a mirror:

```bash
tar -czf "/mnt/backup/dotnotes-vault-$(date +%F).tar.gz" -C /home/<you>/dotnotes vault
```

### Option B — version-controlled backup (`git`)

Since notes are plain text (and `.nd-shares.json` is human-readable
JSON too), the vault is just as `git`-able as any source tree:

```bash
cd /home/<you>/dotnotes/vault
git init
git add -A
git commit -m "Initial vault snapshot"
```

From then on, commit on whatever cadence you like (a cron'd `git add
-A && git commit -m "auto-backup $(date -Iseconds)"`, or manually
whenever you want a checkpoint), and push to a private remote for
off-machine redundancy. Restoring is `git clone`/`git checkout` back
onto the vault path, or just copying files back with `rsync`/`tar` if
you used Option A.

### Restoring

Stop the app, replace the contents of the vault directory with the
backed-up copy, and start the app back up — there's no import step and
nothing else to restore, since the search index, backlinks, and graph
are all derived caches rebuilt automatically from whatever files are on
disk at startup (and kept live afterward by the file watcher).

---

## Reverse proxy / HTTPS guidance

dotNotes itself serves plain HTTP, with **no TLS and no
authentication** on the main REST API (`/api/*`) or the MCP endpoint
(`/mcp`) — by design, for a single-user app trusted to run on
`localhost` or a private LAN (see `docs/02-ARCHITECTURE.md`'s
non-functional notes). The only access control anywhere in the app is
the optional per-note share-token mechanism (`Sharing:Enabled`), which
protects individual shared note links, not the app as a whole.

**If you want to reach it from outside your own network** — for
example, actually sending a shared note link to someone who isn't on
your LAN or VPN — put a reverse proxy in front of it that terminates
TLS. [Caddy](https://caddyserver.com/) is the simplest option: it
handles automatic HTTPS certificate issuance/renewal with essentially
no configuration. A complete `Caddyfile` for this is:

```
notes.example.com {
    reverse_proxy localhost:5175
}
```

That's the entire config — Caddy obtains and renews a Let's Encrypt
certificate for `notes.example.com` automatically and proxies
everything to the dotNotes container/process on port 5175. (nginx or
Traefik work equally well if you already run one of those; the
principle — TLS terminated in front, plain HTTP to `localhost:5175`
behind — is the same regardless of which proxy you pick.)

**Important security caveat:** a reverse proxy by itself only adds
TLS — it does **not** add authentication. Putting a TLS-terminating
proxy in front of the *whole app* and exposing it to the public
internet means `/api/*` and `/mcp` are reachable, unauthenticated, by
anyone who finds the URL — full read/write access to every note, not
just a specific shared one. That is **not safe** for anything beyond a
trusted local network. If you need the whole app reachable from
outside a trusted network/VPN, add an authentication layer in front of
it yourself first (e.g. Caddy's `basicauth` directive, or an
`authelia`/`oauth2-proxy` sidecar) — this is explicitly out of scope
for dotNotes itself (see `docs/02-ARCHITECTURE.md`: "If you later
expose this beyond your own machine/LAN, that's a deliberate follow-up
phase... not part of this plan"). The one thing that *is* safe to
expose deliberately, on its own, is an individual share link generated
via the `Sharing:Enabled` feature — that's exactly what it's for.

---

## Troubleshooting

**Port `5175` is already in use.**
- Docker Compose: set `HOST_PORT` in a `.env` file (copy
  `.env.example`) to a free port, e.g. `HOST_PORT=8080`, then `docker
  compose up -d`. The container's internal port stays `5175`; only the
  host-side mapping changes.
- Native: set `ASPNETCORE_URLS` to a different port before starting the
  app, e.g. `ASPNETCORE_URLS=http://localhost:8080 ./DotNotes.Api`.

**Permission-denied errors on the bind-mounted vault** (can't
`rm -rf`/edit/`git init` the vault directory as yourself). This happens
because the container's `app` user (uid/gid `1654`) owns the files it
wrote, not you. Set `PUID`/`PGID` in a `.env` file to your own `id -u`/
`id -g`, then `docker compose up -d` to recreate the container running
as that uid/gid going forward. (Files already written under the old
uid/gid need one manual `chown` afterward, e.g. `sudo chown -R
"$(id -u):$(id -g)" ./vault` — new writes from then on are correctly
owned.)

**A request returns `503 {"error":"vault_unavailable", "detail":
"..."}`.** This means the app can currently see that the configured
vault root directory doesn't exist on disk — typically a bind mount
whose host directory transiently vanished (an unmounted WSL2/network
drive, or a Docker host path momentarily gone), not data loss. The app
deliberately refuses to write in this state rather than silently
fabricating a fresh, empty vault (see `CHANGELOG.md`'s Phase 8 entry
for the full story of the bug this replaced). Fix: make sure the host
path behind the bind mount (or the native `Vault:RootPath`) is actually
present and mounted, then retry — no data is lost, the app just won't
touch disk until the real vault is back.

**An MCP client (Claude Desktop, Claude Code, etc.) can't connect to
`/mcp`.**
- Confirm `Mcp:Enabled` is `true` (`GET /api/config` reports the
  current effective value under `features.mcp`) — when disabled, the
  route isn't mapped at all and any request to `/mcp` 404s.
- Confirm the exact URL: `http://<host>:<port>/mcp` — Streamable HTTP
  transport, no separate port or process. For the Docker Compose
  default that's `http://localhost:5175/mcp`; adjust the port if you
  set `HOST_PORT`.
- If the client and the app are on different machines, make sure
  nothing in between (e.g. a firewall) is blocking that port — and see
  [Reverse proxy / HTTPS guidance](#reverse-proxy--https-guidance)
  before exposing `/mcp` beyond your own machine/LAN, since it carries
  the same no-auth caveat as the REST API.

---

## Connecting an AI assistant via MCP

Once the app is running (any of the modes above), an MCP-capable client
can reach it over the Streamable HTTP transport at `/mcp` — no separate
process, and no extra port to expose. Point Claude Desktop
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

See `docs/05-MCP-SPEC.md` for the full tool contract (`search_notes`,
`get_note`, `create_note`, `update_note`, `get_backlinks`,
`get_recent_notes`, `get_config`).
