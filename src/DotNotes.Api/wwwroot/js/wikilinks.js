// Client-side mirror of DotNotes.Core.Links.WikiLinkResolver's resolution
// rule (see that file's remarks for the full, authoritative explanation).
// This module keeps a small cache of "known note paths" - built from
// `GET /api/graph`'s node list, filtered to nodes that actually exist on
// disk (`exists: true`; unresolved link targets are excluded, matching
// the server's own "known note paths" input to WikiLinkResolver.Resolve)
// - and uses it to answer two questions used elsewhere in the app:
//   1. markdown.js: should a rendered `[[wikilink]]` be styled as a link
//      to a real note, or as a "missing" (not-yet-created) one?
//   2. app.js: when a wikilink is clicked, what note path should it
//      navigate to (or offer to create)?
//
// Exact parity with the backend isn't required (per the Phase 3 task
// brief) - this is a "good enough to navigate correctly in the common
// case" approximation, refreshed opportunistically rather than kept
// perfectly live via a socket/push channel (overkill for a personal,
// single-user tool - see CLAUDE.md's "keep DOM/JS simple" rule).
const WikiLinks = (() => {
  // Map<lowercased vault-relative path, actual-cased vault-relative path>
  // for every note that currently exists, per the last `refresh()`.
  let existingPathsByLower = new Map();
  // Map<lowercased bare title, actual-cased path[]> - same source data,
  // indexed by filename-without-extension for the "bare title" match step.
  let pathsByLowerTitle = new Map();
  let loaded = false;
  let refreshPromise = null;

  /** Mirrors WikiLinkResolver.NormalizeToNotePath. */
  function normalizeToNotePath(rawTarget) {
    const normalized = (rawTarget || '').trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    return /\.md$/i.test(normalized) ? normalized : `${normalized}.md`;
  }

  /** Mirrors WikiLinkResolver.GetBareTitle. */
  function getBareTitle(notePath) {
    const lastSlash = notePath.lastIndexOf('/');
    const fileName = lastSlash >= 0 ? notePath.slice(lastSlash + 1) : notePath;
    return /\.md$/i.test(fileName) ? fileName.slice(0, -3) : fileName;
  }

  function countPathSegments(path) {
    return (path.match(/\//g) || []).length;
  }

  /** Rebuilds the lookup caches from a `GET /api/graph` response. */
  function rebuildFrom(graph) {
    existingPathsByLower = new Map();
    pathsByLowerTitle = new Map();

    for (const node of graph.nodes || []) {
      if (!node.exists) {
        continue; // unresolved link target, not a real note - excluded per the doc comment above.
      }
      existingPathsByLower.set(node.id.toLowerCase(), node.id);

      const titleLower = getBareTitle(node.id).toLowerCase();
      const bucket = pathsByLowerTitle.get(titleLower) || [];
      bucket.push(node.id);
      pathsByLowerTitle.set(titleLower, bucket);
    }
    loaded = true;
  }

  /** Fetches `/api/graph` and rebuilds the resolution cache. Safe to call repeatedly (e.g. after creating/deleting a note). */
  function refresh() {
    refreshPromise = Api.getGraph()
      .then(rebuildFrom)
      .catch((err) => {
        // Leave the previous (possibly empty) cache in place; resolution
        // just falls back to "unknown" below until a later refresh succeeds.
        console.error('Failed to load graph for wikilink resolution', err);
      });
    return refreshPromise;
  }

  /**
   * Resolves a raw `[[...]]` target (whatever text was between the
   * brackets, before an optional `|Display Text` alias) to
   * `{ path, exists }`, mirroring WikiLinkResolver.Resolve's three-step
   * rule: exact path match, else unique bare-title match, else a
   * deterministic shallowest/alphabetical tie-break, else unresolved.
   *
   * Before the first successful `refresh()`, the cache is empty and this
   * optimistically reports `exists: true` (so links aren't flashed as
   * "missing" before the graph has loaded) - app.js's click handler falls
   * back to an offer-to-create path if navigation then 404s.
   */
  function resolve(rawTarget) {
    const normalized = normalizeToNotePath(rawTarget);

    if (!loaded) {
      return { path: normalized, exists: true };
    }

    const exact = existingPathsByLower.get(normalized.toLowerCase());
    if (exact) {
      return { path: exact, exists: true };
    }

    const titleMatches = pathsByLowerTitle.get(getBareTitle(normalized).toLowerCase());
    if (!titleMatches || titleMatches.length === 0) {
      return { path: normalized, exists: false };
    }
    if (titleMatches.length === 1) {
      return { path: titleMatches[0], exists: true };
    }

    const chosen = [...titleMatches].sort((a, b) => {
      const segmentDiff = countPathSegments(a) - countPathSegments(b);
      return segmentDiff !== 0 ? segmentDiff : a.localeCompare(b, undefined, { sensitivity: 'base' });
    })[0];
    return { path: chosen, exists: true };
  }

  return { refresh, resolve, normalizeToNotePath, getBareTitle };
})();
