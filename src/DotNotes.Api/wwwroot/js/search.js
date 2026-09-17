// Search box + results dropdown (docs/03-FEATURE-SPEC.md: "Full-text
// search across the vault"). A thin, self-contained module - like
// tree.js, it owns its own DOM rendering and hands control back to the
// caller via a handler callback rather than reaching into app.js
// directly, so picking a result reuses whatever note-loading function
// app.js already wires up for file-tree clicks and wikilink navigation,
// instead of duplicating that logic here.
const Search = (() => {
  const DEBOUNCE_MS = 250; // same ballpark as app.js's PREVIEW_DEBOUNCE_MS ("a short debounce")
  const RESULT_LIMIT = 20; // matches docs/04-API-SPEC.md's documented default for `limit`

  let inputEl = null;
  let resultsEl = null;
  let onSelectNote = null;
  let debounceTimer = null;
  let requestSeq = 0; // guards against an older in-flight request clobbering a newer one's results

  function openDropdown() {
    resultsEl.classList.remove('hidden');
  }

  /** Hides the dropdown and clears its contents, so re-opening never briefly shows stale results. */
  function closeDropdown() {
    resultsEl.classList.add('hidden');
    resultsEl.innerHTML = '';
  }

  function renderMessage(text) {
    resultsEl.innerHTML = '';
    const message = document.createElement('p');
    message.className = 'search-empty';
    message.textContent = text;
    resultsEl.appendChild(message);
    openDropdown();
  }

  function renderResults(results) {
    resultsEl.innerHTML = '';

    if (!results || results.length === 0) {
      renderMessage('No results.');
      return;
    }

    for (const result of results) {
      const item = document.createElement('button');
      item.type = 'button';
      item.className = 'search-result';

      const title = document.createElement('div');
      title.className = 'search-result-title';
      title.textContent = result.title || result.path;
      item.appendChild(title);

      const path = document.createElement('div');
      path.className = 'search-result-path';
      path.textContent = result.path;
      item.appendChild(path);

      if (result.snippet) {
        const snippet = document.createElement('div');
        snippet.className = 'search-result-snippet';
        snippet.textContent = result.snippet;
        item.appendChild(snippet);
      }

      item.addEventListener('click', () => {
        closeDropdown();
        inputEl.value = '';
        onSelectNote(result.path);
      });

      resultsEl.appendChild(item);
    }

    openDropdown();
  }

  async function runSearch(query) {
    const seq = ++requestSeq;
    try {
      const results = await Api.search(query, RESULT_LIMIT);
      if (seq !== requestSeq) {
        return; // a newer keystroke's request already landed - discard this stale response
      }
      renderResults(results);
    } catch (err) {
      if (seq !== requestSeq) {
        return;
      }
      renderMessage(`Search failed: ${err.message}`);
    }
  }

  function scheduleSearch() {
    clearTimeout(debounceTimer);
    const query = inputEl.value.trim();
    if (!query) {
      // Emptying the box dismisses the dropdown outright rather than
      // round-tripping to the server for the (always-empty) `q=""` case.
      closeDropdown();
      return;
    }
    debounceTimer = setTimeout(() => runSearch(query), DEBOUNCE_MS);
  }

  /**
   * Wires up the search input + results dropdown.
   * @param {HTMLInputElement} input
   * @param {HTMLElement} resultsContainer
   * @param {{ onSelectNote: (path: string) => void }} handlers
   */
  function init(input, resultsContainer, handlers) {
    inputEl = input;
    resultsEl = resultsContainer;
    onSelectNote = handlers.onSelectNote;

    inputEl.addEventListener('input', scheduleSearch);

    // Re-focusing the box with a query already typed (e.g. after an
    // outside click closed the dropdown) re-runs the search immediately
    // rather than waiting for another keystroke.
    inputEl.addEventListener('focus', () => {
      const query = inputEl.value.trim();
      if (query) {
        runSearch(query);
      }
    });

    inputEl.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') {
        closeDropdown();
      }
    });

    // Clicking anywhere outside the input or the dropdown dismisses it.
    document.addEventListener('click', (event) => {
      if (!inputEl.contains(event.target) && !resultsEl.contains(event.target)) {
        closeDropdown();
      }
    });
  }

  return { init };
})();
