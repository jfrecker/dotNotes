// Wires together the file tree, editor, live preview and autosave.
// Kept as a single flat script (no framework, no module bundler) per
// docs/02-ARCHITECTURE.md - this is a personal single-user tool.
(() => {
  const PREVIEW_DEBOUNCE_MS = 300; // "a short debounce" for live re-render
  const AUTOSAVE_DEBOUNCE_MS = 1500; // "~1-2s after the user stops typing"
  const SAVED_INDICATOR_CLEAR_MS = 2000;

  const fileTreeEl = document.getElementById('file-tree');
  const editorEl = document.getElementById('editor');
  const previewEl = document.getElementById('preview');
  const saveStatusEl = document.getElementById('save-status');
  const homeLogoBtn = document.getElementById('home-logo-btn');
  const sidebarEl = document.getElementById('sidebar');
  const sidebarToggleBtn = document.getElementById('sidebar-toggle-btn');
  const sortToggleBtn = document.getElementById('sort-toggle-btn');
  const sortToggleLabelEl = document.getElementById('sort-toggle-label');
  const newMenuBtn = document.getElementById('new-menu-btn');
  const refreshTreeBtn = document.getElementById('refresh-tree-btn');
  const treeRootDropZoneEl = document.getElementById('tree-root-drop-zone');
  const treeRootDropLabelEl = document.getElementById('tree-root-drop-label');
  const renameModalEl = document.getElementById('rename-modal');
  const renameModalTitleEl = document.getElementById('rename-modal-title');
  const renameModalDescriptionEl = document.getElementById('rename-modal-description');
  const renameModalFieldEl = document.getElementById('rename-modal-field');
  const renameModalLabelEl = document.getElementById('rename-modal-label');
  const renameModalInputEl = document.getElementById('rename-modal-input');
  const renameModalErrorEl = document.getElementById('rename-modal-error');
  const renameModalConfirmBtn = document.getElementById('rename-modal-confirm-btn');
  const renameModalCancelBtn = document.getElementById('rename-modal-cancel-btn');
  const backlinksListEl = document.getElementById('backlinks-list');
  const backlinksRefreshBtn = document.getElementById('backlinks-refresh-btn');
  const searchInputEl = document.getElementById('search-input');
  const searchResultsEl = document.getElementById('search-results');
  const shareNoteBtn = document.getElementById('share-note-btn');
  const shareModalEl = document.getElementById('share-modal');
  const shareModalCloseBtn = document.getElementById('share-modal-close-btn');
  const shareModalErrorEl = document.getElementById('share-modal-error');
  const shareModalBodyEl = document.getElementById('share-modal-body');
  const shareModalUrlEl = document.getElementById('share-modal-url');
  const shareModalQrEl = document.getElementById('share-modal-qr');
  const shareModalCopyBtn = document.getElementById('share-modal-copy-btn');
  const shareModalRevokeBtn = document.getElementById('share-modal-revoke-btn');
  const mediaUploadInputEl = document.getElementById('media-upload-input');
  const viewModeToggleEl = document.getElementById('view-mode-toggle');
  const editorPaneEl = document.getElementById('editor-pane');
  const previewPaneEl = document.getElementById('preview-pane');
  const themeToggleBtn = document.getElementById('theme-toggle-btn');

  // Home / folder-browse view (docs/03-FEATURE-SPEC.md: "Home / folder-
  // browse view") - the default landing state, and where navigateHome below
  // sends the app whenever no note is open.
  const homeViewEl = document.getElementById('home-view');
  const homeAppTitleEl = document.getElementById('home-app-title');
  const homeBreadcrumbEl = document.getElementById('home-breadcrumb');
  const homeSummaryTextEl = document.getElementById('home-summary-text');
  const homeFolderGridEl = document.getElementById('home-folder-grid');
  const homeNewBtn = document.getElementById('home-new-btn');

  // Note editor header (docs/03-FEATURE-SPEC.md: "Note editor header ...")
  // and formatting toolbar - index.html's #note-editor-view.
  const noteEditorViewEl = document.getElementById('note-editor-view');
  const noteTitleInputEl = document.getElementById('note-title-input');
  const prevNoteBtn = document.getElementById('prev-note-btn');
  const nextNoteBtn = document.getElementById('next-note-btn');
  const deleteNoteBtn = document.getElementById('delete-note-btn');
  const splitDividerEl = document.getElementById('split-divider');
  const noteEditedLabelEl = document.getElementById('note-edited-label');
  const exportNoteBtn = document.getElementById('export-note-btn');
  const printNoteBtn = document.getElementById('print-note-btn');
  const copyLinkBtn = document.getElementById('copy-link-btn');
  const fullscreenBtn = document.getElementById('fullscreen-btn');
  const formattingToolbarEl = document.getElementById('formatting-toolbar');

  let currentPath = null;
  let currentUpdatedAt = null;
  // The folder currently browsed in the Home view ('' = vault root) -
  // js/app.js's navigateHome/renderHomeView.
  let currentBrowseFolder = '';
  let lastSavedContent = '';
  // The raw tree from the last successful `Api.getTree()` - kept so the
  // "Move to..." folder picker (openMoveToMenu below) can list every folder
  // without a redundant re-fetch just for that.
  let lastTreeEntries = [];
  let previewTimer = null;
  let autosaveTimer = null;
  let savedIndicatorTimer = null;
  // Token for the share link created for the *currently open* note, if any
  // was created this session (there's no "is this note already shared"
  // field on GET /api/notes/{path}, so this is only known once the Share
  // button has actually been clicked - see shareCurrentNote below). Reset
  // whenever the open note changes, since it's meaningless for any other note.
  let currentShareToken = null;

  function isDirty() {
    return currentPath !== null && editorEl.value !== lastSavedContent;
  }

  function setSaveStatus(text, variant) {
    saveStatusEl.textContent = text;
    saveStatusEl.classList.remove('text-slate-400', 'text-emerald-600', 'text-red-600');
    if (variant === 'success') {
      saveStatusEl.classList.add('text-emerald-600');
    } else if (variant === 'error') {
      saveStatusEl.classList.add('text-red-600');
    } else {
      saveStatusEl.classList.add('text-slate-400');
    }
  }

  function scheduleSavedIndicatorClear() {
    clearTimeout(savedIndicatorTimer);
    savedIndicatorTimer = setTimeout(() => {
      if (saveStatusEl.textContent === 'Saved') {
        setSaveStatus('', null);
      }
    }, SAVED_INDICATOR_CLEAR_MS);
  }

  /**
   * Renders the live preview - and, if the open note has task frontmatter
   * (docs/features/tasks-kanban/PLAN.md §5's "Notes integration"), the
   * compact task header above it instead of the raw YAML block. Notes
   * without task frontmatter (including ones that merely *start* with
   * `---`) render exactly as before - `Tasks.extractTaskFrontmatter`
   * returns `null` for those.
   */
  async function renderPreviewNow() {
    const content = editorEl.value;
    const taskInfo = Tasks.extractTaskFrontmatter(content);
    if (taskInfo) {
      Tasks.renderTaskPreviewHeader(taskInfo);
      await MarkdownView.render(previewEl, taskInfo.body);
    } else {
      Tasks.hideTaskPreviewHeader();
      await MarkdownView.render(previewEl, content);
    }
  }

  function schedulePreviewRender() {
    clearTimeout(previewTimer);
    previewTimer = setTimeout(renderPreviewNow, PREVIEW_DEBOUNCE_MS);
  }

  // Saves are serialised: an in-flight autosave PUT followed by a
  // flushAutosave (blur/navigate) used to send a stale `expectedUpdatedAt`
  // and produce a false 409. Every save runs on this chain, reads the
  // *latest* `currentUpdatedAt` and editor content only when it actually
  // starts, and is a no-op if there's nothing left to save.
  let saveChain = Promise.resolve();
  // True while a conflict prompt is unresolved - queued autosaves stay quiet
  // instead of stacking a second dialog on top of the first.
  let conflictPromptOpen = false;

  /**
   * Saves the editor's current content for `currentPath`, if it differs
   * from what was last persisted. Sends `expectedUpdatedAt` (docs/features/
   * tasks-kanban/PLAN.md §3/§4/§5) so a concurrent edit from the Kanban
   * board/modal or MCP is detected instead of silently overwritten; a 409
   * `conflict` response pauses autosave and asks the user how to resolve it
   * (see handleSaveConflict). The prompt runs *outside* the save chain so
   * resolving it (which may reload the note or save again) can't deadlock.
   */
  async function saveCurrentNote(options = {}) {
    const run = saveChain.then(() => performSave(options));
    saveChain = run.then(() => undefined, () => undefined);
    const outcome = await run;
    if (outcome?.conflict) {
      await handleSaveConflict(outcome);
    }
  }

  /** One save attempt. Returns `{ conflict: 'stale' | 'gone', path }` on a 409, else `null`. */
  async function performSave(options) {
    if (!currentPath || !isDirty()) {
      return null;
    }
    if (conflictPromptOpen && !options.force) {
      return null;
    }
    const contentToSave = editorEl.value;
    const savingPath = currentPath;
    setSaveStatus('Saving…', null);
    try {
      const result = await Api.saveNote(
        savingPath,
        contentToSave,
        options.force ? null : currentUpdatedAt,
      );
      if (currentPath === savingPath) {
        lastSavedContent = contentToSave;
        // Keep the header's "Edited <date>" label in sync with every save,
        // not just the initial load (docs/03-FEATURE-SPEC.md's note editor
        // header) - `PUT /api/notes/{path}` returns the fresh `updatedAt`.
        currentUpdatedAt = result?.updatedAt || currentUpdatedAt;
        renderEditedLabel();
      }
      setSaveStatus('Saved', 'success');
      scheduleSavedIndicatorClear();
    } catch (err) {
      if (err.status === 409 && currentPath === savingPath) {
        clearTimeout(autosaveTimer);
        autosaveTimer = null;
        setSaveStatus('Not saved - changed elsewhere', 'error');
        // A 409 with no `currentUpdatedAt` means the file is gone from this
        // path (moved/deleted), not that its content changed.
        return { conflict: err.body?.currentUpdatedAt ? 'stale' : 'gone', path: savingPath };
      }
      setSaveStatus(`Error saving: ${err.message}`, 'error');
    }
    return null;
  }

  async function handleSaveConflict({ conflict, path }) {
    if (conflictPromptOpen) {
      return;
    }
    conflictPromptOpen = true;
    try {
      if (conflict === 'gone') {
        // Never offer to "overwrite" here - that would silently recreate
        // the note at its old path after it was moved/renamed/deleted.
        const closeNote = await Modal.confirm({
          title: 'This note was moved or deleted elsewhere',
          description:
            'The note no longer exists at this path (it was renamed, moved, archived or deleted from the Kanban board, an AI assistant, or another tab), so your latest edits could not be saved. Close the note, or keep this editor open to copy your text out first.',
          confirmLabel: 'Close note',
          cancelLabel: 'Keep editing',
          danger: true,
        });
        if (currentPath !== path) {
          return;
        }
        if (closeNote) {
          lastSavedContent = editorEl.value; // discard: there is nowhere to save it
          await loadTree();
          await navigateHome('');
        }
        return;
      }

      const reload = await Modal.confirm({
        title: 'This note changed elsewhere',
        description:
          'Someone/something else (the Kanban board, an AI assistant, or another tab) saved a newer version of this note. Reload the latest version, or keep yours and overwrite it with your local changes?',
        confirmLabel: 'Reload latest',
        cancelLabel: 'Keep mine (overwrite)',
        danger: false,
      });
      if (!currentPath || currentPath !== path) {
        return; // navigated away while the confirm dialog was open
      }
      conflictPromptOpen = false;
      if (reload) {
        lastSavedContent = editorEl.value; // discard local edits so the reload isn't itself blocked by a dirty check
        await selectFile(path);
      } else {
        await saveCurrentNote({ force: true });
      }
    } finally {
      conflictPromptOpen = false;
    }
  }

  /** Cancels any pending debounced autosave and saves immediately, if dirty. */
  async function flushAutosave() {
    clearTimeout(autosaveTimer);
    autosaveTimer = null;
    await saveCurrentNote();
  }

  function scheduleAutosave() {
    clearTimeout(autosaveTimer);
    autosaveTimer = setTimeout(saveCurrentNote, AUTOSAVE_DEBOUNCE_MS);
  }

  async function loadTree() {
    try {
      const entries = await Api.getTree();
      lastTreeEntries = entries;
      Tree.render(fileTreeEl, entries, {
        onSelectFile: selectFile,
        onMoveRequest: handleMoveRequest,
        onContextMenu: handleTreeContextMenu,
      });
      if (currentPath) {
        Tree.setSelected(fileTreeEl, currentPath);
        // The tree just changed (create/delete/move elsewhere in this
        // folder) - the adjacent-note list backing prev/next may have too.
        updatePrevNextButtons();
      }
      // Keep the Home view's breadcrumb/summary/grid in sync with whatever
      // just changed the tree (create, delete, move/rename) - a no-op
      // while a note is open (home-view stays hidden until navigateHome).
      if (!homeViewEl.classList.contains('hidden')) {
        renderHomeView();
      }
    } catch (err) {
      fileTreeEl.innerHTML = '';
      const message = document.createElement('p');
      message.className = 'px-1 py-2 text-red-600';
      message.textContent = `Failed to load notes: ${err.message}`;
      fileTreeEl.appendChild(message);
    }
  }

  /**
   * Opens `path` in the editor. `keepView: true` loads it into the (hidden)
   * editor without switching away from the Kanban board/list - used when a
   * task-file change (rename/archive/convert) means the editor's file moved
   * while a task view is showing.
   */
  async function selectFile(path, { keepView = false } = {}) {
    // Don't lose in-flight edits to the note we're navigating away from.
    await flushAutosave();
    clearTimeout(previewTimer);

    try {
      // `includeBacklinks: true` is cheap enough to always pass (per the
      // Phase 3 task brief) rather than firing a second request just for
      // the backlinks panel.
      const note = await Api.getNote(path, { includeBacklinks: true });
      currentPath = note.path;
      currentUpdatedAt = note.updatedAt;
      lastSavedContent = note.content;
      editorEl.value = note.content;
      editorEl.disabled = false;
      noteTitleInputEl.value = WikiLinks.getBareTitle(currentPath);
      renderEditedLabel();
      if (!keepView) {
        Tasks.hide();
        homeViewEl.classList.add('hidden');
        noteEditorViewEl.classList.remove('hidden');
      }
      Tree.setSelected(fileTreeEl, currentPath);
      updatePrevNextButtons();
      setSaveStatus('', null);
      renderBacklinksPanel(note.backlinks);
      // A share token only makes sense for the note it was created for -
      // navigating away closes the (per-previous-note) share modal state.
      currentShareToken = null;
      closeShareModal();
      await renderPreviewNow();
    } catch (err) {
      setSaveStatus(`Error loading note: ${err.message}`, 'error');
    }
  }

  /**
   * Called by js/tasks.js around a task-file change (convert / rename /
   * archive / modal save). Before the change, flush the editor if it has
   * this file open so no edit is lost; after it, reload the editor onto
   * `newPath` (the same path when only the content changed) so it never sits
   * on a dead path or stale `updatedAt`.
   */
  async function prepareNoteChange(path) {
    if (path && currentPath === path) {
      await flushAutosave();
    }
  }

  async function followNoteChange(oldPath, newPath) {
    if (!oldPath || !newPath || currentPath !== oldPath) {
      return;
    }
    const editorVisible = !noteEditorViewEl.classList.contains('hidden');
    // Detach from the old path first so selectFile's own flush can't try to
    // PUT to a path that no longer exists (it was flushed by prepareNoteChange).
    currentPath = null;
    await selectFile(newPath, { keepView: !editorVisible });
  }

  /**
   * Returns to the Home / folder-browse view at `folderPath` ('' = vault
   * root) - the "dotNotes" logo, a Home breadcrumb segment, or a folder
   * card all call this. Flushes/clears the currently-open note first (same
   * "don't lose in-flight edits" treatment as selectFile), but only if a
   * note is actually open - clicking a breadcrumb/card while already
   * browsing Home is a same-view navigation, not a leave-the-editor one.
   */
  async function navigateHome(folderPath) {
    if (currentPath) {
      await flushAutosave();
      clearTimeout(previewTimer);
      currentPath = null;
      currentUpdatedAt = null;
      editorEl.value = '';
      editorEl.disabled = true;
      Tree.setSelected(fileTreeEl, null);
    }
    currentBrowseFolder = folderPath || '';
    Tasks.hide();
    noteEditorViewEl.classList.add('hidden');
    homeViewEl.classList.remove('hidden');
    renderHomeView();
  }

  // --- Home / folder-browse view -------------------------------------------
  //
  // docs/03-FEATURE-SPEC.md: "Home / folder-browse view: breadcrumb trail,
  // app name + tagline at the root only, 'X notes, Y folders' summary,
  // folder card grid that navigates on click." The grid shows *both* the
  // subfolders and the notes directly inside currentBrowseFolder - a folder
  // card navigates deeper, a note card opens that note (js/app.js's
  // selectFile), same as picking it from the sidebar tree. (Phase 11/
  // assets/update.txt bug #1: an earlier pass only ever rendered folder
  // cards here, so a folder containing only notes and no subfolders looked
  // empty even though the summary line above it correctly counted them.)

  const ICON_FILE =
    '<svg width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.75" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l4.414 4.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />' +
    '</svg>';

  function pluralize(count, singular) {
    return `${count} ${singular}${count === 1 ? '' : 's'}`;
  }

  /** The direct children of `folderPath` ('' = vault root) within `entries`, or `null` if `folderPath` no longer exists (e.g. it was just renamed/deleted out from under the Home view). */
  function findFolderChildren(entries, folderPath) {
    if (!folderPath) {
      return entries;
    }
    let list = entries;
    for (const segment of folderPath.split('/')) {
      const found = (list || []).find((e) => e.type === 'folder' && e.name === segment);
      if (!found) {
        return null;
      }
      list = found.children || [];
    }
    return list;
  }

  /** `{ notes, folders }` - direct-child counts only (matches the live NoteDiscovery demo's root summary, e.g. "0 notes 6 folders" for a root with 6 folders and no top-level notes). */
  function directCounts(entries) {
    let notes = 0;
    let folders = 0;
    for (const entry of entries || []) {
      if (entry.type === 'file') {
        notes++;
      } else {
        folders++;
      }
    }
    return { notes, folders };
  }

  /** Total note count nested anywhere inside `entries`, recursively - used for each folder card's "N notes" line (unlike directCounts above, which is only for the summary bar of the folder currently being browsed). */
  function countNotesRecursive(entries) {
    let count = 0;
    for (const entry of entries || []) {
      count += entry.type === 'file' ? 1 : countNotesRecursive(entry.children);
    }
    return count;
  }

  function renderBreadcrumb(folderPath) {
    homeBreadcrumbEl.innerHTML = '';
    const segments = [{ label: 'Home', path: '' }];
    if (folderPath) {
      let cumulative = '';
      for (const part of folderPath.split('/')) {
        cumulative = cumulative ? `${cumulative}/${part}` : part;
        segments.push({ label: part, path: cumulative });
      }
    }

    segments.forEach((segment, index) => {
      if (index > 0) {
        const separator = document.createElement('span');
        separator.className = 'home-breadcrumb-separator';
        separator.textContent = '›';
        separator.setAttribute('aria-hidden', 'true');
        homeBreadcrumbEl.appendChild(separator);
      }

      const isCurrent = index === segments.length - 1;
      if (isCurrent) {
        const current = document.createElement('span');
        current.className = 'home-breadcrumb-current';
        current.textContent = segment.label;
        current.setAttribute('aria-current', 'location');
        homeBreadcrumbEl.appendChild(current);
      } else {
        const link = document.createElement('button');
        link.type = 'button';
        link.className = 'home-breadcrumb-link';
        link.textContent = segment.label;
        link.addEventListener('click', () => navigateHome(segment.path));
        homeBreadcrumbEl.appendChild(link);
      }
    });
  }

  const ICON_FOLDER =
    '<svg width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.75" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M2.25 12.75V12A2.25 2.25 0 014.5 9.75h15A2.25 2.25 0 0121.75 12v.75m-19.5 0v6a2.25 2.25 0 002.25 2.25h15a2.25 2.25 0 002.25-2.25v-6m-19.5 0V6A2.25 2.25 0 014.5 3.75h4.879a1.5 1.5 0 011.06.44l2.122 2.12a1.5 1.5 0 001.06.44H19.5A2.25 2.25 0 0121.75 9v3.75" />' +
    '</svg>';
  const ICON_CHEVRON_RIGHT =
    '<svg width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M8.25 4.5l7.5 7.5-7.5 7.5" /></svg>';

  function alphabetical(entries) {
    return [...entries].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
  }

  function buildFolderCard(folder) {
    const card = document.createElement('button');
    card.type = 'button';
    card.className = 'home-folder-card';

    const top = document.createElement('div');
    top.className = 'home-folder-card-top';
    const icon = document.createElement('span');
    icon.className = 'home-folder-card-icon';
    icon.innerHTML = ICON_FOLDER;
    const name = document.createElement('span');
    name.className = 'home-folder-card-name';
    name.textContent = folder.name;
    top.append(icon, name);

    const bottom = document.createElement('div');
    bottom.className = 'home-folder-card-bottom';
    const count = document.createElement('span');
    count.textContent = pluralize(countNotesRecursive(folder.children), 'note');
    const chevron = document.createElement('span');
    chevron.className = 'home-folder-card-chevron';
    chevron.innerHTML = ICON_CHEVRON_RIGHT;
    bottom.append(count, chevron);

    card.append(top, bottom);
    card.addEventListener('click', () => navigateHome(folder.path));
    return card;
  }

  /** A note card - same visual family as a folder card, but opens the note (js/app.js's selectFile) instead of navigating deeper. */
  function buildNoteCard(note) {
    const card = document.createElement('button');
    card.type = 'button';
    card.className = 'home-folder-card home-note-card';

    const top = document.createElement('div');
    top.className = 'home-folder-card-top';
    const icon = document.createElement('span');
    icon.className = 'home-folder-card-icon';
    icon.innerHTML = ICON_FILE;
    const name = document.createElement('span');
    name.className = 'home-folder-card-name';
    name.textContent = note.name.replace(/\.md$/i, '');
    top.append(icon, name);

    card.append(top);
    card.addEventListener('click', () => selectFile(note.path));
    return card;
  }

  /** True empty-state only when `folderPath` has zero notes *and* zero subfolders (Phase 11 bug #1 - a folder with only notes and no subfolders used to render as if empty). */
  function renderFolderGrid(children) {
    homeFolderGridEl.innerHTML = '';
    const folders = alphabetical((children || []).filter((entry) => entry.type === 'folder'));
    const notes = alphabetical((children || []).filter((entry) => entry.type === 'file'));

    if (folders.length === 0 && notes.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'home-grid-empty';
      empty.textContent = currentBrowseFolder
        ? 'No notes or subfolders here yet. Use "+ New" above to create one.'
        : 'No notes or folders yet. Use "+ New" above to create one.';
      homeFolderGridEl.appendChild(empty);
      return;
    }

    for (const folder of folders) {
      homeFolderGridEl.appendChild(buildFolderCard(folder));
    }
    for (const note of notes) {
      homeFolderGridEl.appendChild(buildNoteCard(note));
    }
  }

  function renderHomeView() {
    let children = findFolderChildren(lastTreeEntries, currentBrowseFolder);
    if (children === null) {
      // The browsed folder was renamed/moved/deleted out from under us -
      // fall back to the root rather than showing a stale/broken view.
      currentBrowseFolder = '';
      children = lastTreeEntries;
    }

    homeAppTitleEl.classList.toggle('hidden', currentBrowseFolder !== '');
    renderBreadcrumb(currentBrowseFolder);

    const { notes, folders } = directCounts(children);
    homeSummaryTextEl.innerHTML =
      `<strong>${notes}</strong> ${notes === 1 ? 'note' : 'notes'} &nbsp; <strong>${folders}</strong> ${folders === 1 ? 'folder' : 'folders'}`;

    renderFolderGrid(children);
  }

  // --- backlinks panel -----------------------------------------------------

  /** Renders the current note's `backlinks` (`[{ path, title }]`, or null/empty) as clickable pills. */
  function renderBacklinksPanel(backlinks) {
    backlinksListEl.innerHTML = '';

    if (!backlinks || backlinks.length === 0) {
      const empty = document.createElement('span');
      empty.className = 'text-slate-400';
      empty.textContent = 'No backlinks yet.';
      backlinksListEl.appendChild(empty);
      return;
    }

    for (const link of backlinks) {
      const pill = document.createElement('button');
      pill.type = 'button';
      pill.className =
        'rounded-full bg-slate-100 px-2 py-0.5 text-slate-700 hover:bg-slate-200 hover:text-slate-900';
      pill.textContent = link.title;
      pill.title = link.path;
      pill.addEventListener('click', () => selectFile(link.path));
      backlinksListEl.appendChild(pill);
    }
  }

  /**
   * Re-fetches backlinks for the currently open note. The backend's index
   * itself updates live (docs/06-DATA-MODEL.md's FileSystemWatcher), but
   * this UI only re-renders the panel when asked to (button click) or when
   * navigating to a note (selectFile above) - see the "Refresh" button's
   * title text and this phase's task-brief write-up for why that manual
   * step is an acceptable trade-off for a personal, single-user tool.
   */
  async function refreshBacklinksPanel() {
    if (!currentPath) {
      return;
    }
    try {
      const note = await Api.getNote(currentPath, { includeBacklinks: true });
      renderBacklinksPanel(note.backlinks);
    } catch (err) {
      console.error('Failed to refresh backlinks', err);
    }
  }

  /**
   * Trims/normalizes a user-typed vault-relative path (backslashes -> `/`,
   * no leading or trailing slashes). Shared by the "+ New note"/"+ New
   * folder" prompts and the rename/move prompt below. Returns null for
   * effectively-empty input.
   */
  function normalizeVaultRelativePath(rawInput) {
    const path = rawInput.trim().replace(/\\/g, '/').replace(/^\/+/, '').replace(/\/+$/, '');
    return path || null;
  }

  function normalizeNewNotePath(rawInput) {
    const path = normalizeVaultRelativePath(rawInput);
    if (!path) {
      return null;
    }
    return /\.md$/i.test(path) ? path : `${path}.md`;
  }

  function normalizeNewFolderPath(rawInput) {
    return normalizeVaultRelativePath(rawInput);
  }

  /**
   * Creates an empty note at `path` (if it doesn't already exist - PUT is
   * an upsert, per docs/04-API-SPEC.md) and navigates the editor to it.
   * Shared by the manual "+ New note" button and the wikilink
   * click-to-create flow below, so both paths stay in sync.
   */
  async function createNoteAtPath(path) {
    await flushAutosave();
    await Api.saveNote(path, '');
    Tree.expandAncestorsOf(path);
    await loadTree();
    await selectFile(path);
    // A newly-created note changes what wikilinks resolve to (elsewhere
    // in the vault, a previously-"missing" link may now resolve to it).
    await WikiLinks.refresh();
  }

  /**
   * `defaultFolder`, when given (the Home view's own "+New" - see
   * home-new-btn's wiring below), pre-fills the prompt with that folder so
   * "+New" from inside a browsed folder actually creates there instead of
   * requiring the full path to be retyped. The sidebar's "+New" (unchanged)
   * calls this with no argument, exactly as before this phase.
   */
  async function createNewNote(defaultFolder) {
    const path = await Modal.prompt({
      title: 'New Note',
      description: 'Enter a path for the new note, relative to the vault root.',
      label: 'Path',
      initialValue: defaultFolder ? `${defaultFolder}/` : '',
      confirmLabel: 'Create',
      validate: (raw) => {
        const normalized = normalizeNewNotePath(raw);
        return normalized ? { ok: true, value: normalized } : { ok: false, message: 'Enter a path for the note.' };
      },
    });
    if (path === null) {
      return; // user cancelled
    }

    try {
      await createNoteAtPath(path);
    } catch (err) {
      window.alert(`Could not create note "${path}": ${err.message}`);
    }
  }

  // --- "+ New folder" (docs/04-API-SPEC.md's `POST /api/folders/{path}`) --

  /**
   * Creates `path` (and any missing parent folders - the endpoint is
   * `mkdir -p`-style and idempotent) and expands the tree down to it so
   * the new, empty folder is immediately visible rather than looking like
   * nothing happened.
   */
  /** `defaultFolder`: same pre-fill treatment as createNewNote above. */
  async function createNewFolder(defaultFolder) {
    const path = await Modal.prompt({
      title: 'New Folder',
      description: 'Enter a path for the new folder, relative to the vault root.',
      label: 'Path',
      initialValue: defaultFolder ? `${defaultFolder}/` : '',
      confirmLabel: 'Create',
      validate: (raw) => {
        const normalized = normalizeNewFolderPath(raw);
        return normalized ? { ok: true, value: normalized } : { ok: false, message: 'Enter a path for the folder.' };
      },
    });
    if (path === null) {
      return; // user cancelled
    }

    try {
      await Api.createFolder(path);
      Tree.expandFolder(path);
      await loadTree();
    } catch (err) {
      window.alert(`Could not create folder "${path}": ${err.message}`);
    }
  }

  // --- rename/move a note or folder (drag-and-drop, right-click menu, or
  //     the Menu-key/Shift+F10 keyboard fallback - js/tree.js) -----------
  //
  // docs/03-FEATURE-SPEC.md: "Sidebar drag-and-drop ..." and "Rename notes
  // and folders...". All three entry points below (drag-drop, "Move to...",
  // Rename) funnel into the one shared `performMove` routine, per this
  // phase's task brief ("One post-move routine").

  function parentFolderOf(path) {
    const idx = path.lastIndexOf('/');
    return idx >= 0 ? path.slice(0, idx) : '';
  }

  // docs/06-DATA-MODEL.md's "Names" section, mirrored here as fast client-
  // side feedback only - the server is still the authority and re-validates
  // (host-OS-specific invalid characters in particular can't be fully
  // replicated in JS, e.g. `Path.GetInvalidFileNameChars()` differs by OS).
  const INVALID_NAME_PATTERN = /[\x00-\x1f[\]|<>:"/\\?*]/;

  function validateEntryName(rawInput) {
    const name = (rawInput || '').trim();
    if (!name) {
      return { ok: false, message: 'Name cannot be empty.' };
    }
    if (INVALID_NAME_PATTERN.test(name)) {
      return { ok: false, message: 'Name cannot contain [, ], |, <, >, :, ", /, \\, ?, *, or control characters.' };
    }
    return { ok: true, name };
  }

  /**
   * The one shared post-move routine (drag-drop, "Move to...", and Rename
   * all call this once they've each worked out `destinationPath`):
   * flush any in-flight autosave, call the matching move endpoint, surface
   * any error with the backend's own message, then bring the tree/wikilink
   * cache/open note up to date with the result.
   */
  async function performMove(entry, destinationPath) {
    if (destinationPath === entry.path) {
      return; // no-op
    }

    // Don't lose in-flight edits, and don't let a debounced autosave fire
    // against the *old* path after it's gone (that PUT would just recreate
    // the note there instead of erroring, silently undoing the move).
    await flushAutosave();

    // If the note being moved is the one currently open (directly, or by
    // being inside a folder that's moving), remember its new path so the
    // editor can follow it there afterward instead of being left pointing
    // at a path that no longer exists.
    let reopenPath = null;
    if (entry.type === 'file' && currentPath === entry.path) {
      reopenPath = destinationPath;
    } else if (entry.type === 'folder' && currentPath && currentPath.startsWith(`${entry.path}/`)) {
      reopenPath = destinationPath + currentPath.slice(entry.path.length);
    }

    let result;
    try {
      result = entry.type === 'folder'
        ? await Api.moveFolder(entry.path, destinationPath)
        : await Api.moveNote(entry.path, destinationPath);
    } catch (err) {
      // Surfaces the backend's exact wording for the documented error
      // cases (docs/04-API-SPEC.md): 404 source missing, 409 destination
      // already exists, 400 invalid path/name or moving a folder into its
      // own descendant, 503 vault unavailable.
      window.alert(`Could not move "${entry.path}" to "${destinationPath}": ${err.message}`);
      return;
    }

    if (entry.type === 'folder') {
      Tree.expandFolder(destinationPath);
    } else {
      Tree.expandAncestorsOf(destinationPath);
    }
    await loadTree();
    // Paths changed under wikilinks.js's resolution cache (docs/06-DATA-
    // MODEL.md) - a link that resolved to the old path would otherwise
    // show as broken (or vice versa) until the next full refresh.
    await WikiLinks.refresh();

    if (reopenPath) {
      await selectFile(reopenPath);
      return;
    }

    // Both move endpoints return a `rewrittenNotes: [path]` field
    // (docs/04-API-SPEC.md) listing every *other* note whose incoming
    // wikilinks got rewritten by this move/rename. If the currently-open
    // note is in that list, its on-disk content just changed out from
    // under the editor's buffer (VaultReorganizationService rewrote it via
    // SaveAsync); reload it so a later autosave can't clobber the rewrite.
    // (The moved note *itself* needs no such check here: when it's the
    // note that was open, `reopenPath` above already re-fetches it fresh
    // via selectFile, which picks up any self-link rewrite the same way.)
    // Still guarded with `Array.isArray` - cheap, and keeps this correct
    // even against an older cached frontend/backend pairing mid-deploy.
    const rewrittenNotes = result?.rewrittenNotes;
    if (currentPath && Array.isArray(rewrittenNotes) && rewrittenNotes.includes(currentPath)) {
      await selectFile(currentPath);
    }
  }

  /**
   * Shared by drag-and-drop (js/tree.js's `onMoveRequest`) and the "Move
   * to..." menu item below: moves `entry` to be a direct child of
   * `targetFolderPath` (`''` for the vault root), after refusing the two
   * cases that make no sense to send to the server at all - dropping/
   * choosing the item's current parent (a no-op) and, for a folder, moving
   * it into itself or one of its own descendants (which the backend would
   * reject anyway, but there's no reason to round-trip for it).
   */
  async function handleMoveRequest(entry, targetFolderPath) {
    if (targetFolderPath === parentFolderOf(entry.path)) {
      return; // no-op: already there
    }
    if (entry.type === 'folder' && (targetFolderPath === entry.path || targetFolderPath.startsWith(`${entry.path}/`))) {
      window.alert(`Can't move "${entry.name}" into itself or one of its own subfolders.`);
      return;
    }
    const destinationPath = targetFolderPath ? `${targetFolderPath}/${entry.name}` : entry.name;
    await performMove(entry, destinationPath);
  }

  // --- "Move to..." folder picker (context menu) --------------------------

  /**
   * Every folder path `entry` could sensibly be moved into: every folder in
   * the vault except its current parent (a no-op) and, if `entry` is itself
   * a folder, itself and any of its own descendants. `'(vault root)'` is
   * offered first unless `entry` is already at the root.
   */
  function buildMoveToOptions(entry) {
    const currentParent = parentFolderOf(entry.path);
    const options = [];
    if (currentParent !== '') {
      options.push({ label: '(vault root)', destination: '' });
    }
    for (const folderPath of Tree.listFolderPaths(lastTreeEntries)) {
      if (folderPath === currentParent) {
        continue;
      }
      if (entry.type === 'folder' && (folderPath === entry.path || folderPath.startsWith(`${entry.path}/`))) {
        continue;
      }
      options.push({ label: folderPath, destination: folderPath });
    }
    return options;
  }

  function openMoveToMenu(entry, anchorEl) {
    const options = buildMoveToOptions(entry);
    if (options.length === 0) {
      window.alert(`There's no other folder to move "${entry.name}" to yet.`);
      return;
    }
    const rect = anchorEl.getBoundingClientRect();
    Menu.open({
      x: rect.right,
      y: rect.top,
      id: 'move-to-menu',
      ariaLabel: `Move "${entry.name}" to`,
      returnFocusEl: anchorEl,
      items: options.map((option) => ({
        label: option.label,
        onSelect: () => handleMoveRequest(entry, option.destination),
      })),
    });
  }

  // --- reusable themed modal (docs/03-FEATURE-SPEC.md: "In-app themed
  // modal ... replacing native prompt()/confirm() for New Note, New Folder,
  // Rename, and delete confirmations") ---------------------------------
  //
  // One promise-based component built on the single #rename-modal element
  // (index.html - kept under that name since it's the same markup this
  // started as, just generalized). `Modal.prompt()` is the `window.prompt()`
  // replacement: it shows the labeled input, resolves to the validated
  // value on Create/Rename, or `null` on Cancel/Escape. `Modal.confirm()` is
  // the `window.confirm()` replacement: it hides the input entirely and
  // resolves to `true`/`false`. Every other new-note/new-folder/rename/
  // delete flow below goes through one of these two - no second prompt/
  // confirm implementation anywhere in this file.
  const Modal = (() => {
    let resolveFn = null;
    let validateFn = null;

    function isPromptMode() {
      return !renameModalFieldEl.classList.contains('hidden');
    }

    function open({ title, description, showInput, label, initialValue, confirmLabel, cancelLabel, danger, validate }) {
      renameModalTitleEl.textContent = title;
      renameModalDescriptionEl.textContent = description || '';
      renameModalDescriptionEl.classList.toggle('hidden', !description);
      renameModalFieldEl.classList.toggle('hidden', !showInput);
      renameModalLabelEl.textContent = label || 'Name';
      renameModalInputEl.value = initialValue || '';
      renameModalErrorEl.classList.add('hidden');
      renameModalConfirmBtn.textContent = confirmLabel || 'OK';
      renameModalCancelBtn.textContent = cancelLabel || 'Cancel';
      renameModalConfirmBtn.classList.toggle('mini-modal-btn-danger', !!danger);
      validateFn = validate || null;
      renameModalEl.classList.remove('hidden');
      if (showInput) {
        renameModalInputEl.focus();
        renameModalInputEl.select();
      } else {
        renameModalConfirmBtn.focus();
      }
      return new Promise((resolve) => {
        resolveFn = resolve;
      });
    }

    function close(result) {
      renameModalEl.classList.add('hidden');
      renameModalConfirmBtn.classList.remove('mini-modal-btn-danger');
      const resolve = resolveFn;
      resolveFn = null;
      validateFn = null;
      if (resolve) {
        resolve(result);
      }
    }

    function handleConfirm() {
      if (!isPromptMode()) {
        close(true);
        return;
      }
      const raw = renameModalInputEl.value;
      if (validateFn) {
        const result = validateFn(raw);
        if (!result.ok) {
          renameModalErrorEl.textContent = result.message;
          renameModalErrorEl.classList.remove('hidden');
          renameModalInputEl.focus();
          return;
        }
        close(result.value);
        return;
      }
      close(raw);
    }

    function handleCancel() {
      close(isPromptMode() ? null : false);
    }

    renameModalConfirmBtn.addEventListener('click', handleConfirm);
    renameModalCancelBtn.addEventListener('click', handleCancel);
    renameModalInputEl.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        handleConfirm();
      } else if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation(); // never also close a modal underneath (e.g. the task modal)
        handleCancel();
      }
    });
    // Confirm-only mode (no input, no keydown target to catch Escape) -
    // catch it at the dialog level instead.
    renameModalEl.addEventListener('keydown', (event) => {
      if (event.key === 'Escape' && !isPromptMode()) {
        event.preventDefault();
        event.stopPropagation();
        handleCancel();
      }
    });

    return {
      isOpen: () => !renameModalEl.classList.contains('hidden'),
      /** Resolves to the validated string, or `null` if cancelled. `validate(raw) => { ok: true, value } | { ok: false, message }`. */
      prompt: (opts) => open({ ...opts, showInput: true, confirmLabel: opts.confirmLabel || 'OK' }),
      /** Resolves to `true`/`false`. */
      confirm: (opts) => open({ ...opts, showInput: false, confirmLabel: opts.confirmLabel || 'OK' }),
    };
  })();

  // js/tasks.js (loaded before this file, but its own IIFE has no reach into
  // app.js's closure) reuses this same themed modal for its Archive
  // confirmation and the "note changed elsewhere" conflict prompt below,
  // rather than a second confirm/prompt implementation - see this phase's
  // task brief ("reuse Modal.confirm").
  window.Modal = Modal;

  // --- rename (notes and folders) ------------------------------------------

  /** The name shown/edited in the rename modal for `entry` - a note's `.md` extension is never shown or typed. */
  function displayNameOf(entry) {
    return entry.type === 'file' ? entry.name.replace(/\.md$/i, '') : entry.name;
  }

  async function openRenameModal(entry) {
    const displayName = displayNameOf(entry);
    const newName = await Modal.prompt({
      title: entry.type === 'folder' ? 'Rename folder' : 'Rename note',
      description: `Enter a new name for "${displayName}".`,
      label: 'New name',
      initialValue: displayName,
      confirmLabel: 'Rename',
      validate: (raw) => {
        const validated = validateEntryName(raw);
        return validated.ok ? { ok: true, value: validated.name } : { ok: false, message: validated.message };
      },
    });
    if (newName === null) {
      return; // cancelled
    }

    let finalName = newName;
    if (entry.type === 'file' && !/\.md$/i.test(finalName)) {
      finalName += '.md';
    }
    const parent = parentFolderOf(entry.path);
    const destinationPath = parent ? `${parent}/${finalName}` : finalName;
    await performMove(entry, destinationPath);
  }

  // --- tree row context menu (right-click, or Menu key/Shift+F10) ---------

  const ICON_RENAME =
    '<svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />' +
    '</svg>';
  const ICON_MOVE_TO =
    '<svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v7a2 2 0 01-2 2H5a2 2 0 01-2-2V7z" />' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M11 12h6m0 0l-2-2m2 2l-2 2" />' +
    '</svg>';

  const ICON_DELETE =
    '<svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />' +
    '</svg>';

  /**
   * Deletes `entry` after a confirmation prompt: a note via
   * `DELETE /api/notes/{path}`, a folder via `DELETE /api/folders/{path}`
   * (recursive - the folder and everything inside it, per
   * docs/04-API-SPEC.md). The backend is the authority on what's legal to
   * delete (it refuses anything that would escape the vault root, and the
   * vault root itself); this only asks, calls, and reports.
   */
  async function deleteEntry(entry) {
    const isFolder = entry.type === 'folder';
    const confirmed = await Modal.confirm({
      title: isFolder ? 'Delete folder' : 'Delete note',
      description: isFolder
        ? `Delete "${entry.path}" and everything inside it? This can't be undone.`
        : `Delete "${entry.path}"? This can't be undone.`,
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!confirmed) {
      return;
    }

    // If the open note is the one being deleted (or lives anywhere inside
    // the folder being deleted), drop it *before* the request: navigateHome
    // below flushes any dirty autosave first, and a flush against a
    // just-deleted path would PUT the editor's buffer straight back to
    // disk, silently undoing the delete. Same reasoning as
    // deleteCurrentNote's own comment.
    const wasOpen = !!currentPath
      && (isFolder ? currentPath.startsWith(`${entry.path}/`) : currentPath === entry.path);
    if (wasOpen) {
      clearTimeout(autosaveTimer);
      currentPath = null;
    }

    try {
      if (isFolder) {
        await Api.deleteFolder(entry.path);
      } else {
        await Api.deleteNote(entry.path);
      }
    } catch (err) {
      // Surfaces the backend's own wording for the documented cases
      // (docs/04-API-SPEC.md): 404 the folder no longer exists, 400 an
      // invalid path, 503 vault unavailable.
      if (wasOpen) {
        currentPath = entry.path; // nothing was deleted - the note is still open
      }
      window.alert(`Could not delete "${entry.path}": ${err.message}`);
      return;
    }

    await loadTree();
    await WikiLinks.refresh();

    if (wasOpen) {
      editorEl.value = '';
      editorEl.disabled = true;
      Tree.setSelected(fileTreeEl, null);
      await navigateHome(parentFolderOf(entry.path));
      return;
    }

    // Not the open note, but the Home view could still be browsing the
    // folder that just disappeared (or one inside it) - fall back to its
    // nearest surviving ancestor rather than leaving an empty view.
    if (isFolder
      && !homeViewEl.classList.contains('hidden')
      && (currentBrowseFolder === entry.path || currentBrowseFolder.startsWith(`${entry.path}/`))) {
      await navigateHome(parentFolderOf(entry.path));
    }
  }

  const ICON_CONVERT_TO_TASK =
    '<svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M9 12.75L11.25 15 15 9.75M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />' +
    '</svg>';

  function handleTreeContextMenu(entry, x, y, anchorEl) {
    const items = [
      { id: 'tree-context-rename', label: 'Rename', icon: ICON_RENAME, onSelect: () => openRenameModal(entry) },
      { id: 'tree-context-move-to', label: 'Move to…', icon: ICON_MOVE_TO, onSelect: () => openMoveToMenu(entry, anchorEl) },
    ];
    // "Convert to task" (docs/features/tasks-kanban/PLAN.md §5's "Notes
    // integration") - notes only, and not for a note that's already a task.
    if (entry.type === 'file' && !Tasks.isTaskPath(entry.path)) {
      items.push({
        id: 'tree-context-convert-to-task',
        label: 'Convert to task',
        icon: ICON_CONVERT_TO_TASK,
        onSelect: () => Tasks.convertNote(entry.path),
      });
    }
    items.push({ id: 'tree-context-delete', label: 'Delete', icon: ICON_DELETE, danger: true, onSelect: () => deleteEntry(entry) });

    Menu.open({
      x,
      y,
      id: 'tree-context-menu',
      ariaLabel: `Actions for "${entry.name}"`,
      returnFocusEl: anchorEl,
      items,
    });
  }

  Tree.wireRootDropZone(treeRootDropZoneEl, treeRootDropLabelEl);

  // --- clickable [[wikilinks]] in the rendered preview ---------------------

  /**
   * Default location for a newly-created note reached via an unresolved
   * `[[wikilink]]` click: if the raw link text already names a folder
   * (e.g. `[[projects/idea]]`), honour it as-is (same as
   * WikiLinkResolver.NormalizeToNotePath). For a bare title with no folder
   * (e.g. `[[idea]]`), this codebase's chosen rule - documented here per
   * docs/06-DATA-MODEL.md's "pick one and document it" - is to create the
   * new note *alongside the note containing the link*, not at the vault
   * root: a personal wiki's bare links usually mean "a sibling note near
   * this one", and grouping related notes together in the same folder is
   * more useful day-to-day than dumping every quick link at the root.
   */
  function defaultCreatePathFor(rawTarget, sourceNotePath) {
    const normalized = WikiLinks.normalizeToNotePath(rawTarget);
    const hasExplicitFolder = rawTarget.trim().replace(/\\/g, '/').includes('/');
    if (hasExplicitFolder) {
      return normalized;
    }
    const lastSlash = sourceNotePath.lastIndexOf('/');
    const sourceFolder = lastSlash >= 0 ? sourceNotePath.slice(0, lastSlash) : '';
    return sourceFolder ? `${sourceFolder}/${normalized}` : normalized;
  }

  /** Handles a click on a rendered `<a class="wikilink">` element. */
  async function handleWikilinkClick(anchor) {
    const rawTarget = anchor.dataset.wikilinkTarget;
    if (!rawTarget || !currentPath) {
      return;
    }

    // Re-resolve at click time rather than trusting the render-time
    // classification - the graph cache (and the vault itself) may have
    // changed since this preview was last rendered.
    const resolution = WikiLinks.resolve(rawTarget);
    if (resolution.exists) {
      await selectFile(resolution.path);
      return;
    }

    const createPath = defaultCreatePathFor(rawTarget, currentPath);
    const confirmed = await Modal.confirm({
      title: 'Create note?',
      description: `Note "${createPath}" doesn't exist yet. Create it?`,
      confirmLabel: 'Create',
    });
    if (!confirmed) {
      return;
    }
    try {
      await createNoteAtPath(createPath);
    } catch (err) {
      window.alert(`Could not create note "${createPath}": ${err.message}`);
    }
  }

  // --- editor -> preview + autosave -----------------------------------

  editorEl.addEventListener('input', () => {
    schedulePreviewRender();
    scheduleAutosave();
  });

  // --- interactive checkboxes in the rendered preview -------------------

  previewEl.addEventListener('click', (event) => {
    const wikilinkAnchor = event.target.closest('a.wikilink');
    if (wikilinkAnchor) {
      event.preventDefault(); // it's a real <a>, but only ever used for in-app navigation/creation.
      handleWikilinkClick(wikilinkAnchor);
      return;
    }

    const target = event.target;
    if (!(target instanceof HTMLInputElement) || !target.classList.contains('task-checkbox')) {
      return;
    }
    if (!currentPath) {
      return;
    }

    const checkboxIndex = Number(target.dataset.checkboxIndex);
    const updatedContent = MarkdownView.toggleCheckbox(editorEl.value, checkboxIndex);
    if (updatedContent === editorEl.value) {
      return; // couldn't find a matching checkbox line - leave everything as-is
    }

    editorEl.value = updatedContent;
    clearTimeout(previewTimer);
    renderPreviewNow();

    // A deliberate toggle click saves right away rather than waiting out
    // the typing debounce - it's a discrete action, not a keystroke.
    clearTimeout(autosaveTimer);
    saveCurrentNote();
  });

  // --- note editor header (docs/03-FEATURE-SPEC.md: "Note editor header:
  // inline title, undo/redo, delete, 'Edited <date>' ...") ----------------

  /** Formats `isoString` (UTC, docs/04-API-SPEC.md: "Timestamps: ISO 8601 UTC") as "Edited <date>" in the browser's local timezone/locale. */
  function formatEditedLabel(isoString) {
    if (!isoString) {
      return '';
    }
    const date = new Date(isoString);
    if (Number.isNaN(date.getTime())) {
      return '';
    }
    return `Edited ${date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}`;
  }

  function renderEditedLabel() {
    noteEditedLabelEl.textContent = formatEditedLabel(currentUpdatedAt);
  }

  // --- inline title = rename ------------------------------------------
  //
  // Funnels into the exact same `performMove` the sidebar's right-click
  // "Rename" uses (below) - not a second, parallel rename implementation,
  // per this phase's task brief. Committing (blur, or Enter) with an
  // unchanged or invalid name is a no-op (an invalid one shows the same
  // validation message the rename modal would); Escape reverts without
  // committing.

  async function commitTitleRename() {
    if (!currentPath) {
      return;
    }
    const validated = validateEntryName(noteTitleInputEl.value);
    const currentTitle = WikiLinks.getBareTitle(currentPath);
    if (!validated.ok) {
      window.alert(validated.message);
    } else if (validated.name !== currentTitle) {
      // A task note's title lives in its frontmatter too, so renaming it
      // has to go through the task API (docs/features/tasks-kanban/PLAN.md
      // §5) rather than the plain move/rename flow - it renames the file
      // *and* rewrites `title:`, keeping both in sync.
      if (Tasks.isTaskPath(currentPath)) {
        try {
          await flushAutosave();
          const result = await Tasks.renameTaskTitle(currentPath, validated.name);
          await loadTree();
          await selectFile(result.path);
        } catch (err) {
          window.alert(`Could not rename this task: ${err.message}`);
        }
      } else {
        const entry = { path: currentPath, name: currentPath.slice(currentPath.lastIndexOf('/') + 1), type: 'file' };
        const parent = parentFolderOf(currentPath);
        const destinationPath = parent ? `${parent}/${validated.name}.md` : `${validated.name}.md`;
        await performMove(entry, destinationPath);
      }
    }
    // Whichever branch above ran (renamed, rejected, or a no-op),
    // re-sync the input with whatever `currentPath` actually is now -
    // performMove already reloaded the note via selectFile on success.
    if (currentPath) {
      noteTitleInputEl.value = WikiLinks.getBareTitle(currentPath);
    }
  }

  noteTitleInputEl.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      noteTitleInputEl.blur(); // the 'blur' handler below is the one commit path
    } else if (event.key === 'Escape') {
      event.preventDefault();
      if (currentPath) {
        noteTitleInputEl.value = WikiLinks.getBareTitle(currentPath);
      }
      noteTitleInputEl.blur();
    }
  });
  noteTitleInputEl.addEventListener('blur', commitTitleRename);

  // --- previous/next-note navigation ---------------------------------------
  //
  // docs/03-FEATURE-SPEC.md's note editor header: steps to the adjacent note
  // within the *current note's folder*, in the sidebar's current display
  // order (js/tree.js's getSortedChildren - the one source of truth for
  // folder sort order, per this phase's task brief: "don't duplicate sort
  // logic"). Disabled at the first/last note in that folder. This is plain
  // previous/next navigation, not undo/redo - a mislabel carried over from
  // the NoteDiscovery reference in Phase 10 (assets/update.txt) that these
  // two buttons (and their handlers) replace outright; native browser
  // undo/redo (ctrl+Z) in the textarea needs no JS at all and is untouched.

  /** The notes (only - not subfolders) directly inside the current note's folder, in the sidebar's active display order. */
  function currentFolderNotesInOrder() {
    if (!currentPath) {
      return [];
    }
    return Tree.getSortedChildren(parentFolderOf(currentPath)).filter((entry) => entry.type === 'file');
  }

  function updatePrevNextButtons() {
    const notes = currentFolderNotesInOrder();
    const index = notes.findIndex((entry) => entry.path === currentPath);
    prevNoteBtn.disabled = index <= 0;
    nextNoteBtn.disabled = index === -1 || index >= notes.length - 1;
  }

  async function navigateToAdjacentNote(offset) {
    const notes = currentFolderNotesInOrder();
    const index = notes.findIndex((entry) => entry.path === currentPath);
    const target = index === -1 ? null : notes[index + offset];
    if (target) {
      await selectFile(target.path);
    }
  }

  prevNoteBtn.addEventListener('click', () => navigateToAdjacentNote(-1));
  nextNoteBtn.addEventListener('click', () => navigateToAdjacentNote(1));

  // --- delete ---------------------------------------------------------

  async function deleteCurrentNote() {
    if (!currentPath) {
      return;
    }
    const deletedPath = currentPath;
    const confirmed = await Modal.confirm({
      title: 'Delete note',
      description: `Delete "${deletedPath}"? This can't be undone.`,
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!confirmed) {
      return;
    }
    clearTimeout(autosaveTimer);
    // Clear currentPath *before* the DELETE call: navigateHome (below)
    // flushes any dirty autosave first, and if currentPath still pointed at
    // the just-deleted note, that flush would PUT the editor's buffer right
    // back to disk, silently undoing the delete.
    currentPath = null;
    try {
      await Api.deleteNote(deletedPath);
    } catch (err) {
      currentPath = deletedPath; // deletion failed - the note is still open
      window.alert(`Could not delete "${deletedPath}": ${err.message}`);
      return;
    }
    editorEl.value = '';
    editorEl.disabled = true;
    Tree.setSelected(fileTreeEl, null);
    await loadTree();
    await WikiLinks.refresh();
    await navigateHome(parentFolderOf(deletedPath));
  }

  deleteNoteBtn.addEventListener('click', deleteCurrentNote);

  // --- export / print / copy-link / fullscreen -----------------------
  //
  // None of these need a backend endpoint. Share (below) reuses the
  // existing #share-modal/shareCurrentNote flow unchanged.

  function exportCurrentNote() {
    if (!currentPath) {
      return;
    }
    const blob = new Blob([editorEl.value], { type: 'text/markdown' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = currentPath.slice(currentPath.lastIndexOf('/') + 1);
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  }

  function printCurrentNote() {
    if (!currentPath) {
      return;
    }
    window.print(); // css/app.css's `@media print` isolates #preview
  }

  /** `navigator.clipboard.writeText`, falling back to the same hidden-textarea + `execCommand('copy')` trick as the share modal's Copy button, for browsers/contexts where the Clipboard API isn't available. */
  async function copyTextToClipboard(text) {
    if (navigator.clipboard?.writeText) {
      try {
        await navigator.clipboard.writeText(text);
        return;
      } catch {
        // Fall through to the execCommand fallback below.
      }
    }
    const scratch = document.createElement('textarea');
    scratch.value = text;
    scratch.style.position = 'fixed';
    scratch.style.opacity = '0';
    document.body.appendChild(scratch);
    scratch.select();
    document.execCommand('copy');
    scratch.remove();
  }

  /**
   * Copies an internal deep link to the current note (reusing index.html's
   * existing `?note=` query param, also used by graph.html) - distinct from
   * "Share" (below), which issues a public, token-protected read-only link
   * instead (docs/04-API-SPEC.md's Sharing section).
   */
  function copyCurrentNoteLink() {
    if (!currentPath) {
      return;
    }
    const url = `${window.location.origin}/index.html?note=${encodeURIComponent(currentPath)}`;
    copyTextToClipboard(url);
  }

  function toggleFullscreen() {
    if (document.fullscreenElement) {
      document.exitFullscreen();
    } else {
      document.documentElement.requestFullscreen?.();
    }
  }

  exportNoteBtn.addEventListener('click', exportCurrentNote);
  printNoteBtn.addEventListener('click', printCurrentNote);
  copyLinkBtn.addEventListener('click', copyCurrentNoteLink);
  fullscreenBtn.addEventListener('click', toggleFullscreen);

  // --- "Share this note" (docs/04-API-SPEC.md's "Sharing" section) --------

  function openShareModal() {
    shareModalEl.classList.remove('hidden');
  }

  function closeShareModal() {
    shareModalEl.classList.add('hidden');
  }

  /**
   * POSTs a fresh share token for the currently open note and shows its
   * link + QR code in the modal. A 404 here means either the note doesn't
   * exist (shouldn't happen for a note we just loaded) or sharing is
   * disabled server-side (`Sharing:Enabled=false`) - the API deliberately
   * makes those two cases indistinguishable, so this shows one generic
   * message rather than guessing which it was.
   */
  async function shareCurrentNote() {
    if (!currentPath) {
      return;
    }
    shareModalErrorEl.classList.add('hidden');
    shareModalBodyEl.classList.add('hidden');
    openShareModal();
    try {
      const result = await Api.shareNote(currentPath);
      currentShareToken = result.token;
      shareModalUrlEl.value = result.url;
      shareModalQrEl.src = `data:image/png;base64,${result.qrCodePngBase64}`;
      shareModalBodyEl.classList.remove('hidden');
    } catch {
      // Deliberately generic per docs/04-API-SPEC.md - see this function's
      // doc comment above.
      shareModalErrorEl.textContent = "Couldn't share this note.";
      shareModalErrorEl.classList.remove('hidden');
    }
  }

  /** Revokes the share token created for the currently open note (idempotent server-side) and closes the modal. */
  async function revokeCurrentShare() {
    const token = currentShareToken;
    currentShareToken = null;
    closeShareModal();
    if (!token) {
      return;
    }
    try {
      await Api.revokeShare(token);
    } catch (err) {
      console.error('Failed to revoke share', err);
    }
  }

  shareNoteBtn.addEventListener('click', shareCurrentNote);
  shareModalCloseBtn.addEventListener('click', closeShareModal);
  shareModalRevokeBtn.addEventListener('click', revokeCurrentShare);
  shareModalCopyBtn.addEventListener('click', () => {
    shareModalUrlEl.select();
    document.execCommand('copy'); // simplest cross-browser copy without a clipboard-permission prompt
  });

  // --- "Upload file" (docs/04-API-SPEC.md's "Media" section) --------------

  /**
   * Embed syntax choice per uploaded content-type (a judgment call, not
   * specified in the docs - recorded here so it's discoverable later):
   *   - image/*        -> standard markdown image syntax, `![alt](relativePath)`.
   *   - audio/*        -> raw HTML `<audio controls src="relativePath"></audio>`
   *                       (marked passes untouched raw HTML straight through;
   *                       markdown.js's `_media/` -> `/media/` rewrite applies
   *                       to this `src` the same as an <img> src).
   *   - video/*        -> raw HTML `<video controls src="relativePath"></video>`,
   *                       same reasoning as audio.
   *   - everything else (application/pdf, etc.) -> a plain markdown link,
   *                       `[filename](relativePath)`.
   */
  function buildMediaMarkdown(file, relativePath) {
    const dotIndex = file.name.lastIndexOf('.');
    const baseName = dotIndex > 0 ? file.name.slice(0, dotIndex) : file.name;
    const contentType = file.type || '';

    if (contentType.startsWith('image/')) {
      return `![${baseName || 'image'}](${relativePath})`;
    }
    if (contentType.startsWith('audio/')) {
      return `<audio controls src="${relativePath}"></audio>`;
    }
    if (contentType.startsWith('video/')) {
      return `<video controls src="${relativePath}"></video>`;
    }
    return `[${file.name}](${relativePath})`;
  }

  /**
   * Replaces `editorEl.value`'s `[start, end)` range with `text`, then
   * re-focuses the editor. By default the caret lands right after the
   * inserted text - `selectionOffset`/`selectionLength`, measured from the
   * start of the replaced range, let a caller instead leave a *range*
   * selected (e.g. a wrapped selection's original text, so typing
   * immediately overwrites it) or place the caret partway through the
   * inserted text (e.g. inside a link's `(url)` slot).
   *
   * Goes through `document.execCommand('insertText', ...)` on the
   * selection, rather than splicing `editorEl.value` directly, so the edit
   * lands on the textarea's own native undo/redo history the same as a
   * real keystroke would - the undo/redo toolbar buttons below use that
   * same native history (`execCommand('undo'/'redo')`), and a direct
   * `.value` splice is invisible to it (confirmed live: toolbar-inserted
   * text didn't undo at all before this). Falls back to a direct splice
   * only if `execCommand` is unsupported/refused (some browsers/contexts) -
   * that fallback edit just won't be undoable via the toolbar's undo
   * button, same as before this comment.
   */
  function replaceRange(start, end, text, selectionOffset = text.length, selectionLength = 0) {
    editorEl.focus();
    editorEl.setSelectionRange(start, end);
    const inserted = document.execCommand('insertText', false, text);
    if (!inserted) {
      editorEl.value = editorEl.value.slice(0, start) + text + editorEl.value.slice(end);
    }
    const selStart = start + selectionOffset;
    editorEl.setSelectionRange(selStart, selStart + selectionLength);
  }

  /**
   * Inserts `text` at the editor's current cursor position (or replaces the
   * current selection) - see replaceRange above for the selectionOffset/
   * selectionLength/undo-history details. This is the one place any
   * toolbar/upload action touches `selectionStart`/`selectionEnd` directly
   * (aside from applyLinePrefix below, which needs a wider, line-aligned
   * range than the raw selection) - every other formatting-toolbar helper
   * builds on this instead of its own cursor-handling code.
   */
  function insertAtCursor(text, selectionOffset = text.length, selectionLength = 0) {
    const start = editorEl.selectionStart ?? editorEl.value.length;
    const end = editorEl.selectionEnd ?? editorEl.value.length;
    replaceRange(start, end, text, selectionOffset, selectionLength);
  }

  async function uploadAndInsertMedia(file) {
    try {
      const { relativePath } = await Api.uploadMedia(file);
      insertAtCursor(buildMediaMarkdown(file, relativePath));
      schedulePreviewRender();
      scheduleAutosave();
    } catch (err) {
      window.alert(`Could not upload "${file.name}": ${err.message}`);
    }
  }

  mediaUploadInputEl.addEventListener('change', () => {
    const file = mediaUploadInputEl.files[0];
    mediaUploadInputEl.value = ''; // reset so picking the same file again still fires 'change'
    if (file && currentPath) {
      uploadAndInsertMedia(file);
    }
  });

  // --- formatting toolbar (docs/03-FEATURE-SPEC.md: "Markdown formatting
  // toolbar in Edit and Split modes") ---------------------------------
  //
  // Every action below builds on insertAtCursor above - none of them touch
  // `selectionStart`/`selectionEnd` directly. Deliberately simple over
  // clever (CLAUDE.md): a single heading level, one fixed table template,
  // etc., rather than e.g. cycling heading levels or a table-size picker.

  function currentSelectionText() {
    const start = editorEl.selectionStart ?? editorEl.value.length;
    const end = editorEl.selectionEnd ?? editorEl.value.length;
    return editorEl.value.slice(start, end);
  }

  /** Wraps the current selection in `before`/`after` (e.g. bold's `**`/`**`), or inserts them around `placeholder` text - left selected, ready to type over - if nothing was selected. */
  function wrapSelection(before, after, placeholder) {
    const selected = currentSelectionText() || placeholder;
    insertAtCursor(before + selected + after, before.length, selected.length);
    schedulePreviewRender();
    scheduleAutosave();
  }

  /** Prepends `makePrefix(lineIndex)` to every line touched by the current selection (e.g. blockquote's `> `, a numbered list's `1. `, `2. `, ...). */
  function applyLinePrefix(makePrefix) {
    const value = editorEl.value;
    const start = editorEl.selectionStart ?? value.length;
    const end = editorEl.selectionEnd ?? value.length;
    const lineStart = value.lastIndexOf('\n', start - 1) + 1;
    const lineEndIdx = value.indexOf('\n', end);
    const lineEnd = lineEndIdx === -1 ? value.length : lineEndIdx;
    const lines = value.slice(lineStart, lineEnd).split('\n');
    const replacement = lines.map((line, i) => makePrefix(i) + line).join('\n');

    // The range being replaced (the whole line(s) touched by the
    // selection) is wider than the raw selection itself, so this calls
    // replaceRange directly instead of insertAtCursor (which always
    // replaces exactly [selectionStart, selectionEnd)).
    replaceRange(lineStart, lineEnd, replacement, 0, replacement.length);
    schedulePreviewRender();
    scheduleAutosave();
  }

  function insertCodeBlock() {
    const selected = currentSelectionText() || 'code';
    const fence = '```';
    insertAtCursor(`${fence}\n${selected}\n${fence}`, fence.length + 1, selected.length);
    schedulePreviewRender();
    scheduleAutosave();
  }

  /** `[label](url)`, with the `url` placeholder left selected, ready to type over. */
  function insertLink() {
    const label = currentSelectionText() || 'link text';
    const text = `[${label}](url)`;
    const urlOffset = text.indexOf('(') + 1;
    insertAtCursor(text, urlOffset, 3);
    schedulePreviewRender();
    scheduleAutosave();
  }

  function insertTable() {
    insertAtCursor('\n| Column 1 | Column 2 |\n| --- | --- |\n| Cell 1 | Cell 2 |\n');
    schedulePreviewRender();
    scheduleAutosave();
  }

  const FORMATTING_ACTIONS = {
    bold: () => wrapSelection('**', '**', 'bold text'),
    italic: () => wrapSelection('_', '_', 'italic text'),
    strikethrough: () => wrapSelection('~~', '~~', 'strikethrough text'),
    heading: () => applyLinePrefix(() => '## '),
    link: insertLink,
    // The toolbar's "Image" button doubles as the existing upload flow
    // (opens the same file picker Phase 5 already wired up) rather than
    // inserting a placeholder that would just be broken until manually
    // fixed up - see index.html's comment on this button.
    image: () => mediaUploadInputEl.click(),
    code: () => wrapSelection('`', '`', 'code'),
    codeblock: insertCodeBlock,
    blockquote: () => applyLinePrefix(() => '> '),
    'bullet-list': () => applyLinePrefix(() => '- '),
    'numbered-list': () => applyLinePrefix((i) => `${i + 1}. `),
    'task-list': () => applyLinePrefix(() => '- [ ] '),
    table: insertTable,
  };

  formattingToolbarEl.addEventListener('click', (event) => {
    const btn = event.target.closest('[data-format]');
    if (!btn || !currentPath) {
      return;
    }
    FORMATTING_ACTIONS[btn.dataset.format]?.();
  });

  // --- editor view-mode toggle (Edit-only / Split / Preview-only) --------
  //
  // docs/03-FEATURE-SPEC.md: "Editor view-mode toggle ..., persisted for
  // the session". This is purely a show/hide of the two existing panes -
  // it doesn't touch markdown.js's rendering pipeline at all. Deliberately
  // sessionStorage (not localStorage, unlike the theme toggle below): the
  // spec calls for this to reset to "Split" (today's default) when the
  // browser tab/session ends, while still surviving a same-tab reload and
  // switching between notes.

  const VIEW_MODE_STORAGE_KEY = 'dotnotes-view-mode';
  const VIEW_MODES = ['edit', 'split', 'preview'];
  const DEFAULT_VIEW_MODE = 'split';

  function getStoredViewMode() {
    const stored = sessionStorage.getItem(VIEW_MODE_STORAGE_KEY);
    return VIEW_MODES.includes(stored) ? stored : DEFAULT_VIEW_MODE;
  }

  // --- Split-mode divider (docs/03-FEATURE-SPEC.md: "Split-pane editor ...
  // resizable via a draggable divider (ratio persisted)") - unlike the
  // view-mode itself (sessionStorage, above), this ratio is a display
  // preference that should roam across reloads the same way as the theme
  // toggle (docs/02-ARCHITECTURE.md's "Client-side UI display preferences
  // live in browser localStorage" note), so it's localStorage. The ratio is
  // applied as a `flex-basis` *percentage* of the shared flex container,
  // not a pixel width - that's what makes both panes reflow proportionally
  // on a window resize or a sidebar collapse (#4) for free, with no resize
  // listener needed: the browser recomputes what percentage means every
  // time the container's own width changes.

  const SPLIT_RATIO_STORAGE_KEY = 'dotnotes-split-ratio';
  const MIN_SPLIT_RATIO = 0.15;
  const MAX_SPLIT_RATIO = 0.85;

  function getStoredSplitRatio() {
    const stored = parseFloat(localStorage.getItem(SPLIT_RATIO_STORAGE_KEY));
    return Number.isFinite(stored) && stored >= MIN_SPLIT_RATIO && stored <= MAX_SPLIT_RATIO ? stored : 0.5;
  }

  let splitRatio = getStoredSplitRatio();

  function applySplitRatio() {
    editorPaneEl.style.flex = `0 0 ${splitRatio * 100}%`;
    previewPaneEl.style.flex = `0 0 ${(1 - splitRatio) * 100}%`;
  }

  let dividerDragging = false;

  splitDividerEl.addEventListener('pointerdown', (event) => {
    dividerDragging = true;
    splitDividerEl.setPointerCapture(event.pointerId);
    document.body.classList.add('split-dragging');
  });

  splitDividerEl.addEventListener('pointermove', (event) => {
    if (!dividerDragging) {
      return;
    }
    const containerRect = splitDividerEl.parentElement.getBoundingClientRect();
    const ratio = (event.clientX - containerRect.left) / containerRect.width;
    splitRatio = Math.min(MAX_SPLIT_RATIO, Math.max(MIN_SPLIT_RATIO, ratio));
    applySplitRatio();
  });

  function endDividerDrag(event) {
    if (!dividerDragging) {
      return;
    }
    dividerDragging = false;
    splitDividerEl.releasePointerCapture(event.pointerId);
    document.body.classList.remove('split-dragging');
    localStorage.setItem(SPLIT_RATIO_STORAGE_KEY, String(splitRatio));
  }
  splitDividerEl.addEventListener('pointerup', endDividerDrag);
  splitDividerEl.addEventListener('pointercancel', endDividerDrag);

  function applyViewMode(mode) {
    editorPaneEl.classList.toggle('hidden', mode === 'preview');
    previewPaneEl.classList.toggle('hidden', mode === 'edit');
    splitDividerEl.classList.toggle('hidden', mode !== 'split');

    if (mode === 'split') {
      applySplitRatio();
    } else {
      // Full-width whichever single pane is shown alone - the divider is
      // hidden above, so there's nothing to drag anyway.
      editorPaneEl.style.flex = '0 0 100%';
      previewPaneEl.style.flex = '0 0 100%';
    }

    // Formatting toolbar: "Visible in Edit and Split modes only, hidden in
    // Preview" (docs/03-FEATURE-SPEC.md).
    formattingToolbarEl.classList.toggle('hidden', mode === 'preview');

    for (const btn of viewModeToggleEl.querySelectorAll('[data-view-mode]')) {
      const isActive = btn.dataset.viewMode === mode;
      btn.classList.toggle('view-mode-btn-active', isActive);
      btn.setAttribute('aria-selected', String(isActive));
    }
  }

  function setViewMode(mode) {
    sessionStorage.setItem(VIEW_MODE_STORAGE_KEY, mode);
    applyViewMode(mode);
  }

  viewModeToggleEl.addEventListener('click', (event) => {
    const btn = event.target.closest('[data-view-mode]');
    if (!btn) {
      return;
    }
    setViewMode(btn.dataset.viewMode);
  });

  applyViewMode(getStoredViewMode());

  // --- collapsible left sidebar (docs/03-FEATURE-SPEC.md: "Collapsible
  // sidebar (persisted collapsed/expanded state), editor/split area
  // reflows to use freed width") -------------------------------------
  //
  // Purely a CSS width toggle on `#sidebar` (css/app.css) - the main
  // content area next to it is already a `flex-1` flex item, so it reflows
  // into the freed width automatically the moment the sidebar's width
  // hits 0, no extra JS needed for that part. Same localStorage treatment
  // as the split ratio above.

  const SIDEBAR_COLLAPSED_STORAGE_KEY = 'dotnotes-sidebar-collapsed';

  function getStoredSidebarCollapsed() {
    return localStorage.getItem(SIDEBAR_COLLAPSED_STORAGE_KEY) === 'true';
  }

  function applySidebarCollapsed(collapsed) {
    sidebarEl.classList.toggle('sidebar-collapsed', collapsed);
    sidebarToggleBtn.setAttribute('aria-pressed', String(collapsed));
    sidebarToggleBtn.title = collapsed ? 'Show sidebar' : 'Hide sidebar';
  }

  function setSidebarCollapsed(collapsed) {
    localStorage.setItem(SIDEBAR_COLLAPSED_STORAGE_KEY, String(collapsed));
    applySidebarCollapsed(collapsed);
  }

  sidebarToggleBtn.addEventListener('click', () => {
    setSidebarCollapsed(!sidebarEl.classList.contains('sidebar-collapsed'));
  });

  applySidebarCollapsed(getStoredSidebarCollapsed());

  // --- sidebar sort-toggle button (docs/03-FEATURE-SPEC.md: "... a name-
  // ascending/descending sort toggle") - js/tree.js owns the actual sort
  // mode/custom-order state and logic; this just reflects it in the button
  // and asks Tree to cycle it on click. -------------------------------

  function updateSortToggleLabel() {
    const mode = Tree.getSortMode();
    sortToggleLabelEl.textContent = mode === 'desc' ? 'Z↓' : 'A↓';
    sortToggleBtn.title = mode === 'desc' ? 'Sorted Z→A - click to sort A→Z' : 'Sorted A→Z - click to sort Z→A';
  }

  sortToggleBtn.addEventListener('click', () => {
    Tree.cycleSortMode();
    updateSortToggleLabel();
  });

  updateSortToggleLabel();

  // --- light/dark theme toggle ---------------------------------------------
  //
  // docs/03-FEATURE-SPEC.md: "Light/dark theme toggle in the top nav".
  // The actual theme state/persistence lives in js/theme.js (loaded from
  // <head>, before this script, so `Theme` is already applied to <html> by
  // the time this runs); this just wires the button and keeps its label in
  // sync with the currently-*inactive* theme (i.e. what clicking it does).

  function updateThemeToggleLabel() {
    themeToggleBtn.textContent = Theme.current() === 'dark' ? 'Light mode' : 'Dark mode';
  }

  themeToggleBtn.addEventListener('click', () => {
    Theme.toggle();
    updateThemeToggleLabel();
  });

  updateThemeToggleLabel();

  // --- "+New" dropdown (docs/03-FEATURE-SPEC.md: 'Single "+New" menu') ----
  //
  // Replaces last round's separate "+ New note"/"+ New folder" buttons.
  // Shared by two anchors - the sidebar's own "+New" (`newMenuBtn`, creates
  // at the vault root/wherever the user types) and the Home view's summary-
  // bar "+New" (`homeNewBtn`, pre-fills createNewNote/createNewFolder's
  // prompt with whatever folder is currently being browsed - see those
  // functions' doc comments). "New from Template" and "New Drawing" are
  // rendered disabled per docs/03-FEATURE-SPEC.md's "Present in the UI but
  // deliberately not implemented" - no template or drawing feature exists
  // to wire them to.

  const ICON_NEW_NOTE =
    '<svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l4.414 4.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />' +
    '</svg>';
  const ICON_NEW_FOLDER =
    '<svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v7a2 2 0 01-2 2H5a2 2 0 01-2-2V7z" />' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M12 11v4m-2-2h4" />' +
    '</svg>';
  const ICON_TEMPLATE =
    '<svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M8 7V5a2 2 0 012-2h8a2 2 0 012 2v10a2 2 0 01-2 2h-2M6 21h8a2 2 0 002-2V11a2 2 0 00-2-2H6a2 2 0 00-2 2v8a2 2 0 002 2z" />' +
    '</svg>';
  const ICON_DRAWING =
    '<svg width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24" aria-hidden="true">' +
    '<path stroke-linecap="round" stroke-linejoin="round" d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />' +
    '</svg>';

  function openNewMenu(anchorEl, defaultFolder) {
    if (Menu.isOpen()) {
      Menu.close();
      return;
    }
    const rect = anchorEl.getBoundingClientRect();
    anchorEl.setAttribute('aria-expanded', 'true');
    Menu.open({
      x: rect.right,
      y: rect.bottom + 4,
      align: 'right',
      id: 'new-menu',
      ariaLabel: 'Create new',
      returnFocusEl: anchorEl,
      onClose: () => anchorEl.setAttribute('aria-expanded', 'false'),
      items: [
        { id: 'new-menu-note', label: 'New Note', icon: ICON_NEW_NOTE, onSelect: () => createNewNote(defaultFolder) },
        { id: 'new-menu-folder', label: 'New Folder', icon: ICON_NEW_FOLDER, onSelect: () => createNewFolder(defaultFolder) },
        { id: 'new-menu-template', label: 'New from Template', icon: ICON_TEMPLATE, disabled: true },
        { id: 'new-menu-drawing', label: 'New Drawing', icon: ICON_DRAWING, disabled: true },
      ],
    });
  }

  newMenuBtn.addEventListener('click', () => openNewMenu(newMenuBtn));
  homeNewBtn.addEventListener('click', () => openNewMenu(homeNewBtn, currentBrowseFolder));

  // --- "dotNotes" logo = Home link (docs/03-FEATURE-SPEC.md's Home view) --

  homeLogoBtn.addEventListener('click', () => navigateHome(''));

  // --- toolbar buttons ---------------------------------------------------

  refreshTreeBtn.addEventListener('click', loadTree);
  backlinksRefreshBtn.addEventListener('click', refreshBacklinksPanel);

  // Search box (docs/03-FEATURE-SPEC.md: "Full-text search across the
  // vault") - picking a result reuses selectFile, same as a file-tree
  // click or a resolved wikilink click.
  Search.init(searchInputEl, searchResultsEl, { onSelectNote: selectFile });

  // --- initial load --------------------------------------------------------

  // A `?note=<vault-relative-path>` query param opens straight into that
  // note - used by graph.html when a node is clicked (plain-HTML
  // navigation between two static pages, rather than a client-side
  // router, per CLAUDE.md's "no framework" rule).
  const requestedNotePath = new URLSearchParams(window.location.search).get('note');
  // Optional deep links for the Tasks & Kanban views (docs/features/tasks-
  // kanban/PLAN.md §5): `?view=board` opens the Kanban board, `?view=tasks`
  // opens the All Tasks list, and `?task=TASK-1` (with either, or alone -
  // implies the board) opens that task's modal once the view is showing.
  const requestedView = new URLSearchParams(window.location.search).get('view');
  const requestedTaskId = new URLSearchParams(window.location.search).get('task');

  Tasks.init({ onOpenNote: selectFile, onTreeReload: loadTree, prepareNoteChange, followNoteChange }).then(async () => {
    if (requestedView === 'board' || (!requestedView && requestedTaskId)) {
      await Tasks.showBoard();
    } else if (requestedView === 'tasks') {
      await Tasks.showList();
    } else {
      return;
    }
    if (requestedTaskId) {
      await Tasks.openTaskById(requestedTaskId);
    }
  });

  // The initial graph fetch races with the user picking a note from the
  // tree; until it resolves, wikilinks.js optimistically treats every
  // link as existing (see its `resolve` doc comment) so nothing flashes
  // as "missing" before the graph has even loaded. Once it *does* load,
  // re-render the currently-open note's preview so that any wikilinks
  // rendered under that optimistic assumption get corrected.
  WikiLinks.refresh().then(() => {
    if (currentPath) {
      renderPreviewNow();
    }
  });
  loadTree().then(() => {
    if (requestedNotePath) {
      selectFile(requestedNotePath);
    }
  });
})();
