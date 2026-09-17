// Dev-only Playwright config for dotNotes' browser-only-behavior suite
// (drag-and-drop, context menus, editor tabs, theme rendering - see
// README.md for the full rationale and CLAUDE.md's "tests/e2e ... is test
// tooling, not a frontend build pipeline" rule). Not consumed by
// `dotnet test`, not referenced by the Docker image or the shipped app.
const path = require('node:path');
const fs = require('node:fs');

// A non-default port, so this suite never collides with a developer's own
// `dotnet run` (default 5175, see
// src/DotNotes.Api/Properties/launchSettings.json) already running against
// their real vault.
const PORT = process.env.E2E_PORT || '5199';
const BASE_URL = process.env.E2E_BASE_URL || `http://127.0.0.1:${PORT}`;

const REPO_ROOT = path.resolve(__dirname, '..', '..');
const API_PROJECT = path.join(REPO_ROOT, 'src', 'DotNotes.Api');

// A throwaway vault directory, unique per config load - never the
// developer's real src/DotNotes.Api/vault/. Individual spec files still
// namespace whatever notes/folders they create under a per-run-unique name
// (see specs/fixtures.js's `uniqueName`) so re-running against an
// already-started server (E2E_SKIP_WEBSERVER=1) doesn't collide with
// leftovers from an earlier run.
const VAULT_ROOT = process.env.E2E_VAULT_ROOT || path.join(__dirname, '.e2e-vault');

// The prebuilt Chromium this suite drives (`npx playwright install
// chromium`) is missing a handful of shared libraries on a minimal
// Debian/Ubuntu base (libnspr4/libnss3/libasound2 and friends) that a
// normal desktop Linux install would already have. README.md documents how
// scripts/fetch-chromium-libs.sh populates .chromium-libs/ (gitignored,
// never committed) with just those .so files via `apt-get download` +
// `dpkg -x` - no root, no system package install required. Used only if
// that directory actually exists; a normal desktop/CI Linux box that
// already has these libraries system-wide is unaffected.
const chromiumLibsDir = path.join(__dirname, '.chromium-libs', 'x86_64-linux-gnu');
const launchEnv = fs.existsSync(chromiumLibsDir)
  ? { ...process.env, LD_LIBRARY_PATH: `${chromiumLibsDir}:${process.env.LD_LIBRARY_PATH || ''}` }
  : { ...process.env };

/** @type {import('@playwright/test').PlaywrightTestConfig} */
module.exports = {
  testDir: './specs',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  // Every spec shares one running app instance/vault (see webServer below),
  // so tests run one at a time rather than racing each other's tree state.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: BASE_URL,
    headless: true,
    viewport: { width: 1280, height: 900 },
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    launchOptions: { env: launchEnv },
  },
  webServer: process.env.E2E_SKIP_WEBSERVER
    ? undefined
    : {
        command: `dotnet run --project "${API_PROJECT}" --no-launch-profile --urls "${BASE_URL}"`,
        url: `${BASE_URL}/healthz`,
        reuseExistingServer: !process.env.CI,
        timeout: 120_000,
        env: {
          ...process.env,
          ASPNETCORE_ENVIRONMENT: 'Development',
          // Isolates this suite from any real vault - see VAULT_ROOT above.
          Vault__RootPath: VAULT_ROOT,
        },
      },
};
