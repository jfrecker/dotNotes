// Read-only shared-note view (wwwroot/shared.html). Deliberately talks to
// only one, narrow, token-scoped endpoint - GET /api/share/{token}/content
// - rather than the main /api/notes/* surface (which has no auth and
// would let a shared-link visitor browse the whole private vault). See
// docs/04-API-SPEC.md's Sharing section and this phase's report for why
// that endpoint exists.
(() => {
  const previewEl = document.getElementById('preview');
  const statusEl = document.getElementById('shared-status');

  /** The token is the single path segment after "/shared/" (URL-decoded). */
  function getTokenFromUrl() {
    const segments = window.location.pathname.split('/').filter(Boolean);
    return segments.length > 0 ? decodeURIComponent(segments[segments.length - 1]) : '';
  }

  function showError(message) {
    statusEl.textContent = '';
    previewEl.innerHTML = '';
    const p = document.createElement('p');
    p.className = 'text-red-600';
    p.textContent = message;
    previewEl.appendChild(p);
  }

  async function loadSharedNote() {
    const token = getTokenFromUrl();
    if (!token) {
      showError('No share token was given.');
      return;
    }

    statusEl.textContent = 'Loading…';
    let response;
    try {
      response = await fetch(`/api/share/${encodeURIComponent(token)}/content`);
    } catch (err) {
      showError(`Could not reach the server: ${err.message}`);
      return;
    }

    if (!response.ok) {
      // Deliberately generic per the API's own not-found handling
      // (missing/expired/revoked tokens are indistinguishable) - see
      // SharingEndpoints.ShareNotFound.
      showError(
        response.status === 404
          ? 'This shared link is invalid, expired, or has been revoked.'
          : `Failed to load this note (${response.status}).`,
      );
      return;
    }

    const note = await response.json();
    statusEl.textContent = note.path;
    document.title = `${note.path} — dotNotes (shared)`;

    // plainWikilinks: true - see js/markdown.js's render() doc comment -
    // renders [[wikilinks]] as plain text, not a navigable/creatable link.
    await MarkdownView.render(previewEl, note.content, { plainWikilinks: true });
  }

  loadSharedNote();
})();
