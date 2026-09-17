// Markdown render pipeline: marked (markdown -> HTML) + highlight.js
// (fenced code blocks) + mermaid (```mermaid fences) + MathJax ($..$ / $$..$$).
// Exposes a small global `MarkdownView` object used by app.js.
const MarkdownView = (() => {
  let mermaidInitialized = false;
  let checkboxCounter = 0;

  // Read-only shared view (wwwroot/shared.html + js/shared.js) renders
  // [[wikilinks]] as plain, non-clickable text instead of an `<a>` a
  // visitor could click to browse the rest of the (private) vault - see
  // the `render()` `plainWikilinks` option below and this module's
  // wikilink renderer, which checks this flag at render time. Safe as a
  // single module-level flag (not a per-call parameter threaded through
  // marked's renderer callback) because `marked.parse()` runs
  // synchronously and JS is single-threaded, so no render can interleave
  // with another.
  let suppressWikilinkNavigation = false;

  // --- marked configuration --------------------------------------------
  //
  // marked v12 dropped the old built-in `highlight` option in favour of
  // renderer overrides, so code blocks are highlighted here directly
  // rather than via a `marked-highlight` plugin (kept out to avoid an
  // extra vendored dependency for one renderer method).
  //
  // Fenced ```mermaid blocks are rendered as `<pre class="mermaid">` so
  // mermaid.js's own scanner picks them up; everything else goes through
  // highlight.js.
  marked.use({
    gfm: true,
    breaks: false,
  });

  marked.use({
    renderer: {
      code(code, infostring) {
        const lang = (infostring || '').trim().split(/\s+/)[0] || '';

        if (lang.toLowerCase() === 'mermaid') {
          return `<pre class="mermaid">${escapeHtml(code)}</pre>\n`;
        }

        if (lang && window.hljs && window.hljs.getLanguage(lang)) {
          const highlighted = window.hljs.highlight(code, { language: lang, ignoreIllegals: true }).value;
          return `<pre><code class="hljs language-${escapeHtml(lang)}">${highlighted}</code></pre>\n`;
        }

        if (window.hljs) {
          const auto = window.hljs.highlightAuto(code);
          return `<pre><code class="hljs">${auto.value}</code></pre>\n`;
        }

        return `<pre><code>${escapeHtml(code)}</code></pre>\n`;
      },

      // Renders each GFM task-list checkbox with a stable, zero-based
      // `data-checkbox-index` reflecting its order of appearance in the
      // document (marked calls this once per task item, in document
      // order, including nested list items) - matches
      // `findNthCheckboxLine` below, which scans the raw markdown source
      // in the same top-to-bottom order. Unlike marked's default output,
      // the checkbox is NOT disabled, so it can be clicked in the
      // preview pane.
      checkbox(checked) {
        const index = checkboxCounter++;
        return `<input type="checkbox" class="task-checkbox" data-checkbox-index="${index}"${checked ? ' checked' : ''}>`;
      },
    },
  });

  function escapeHtml(text) {
    return text
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  // --- [[wikilinks]] --------------------------------------------------------
  //
  // marked has no built-in notion of `[[path]]` / `[[path|Display Text]]`
  // syntax (docs/06-DATA-MODEL.md), so it's added as a small custom inline
  // extension rather than pre/post-processing the HTML string (which would
  // risk mangling links that land inside code spans, etc.). Resolution of
  // whether the target actually exists is delegated to `WikiLinks.resolve`
  // (js/wikilinks.js), which mirrors the backend's WikiLinkResolver rule
  // against a client-side cache of `GET /api/graph`'s node list.
  //
  // The rendered element is a plain `<a>` with `href="#"` (so it looks and
  // behaves like a link - cursor, focusability - without ever actually
  // navigating the browser) plus data attributes app.js reads in its click
  // handler; app.js re-resolves at click time rather than trusting these
  // attributes, since the graph cache may have changed since this render.
  const WIKILINK_PATTERN = /^\[\[([^\]|]+)(?:\|([^\]]+))?\]\]/;

  marked.use({
    extensions: [
      {
        name: 'wikilink',
        level: 'inline',
        start(src) {
          return src.indexOf('[[');
        },
        tokenizer(src) {
          const match = WIKILINK_PATTERN.exec(src);
          if (!match) {
            return undefined;
          }
          const target = match[1].trim();
          const display = (match[2] || '').trim();
          return {
            type: 'wikilink',
            raw: match[0],
            target,
            text: display || target,
          };
        },
        renderer(token) {
          if (suppressWikilinkNavigation) {
            // Read-only shared view: a visitor shouldn't be able to
            // navigate/browse the private vault from a shared link, so
            // render plain text - no <a>, no click-to-create affordance,
            // and no "does this target exist" leak via title/class.
            return escapeHtml(token.text);
          }

          // `WikiLinks` (js/wikilinks.js) is a `const` declared in its own
          // classic <script> tag - it lives in the shared top-level scope
          // these scripts all run in, but (unlike `var`) never becomes a
          // `window` property, so `typeof` is the correct existence check
          // here, not `window.WikiLinks`.
          const resolution = typeof WikiLinks !== 'undefined'
            ? WikiLinks.resolve(token.target)
            : { path: token.target, exists: true };
          const missingClass = resolution.exists ? '' : ' wikilink-missing';
          return (
            `<a href="#" class="wikilink${missingClass}" ` +
            `data-wikilink-target="${escapeHtml(token.target)}" ` +
            `title="${resolution.exists ? escapeHtml(resolution.path) : 'Note does not exist yet - click to create'}">` +
            `${escapeHtml(token.text)}</a>`
          );
        },
      },
    ],
  });

  // --- _media/ path rewriting ----------------------------------------------
  //
  // A note references an uploaded file with a vault-relative path like
  // `_media/photo.png` (docs/04-API-SPEC.md's "Media" section /
  // docs/06-DATA-MODEL.md's vault layout), because that's the path that
  // actually resolves on disk and via the MCP/API surface. But the browser
  // can't fetch that path directly - `_media/` lives in the vault root,
  // outside wwwroot/, so it's only reachable through the backend's
  // `GET /media/{**path}` route (note: no `_media` segment in the URL, it's
  // stripped). This rewrites every rendered `src`/`href` that starts with
  // `_media/` to `/media/<rest>` after marked has produced the HTML, so it
  // covers plain markdown image syntax (`![alt](_media/x.png)`), plain
  // links to a non-image file under `_media/` (e.g. a linked PDF), and raw
  // HTML embeds that pass through marked untouched (`<audio src="_media/x.mp3">`,
  // `<video src="_media/x.mp4">`) in one place, rather than three special
  // cases in the marked renderer config above.
  const MEDIA_PATH_PREFIX = '_media/';

  function rewriteMediaPaths(container) {
    for (const el of container.querySelectorAll('[src], [href]')) {
      for (const attr of ['src', 'href']) {
        const value = el.getAttribute(attr);
        if (value && value.startsWith(MEDIA_PATH_PREFIX)) {
          el.setAttribute(attr, `/media/${value.slice(MEDIA_PATH_PREFIX.length)}`);
        }
      }
    }
  }

  // --- mermaid ------------------------------------------------------------

  function ensureMermaidInitialized() {
    if (mermaidInitialized || !window.mermaid) {
      return;
    }
    window.mermaid.initialize({ startOnLoad: false, securityLevel: 'loose' });
    mermaidInitialized = true;
  }

  async function renderMermaidBlocks(container) {
    if (!window.mermaid) {
      return;
    }
    ensureMermaidInitialized();
    const nodes = container.querySelectorAll('pre.mermaid');
    if (nodes.length === 0) {
      return;
    }
    try {
      await window.mermaid.run({ nodes: Array.from(nodes), suppressErrors: true });
    } catch (err) {
      // A single malformed diagram shouldn't break the rest of the
      // preview; mermaid.run with suppressErrors already leaves a
      // readable error message in place of the failed diagram.
      console.error('mermaid render error', err);
    }
  }

  // --- MathJax --------------------------------------------------------------

  async function typesetMath(container) {
    if (!window.MathJax || !window.MathJax.typesetPromise) {
      return;
    }
    if (window.MathJax.startup && window.MathJax.startup.promise) {
      await window.MathJax.startup.promise;
    }
    try {
      await window.MathJax.typesetPromise([container]);
    } catch (err) {
      console.error('MathJax typeset error', err);
    }
  }

  // --- public render entry point -----------------------------------------

  /**
   * Renders `rawMarkdown` into `container` (mermaid + MathJax included).
   * `options.plainWikilinks` (default false) renders `[[wikilinks]]` as
   * plain non-clickable text instead of a navigable/creatable link - used
   * by the read-only shared view (js/shared.js) so a visitor can't browse
   * the rest of the private vault from a shared link.
   */
  async function render(container, rawMarkdown, options = {}) {
    checkboxCounter = 0;
    suppressWikilinkNavigation = !!options.plainWikilinks;
    container.innerHTML = marked.parse(rawMarkdown || '');
    rewriteMediaPaths(container);
    await renderMermaidBlocks(container);
    await typesetMath(container);
  }

  // --- checkbox toggle (pure, operates on raw markdown text) --------------

  // Matches a task-list item line: leading whitespace, a bullet
  // (-, *, +) or ordered marker (1. / 1)), then `[ ]`/`[x]`/`[X]`.
  const TASK_LINE_PATTERN = /^(\s*(?:[-*+]|\d+[.)])\s+)\[([ xX])\](.*)$/;

  /**
   * Returns `rawMarkdown` with the `checkboxIndex`-th task checkbox (0-based,
   * top-to-bottom document order - the same order marked assigns via the
   * `checkbox` renderer above) flipped between `[ ]` and `[x]`. Returns the
   * input unchanged if no checkbox with that index is found.
   */
  function toggleCheckbox(rawMarkdown, checkboxIndex) {
    const lines = (rawMarkdown || '').split('\n');
    let seen = 0;
    for (let i = 0; i < lines.length; i++) {
      const match = lines[i].match(TASK_LINE_PATTERN);
      if (!match) {
        continue;
      }
      if (seen === checkboxIndex) {
        const isChecked = match[2] !== ' ';
        const newMark = isChecked ? ' ' : 'x';
        lines[i] = `${match[1]}[${newMark}]${match[3]}`;
        return lines.join('\n');
      }
      seen++;
    }
    return rawMarkdown;
  }

  return { render, toggleCheckbox };
})();
