// Small shared helpers for this suite's spec files. Deliberately plain
// functions (not a Playwright fixture-injection setup) - see README.md for
// why this suite stays as simple as possible.
const { test: base, expect } = require('@playwright/test');

/** A per-test-run-unique name prefix, so two runs against the same
 * long-lived vault (E2E_SKIP_WEBSERVER=1 re-runs) never collide with
 * leftovers from an earlier run that didn't clean up (e.g. a failed
 * assertion skipped this file's own afterEach). */
function uniqueName(label) {
  return `${label}-${Date.now()}-${Math.floor(Math.random() * 100000)}`;
}

/** Thin wrapper over the REST API (docs/04-API-SPEC.md) for test setup/
 * teardown that would be slow or flaky to do by driving the UI - e.g.
 * seeding a note's content directly rather than typing it character by
 * character. Using the real API (not writing to disk directly) exercises
 * the same code path the app itself uses, and keeps the in-memory link/
 * search indexes consistent with whatever's seeded. */
class Api {
  constructor(request, baseURL) {
    this.request = request;
    this.baseURL = baseURL;
  }

  async saveNote(path, content) {
    const res = await this.request.put(`${this.baseURL}/api/notes/${encodeURIComponentPath(path)}`, {
      data: { content },
    });
    expect(res.ok(), `PUT /api/notes/${path} failed: ${res.status()} ${await res.text()}`).toBeTruthy();
  }

  async createFolder(path) {
    const res = await this.request.post(`${this.baseURL}/api/folders/${encodeURIComponentPath(path)}`);
    expect(res.ok(), `POST /api/folders/${path} failed: ${res.status()} ${await res.text()}`).toBeTruthy();
  }

  async deleteNote(path) {
    await this.request.delete(`${this.baseURL}/api/notes/${encodeURIComponentPath(path)}`);
  }

  /** Best-effort recursive folder cleanup: moves nothing, just deletes every
   * note this suite is tracking underneath `folderPath` via the tree
   * endpoint, then leaves the (now-empty) folder behind - matching the
   * app's own "never auto-delete empty folders" behavior
   * (docs/06-DATA-MODEL.md), which is harmless test-vault clutter, not a
   * correctness problem for the next run. */
  async deleteTree(rootPath) {
    const res = await this.request.get(`${this.baseURL}/api/notes`);
    if (!res.ok()) {
      return;
    }
    const tree = await res.json();
    const notePaths = [];
    collectNotePaths(tree, notePaths);
    await Promise.all(
      notePaths
        .filter((p) => p === rootPath || p.startsWith(`${rootPath}/`))
        .map((p) => this.deleteNote(p)),
    );
  }
}

function collectNotePaths(entries, out) {
  for (const entry of entries || []) {
    if (entry.type === 'file') {
      out.push(entry.path);
    } else if (entry.children) {
      collectNotePaths(entry.children, out);
    }
  }
}

function encodeURIComponentPath(path) {
  // Vault-relative paths use '/' as a real separator, not something to
  // percent-encode - only encode within each segment.
  return path.split('/').map(encodeURIComponent).join('/');
}

/**
 * Ensures the sidebar tree row `rowLocator` (a folder) is expanded,
 * clicking it only if it's currently collapsed. Needed because a native
 * HTML5 drag-and-drop whose *target* is a folder row can itself leave that
 * row already expanded (Chromium's drop sequence ends with a real `click`
 * on the drop target, on top of the `drop` event js/tree.js listens for) -
 * an unconditional extra `.click()` after such a drag would toggle it back
 * closed instead of opening it, which is exactly what this guards against.
 */
async function ensureExpanded(rowLocator) {
  if ((await rowLocator.getAttribute('aria-expanded')) !== 'true') {
    await rowLocator.click();
  }
}

const test = base.extend({
  api: async ({ request, baseURL }, use) => {
    await use(new Api(request, baseURL));
  },
});

module.exports = { test, expect, uniqueName, ensureExpanded };
