// Thin wrapper around the Notes section of docs/04-API-SPEC.md. Every
// function returns a Promise that resolves with the parsed JSON body (or
// null for 204 No Content) and rejects with an Error whose `.message` is
// the server's `detail`/`error` string on non-2xx responses.
const Api = (() => {
  async function request(path, options) {
    const response = await fetch(path, options);

    if (!response.ok) {
      let body = null;
      try {
        body = await response.json();
      } catch {
        // Non-JSON error body (or no body at all) — fall back below.
      }
      const message = body?.detail || body?.error || `${response.status} ${response.statusText}`;
      const error = new Error(message);
      error.status = response.status;
      error.body = body;
      throw error;
    }

    if (response.status === 204) {
      return null;
    }
    return response.json();
  }

  // Vault-relative note paths (e.g. "projects/idea.md") are encoded
  // segment-by-segment so slashes keep working as path separators against
  // the {**path} catch-all route, while special characters within a
  // single segment (spaces, #, ?, etc.) are still percent-encoded.
  function encodePath(path) {
    return path
      .split('/')
      .map(encodeURIComponent)
      .join('/');
  }

  // Query-string builder shared by the Tasks endpoints below
  // (docs/features/tasks-kanban/PLAN.md §4) - every filter is optional and
  // simply omitted when falsy/absent, matching the API's own "all filters
  // optional" contract.
  function buildTaskQuery(filters) {
    const params = new URLSearchParams();
    for (const key of ['status', 'label', 'assignee', 'priority', 'milestone', 'q']) {
      const value = filters?.[key];
      if (value) {
        params.set(key, value);
      }
    }
    if (filters?.includeArchived) {
      params.set('includeArchived', 'true');
    }
    const qs = params.toString();
    return qs ? `?${qs}` : '';
  }

  return {
    getTree() {
      return request('/api/notes');
    },
    // `/api/config` (docs/features/tasks-kanban/PLAN.md §10):
    // `{ name, version, features, autosaveDelayMs }`.
    getConfig() {
      return request('/api/config');
    },
    // --- Tasks & Kanban (docs/features/tasks-kanban/PLAN.md §4) -----------
    getTaskConfig() {
      return request('/api/tasks/config');
    },
    listTasks(filters) {
      return request(`/api/tasks${buildTaskQuery(filters)}`);
    },
    getTaskRevision() {
      return request('/api/tasks/revision');
    },
    getBoard(filters) {
      return request(`/api/tasks/board${buildTaskQuery(filters)}`);
    },
    getTask(id) {
      return request(`/api/tasks/${encodeURIComponent(id)}`);
    },
    createTask(data) {
      return request('/api/tasks', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(data),
      });
    },
    // `TaskPatch`: only supplied fields change (docs/features/tasks-kanban/
    // PLAN.md §4) - callers should only include fields that actually changed.
    updateTask(id, patch) {
      return request(`/api/tasks/${encodeURIComponent(id)}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(patch),
      });
    },
    // `beforeId` (optional): insert immediately before that task in the
    // target column's FULL order - preferred over `index` because `index`
    // counts every task in the column, which a filtered board can't know.
    // Servers that predate `beforeId` ignore the unknown field.
    moveTask(id, status, index, beforeId) {
      const body = { status };
      if (index !== undefined && index !== null) {
        body.index = index;
      }
      if (beforeId) {
        body.beforeId = beforeId;
      }
      return request(`/api/tasks/${encodeURIComponent(id)}/move`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
    },
    archiveTask(id) {
      return request(`/api/tasks/${encodeURIComponent(id)}/archive`, { method: 'POST' });
    },
    convertNoteToTask(path, status) {
      const body = { path };
      if (status) {
        body.status = status;
      }
      return request('/api/tasks/convert', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
    },
    // `includeBacklinks` maps to the `?includeBacklinks=true` query param
    // documented in docs/04-API-SPEC.md's "Links & graph" section; when
    // omitted the response simply has no `backlinks` field.
    getNote(path, { includeBacklinks = false } = {}) {
      const query = includeBacklinks ? '?includeBacklinks=true' : '';
      return request(`/api/notes/${encodePath(path)}${query}`);
    },
    // `expectedUpdatedAt`, when given, enables optimistic-concurrency
    // checking (docs/features/tasks-kanban/PLAN.md §3/§4): the server
    // responds 409 `conflict` (with `currentUpdatedAt`) if the file's
    // mtime has moved on since the caller last read it. Omitted entirely
    // (not just falsy) keeps today's last-write-wins PUT behaviour.
    saveNote(path, content, expectedUpdatedAt) {
      const body = { content };
      if (expectedUpdatedAt) {
        body.expectedUpdatedAt = expectedUpdatedAt;
      }
      return request(`/api/notes/${encodePath(path)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
    },
    deleteNote(path) {
      return request(`/api/notes/${encodePath(path)}`, { method: 'DELETE' });
    },
    // Folder & note move/rename (docs/04-API-SPEC.md's Notes section).
    moveNote(path, destinationPath) {
      return request(`/api/notes/${encodePath(path)}/move`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ destinationPath }),
      });
    },
    // `mkdir -p` semantics, idempotent - no request body.
    createFolder(path) {
      return request(`/api/folders/${encodePath(path)}`, { method: 'POST' });
    },
    // Recursive: removes the folder and everything inside it. 404s if no
    // folder exists at `path` (docs/04-API-SPEC.md) - deliberately *not*
    // idempotent like deleteNote, so the UI can say "it's already gone".
    deleteFolder(path) {
      return request(`/api/folders/${encodePath(path)}`, { method: 'DELETE' });
    },
    moveFolder(path, destinationPath) {
      return request(`/api/folders/${encodePath(path)}/move`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ destinationPath }),
      });
    },
    getGraph() {
      return request('/api/graph');
    },
    // docs/04-API-SPEC.md's Search section: `q` missing/blank is safe to
    // send (the server returns `200 []`, not a 400), so callers can fire
    // this on every keystroke including an emptied search box.
    search(query, limit) {
      const params = new URLSearchParams();
      if (query) {
        params.set('q', query);
      }
      if (limit) {
        params.set('limit', String(limit));
      }
      const qs = params.toString();
      return request(`/api/search${qs ? `?${qs}` : ''}`);
    },
    // docs/04-API-SPEC.md's Sharing section: `expiresInDays` is optional,
    // so an omitted/undefined value is sent as an empty JSON object rather
    // than `{ expiresInDays: undefined }` (which JSON.stringify would drop
    // anyway, but being explicit here documents the "optional" contract).
    shareNote(path, expiresInDays) {
      const body = expiresInDays ? { expiresInDays } : {};
      return request(`/api/share/${encodePath(path)}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
    },
    revokeShare(token) {
      return request(`/api/share/${encodeURIComponent(token)}`, { method: 'DELETE' });
    },
    // multipart/form-data upload (field name `file`, per docs/04-API-SPEC.md's
    // "Media" section) - deliberately doesn't set a Content-Type header;
    // the browser sets `multipart/form-data; boundary=...` itself from the
    // FormData body, and overriding it manually would drop the boundary.
    uploadMedia(file) {
      const formData = new FormData();
      formData.append('file', file);
      return request('/api/upload', { method: 'POST', body: formData });
    },
  };
})();
