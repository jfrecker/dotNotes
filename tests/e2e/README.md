# dotNotes end-to-end (Playwright) suite

Dev-only test tooling for behavior only a real browser can exercise:
sidebar drag-and-drop, the right-click/keyboard context menu, the "+New"
dropdown, the Home/folder-browse view, inline-title rename + wikilink
rewrite propagation to a second open note, Edit/Split/Preview tab
switching + the formatting toolbar, and the light/dark theme toggle.

Per `CLAUDE.md`: this directory is **not** part of `dotnet test`, is
**not** a frontend build pipeline, and nothing it installs (Node
`node_modules/`, the downloaded Chromium browser, `.chromium-libs/`, the
throwaway `.e2e-vault/`) ships in the app or the Docker image. All of that
is gitignored - only this README, `package.json`, `playwright.config.js`,
`scripts/`, and `specs/*.spec.js` are checked in.

## One-time setup

```bash
cd tests/e2e
npm install
npx playwright install chromium
```

On a minimal Linux install without a desktop environment, Playwright's
downloaded Chromium may be missing a few shared libraries
(`libnspr4`, `libnss3`, `libasound2`, ...) that a normal desktop Linux box
or most CI Ubuntu images already have. If `npx playwright test` fails with
`error while loading shared libraries`, run:

```bash
./scripts/fetch-chromium-libs.sh
```

This downloads just those `.deb` packages (`apt-get download`, no
`sudo`/install) and extracts (`dpkg -x`) their `.so` files into
`.chromium-libs/` (gitignored). `playwright.config.js` automatically picks
this directory up via `LD_LIBRARY_PATH` if it exists, and does nothing
otherwise.

## Running the suite

The `.NET` SDK must be resolvable as `dotnet` on `PATH` (per the repo's own
convention, it lives at `~/.dotnet`, not on `PATH` by default):

```bash
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd tests/e2e
npx playwright test
```

By default this **starts its own instance of the app** (`dotnet run
--project src/DotNotes.Api`) on `http://127.0.0.1:5199` (not the app's
usual `5175`, so it never collides with a developer's own `dotnet run`),
pointed at a throwaway vault directory (`tests/e2e/.e2e-vault/`, gitignored
- never the real `src/DotNotes.Api/vault/`). Playwright tears the server
down again once the run finishes. `webServer.url` polls `/healthz` before
tests start, matching the same health check Docker's `HEALTHCHECK` uses.

Every spec seeds only the notes/folders it needs (via the real REST API,
`specs/fixtures.js`'s `Api` helper) under a run-unique name
(`specs/fixtures.js`'s `uniqueName`) and deletes them again in the test
body itself, so specs never depend on execution order and re-running the
suite against a not-yet-cleaned vault doesn't collide with an earlier
run's leftovers.

### Running against an already-running instance

If you'd rather point the suite at an app you started yourself (e.g. to
watch the browser interact with your own dev vault, or because you're
running the app in Docker):

```bash
E2E_SKIP_WEBSERVER=1 E2E_BASE_URL=http://localhost:5175 npx playwright test
```

**Warning:** this suite creates, renames, moves and deletes real notes and
folders as part of its tests. Never point it at a vault containing real
notes you care about - always use a scratch/throwaway vault when supplying
your own instance this way.

### Watching it run / debugging a failure

```bash
npx playwright test --headed          # visible browser
npx playwright test --debug           # Playwright Inspector, step-through
npx playwright show-trace <trace.zip> # inspect a failed run's trace
```

## What's covered here vs. `dotnet test`

`dotnet test` (the `DotNotes.Core.Tests`/`DotNotes.Api.Tests` projects)
covers every business rule and REST/MCP contract - repository CRUD, path
validation, wikilink parsing/rewriting, backlink/search index updates,
share tokens, and the full HTTP request/response shape of every endpoint,
including error cases - none of which needs a real browser and all of
which is far faster and more precisely assertable through
`WebApplicationFactory` than by driving a UI. This suite intentionally
only covers the remaining slice that a headless `HttpClient` fundamentally
cannot: native HTML5 drag-and-drop, `contextmenu`/keyboard-menu wiring,
`window.prompt`/`window.alert` interception, CSS-computed rendering
(dark mode), and cross-editor-instance UI state (the "another open note
reloads because its content was rewritten out from under it" scenario).
