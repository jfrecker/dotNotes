// Renders the file-tree sidebar from a GET /api/notes response and wires
// up folder expand/collapse, file selection, native HTML5 drag-and-drop,
// and the right-click/keyboard context menu (js/menu.js does the actual
// menu widget; js/app.js decides what's in it and what each action does -
// this module only ever reports "here's the entry, here's where the user
// wants it" via the handlers passed to `render()`). Expanded-folder state
// lives in this module's closure so it survives a tree refresh (e.g. after
// creating a note) without collapsing everything the user had open.
const Tree = (() => {
  const expandedFolders = new Set();
  let selectedPath = null;
  // The entry currently being dragged, tracked in JS (not read back out of
  // `event.dataTransfer` during dragover/drop) because several browsers
  // restrict reading a drag's payload data until the `drop` event fires -
  // https://html.spec.whatwg.org/multipage/dnd.html's "drag data store
  // mode". A plain module variable sidesteps that entirely, and is safe
  // here (unlike a global) since HTML5 drag-and-drop is inherently
  // single-drag-at-a-time within one page.
  let draggedEntry = null;
  // The handlers passed to the most recent `render()` call - kept so
  // `wireRootDropZone` (called once, outside of any render) can still reach
  // `onMoveRequest` without app.js having to re-wire it on every refresh.
  let currentHandlers = null;
  // The container/entries from the most recent `render()` call - kept so a
  // sort-mode toggle or a drag-reorder (both below) can redraw immediately
  // from the same data, without app.js having to re-fetch/re-call render()
  // itself for what's purely a display-order change.
  let lastContainer = null;
  let lastEntries = null;

  // Every DOM node currently carrying a drag-feedback class, so
  // `endDrag()` below can strip them all even for rows whose own
  // `dragleave`/`dragend` never fires (see its comment).
  const dragFeedbackNodes = new Set();

  /** Adds a drag-feedback class to `node` and remembers it for `endDrag`. */
  function addDragFeedback(node, ...classNames) {
    node.classList.add(...classNames);
    dragFeedbackNodes.add(node);
  }

  /** Strips the drop-target/reorder markers from one row (its `dragleave`, or its `drop`). */
  function clearRowDropFeedback(row) {
    row.classList.remove('tree-row-drop-target', 'tree-row-reorder-before', 'tree-row-reorder-after');
    row._reorderZone = null;
  }

  /**
   * The single teardown for a drag, whatever ended it (a successful drop,
   * a cancelled drag, or a drop whose handler replaced the tree). Clears
   * `draggedEntry` and strips every drag-feedback class that was applied
   * anywhere.
   *
   * Why this is centralized rather than left to each row's own `dragend`:
   * a `drop` handler that rebuilds the tree removes the drag *source* row
   * from the document while the drop is still being dispatched, and the
   * browser then never fires `dragend` on that detached node. That left
   * `draggedEntry` permanently non-null and the root drop zone stuck in
   * its armed state ("Drop here to move to root") until a page reload -
   * and, worse, left the browser's own drag session unterminated, which
   * is what made the page stop responding to the mouse after dragging a
   * folder. Every DOM-replacing drop handler now defers its work out of
   * the drop dispatch (see `drop` below) *and* this runs from a
   * document-level `drop`/`dragend` listener, so neither half depends on
   * the other being enough.
   */
  function endDrag() {
    draggedEntry = null;
    for (const node of dragFeedbackNodes) {
      node.classList.remove(
        'tree-row-dragging',
        'tree-row-drop-target',
        'tree-row-reorder-before',
        'tree-row-reorder-after',
        'tree-root-drop-hint-armed',
        'tree-root-drop-hint-over',
      );
      node._reorderZone = null;
    }
    dragFeedbackNodes.clear();
  }

  /**
   * Runs `fn` after the current drag event's dispatch has finished. Any
   * work that replaces or removes tree DOM must go through this: doing it
   * synchronously inside `drop` detaches the drag source mid-gesture and
   * wedges the browser's drag session (see `endDrag` above). `setTimeout`
   * (a macrotask), not a microtask - microtasks still run before the
   * browser gets to fire `dragend`.
   */
  function afterDragEvent(fn) {
    setTimeout(fn, 0);
  }

  // --- sort mode + per-folder-level custom order (docs/03-FEATURE-SPEC.md's
  // "Sidebar folder reorder ... and a name-ascending/descending sort
  // toggle") - both persisted client-side only, per docs/02-ARCHITECTURE.md's
  // "Client-side UI display preferences live in browser localStorage" note.
  //
  // Two independent pieces of state:
  //   - `sortMode` ('asc'/'desc'): which alphabetical direction the sort-
  //     toggle button (js/app.js's #sort-toggle-btn) last picked. Applies to
  //     every folder level's *files* always (files are never manually
  //     reordered), and to a folder level's *subfolders* only while that
  //     level has no active custom order (see `sortOverrideActive` below).
  //   - `customOrders` (`{ [parentPath]: string[] }`, folder names only):
  //     the manual drag-reordered order for a given parent's subfolders.
  //     Never discarded by toggling sort mode - only hidden from view (see
  //     below) - so a later drag doesn't have to rebuild it from scratch.
  //
  // `sortOverrideActive`: true right after a sort-toggle click, false right
  // after a drag-reorder. This is *the* one flag deciding whether a level
  // with a saved custom order actually shows it (task-brief wording: "the
  // sort toggle overrides back to alphabetical until the user drags
  // again"). It's intentionally a single global flag, not per-level, to
  // match the single (non-per-folder) sort-toggle button in the sidebar;
  // a drag on any one level turns it off for every level, but only levels
  // that actually *have* a stored custom order are affected (levels
  // without one already fall back to alphabetical regardless).
  const SORT_MODE_KEY = 'dotnotes-sort-mode';
  const SORT_OVERRIDE_KEY = 'dotnotes-sort-override-active';
  const CUSTOM_ORDER_KEY = 'dotnotes-custom-order';

  function loadSortMode() {
    return localStorage.getItem(SORT_MODE_KEY) === 'desc' ? 'desc' : 'asc';
  }
  function loadSortOverrideActive() {
    return localStorage.getItem(SORT_OVERRIDE_KEY) === 'true';
  }
  function loadCustomOrders() {
    try {
      const parsed = JSON.parse(localStorage.getItem(CUSTOM_ORDER_KEY) || '{}');
      return parsed && typeof parsed === 'object' ? parsed : {};
    } catch {
      return {};
    }
  }

  let sortMode = loadSortMode();
  let sortOverrideActive = loadSortOverrideActive();
  let customOrders = loadCustomOrders();

  function saveCustomOrders() {
    localStorage.setItem(CUSTOM_ORDER_KEY, JSON.stringify(customOrders));
  }

  function redrawFromLastRender() {
    if (!lastContainer) {
      return;
    }
    render(lastContainer, lastEntries, currentHandlers);
  }

  /** Cycles the global sort-toggle button between ascending/descending, and switches every folder level back to showing alphabetical order (without discarding any level's saved custom order - see the block comment above). Returns the new mode. */
  function cycleSortMode() {
    sortMode = sortMode === 'asc' ? 'desc' : 'asc';
    sortOverrideActive = true;
    localStorage.setItem(SORT_MODE_KEY, sortMode);
    localStorage.setItem(SORT_OVERRIDE_KEY, 'true');
    redrawFromLastRender();
    return sortMode;
  }

  function getSortMode() {
    return sortMode;
  }

  function sortByNameMode(list) {
    const sorted = [...list].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
    return sortMode === 'desc' ? sorted.reverse() : sorted;
  }

  /** Subfolders of `parentPath`, in the order they should currently display: `parentPath`'s saved custom order (if any, and not currently overridden by the sort toggle), else alphabetical per `sortMode`. Any folder present in `folders` but missing from a saved custom order (e.g. created since the last drag) is appended, alphabetically, after the ones the order does know about. */
  function sortFolders(parentPath, folders) {
    const order = !sortOverrideActive ? customOrders[parentPath] : null;
    if (!order || order.length === 0) {
      return sortByNameMode(folders);
    }
    const byName = new Map(folders.map((f) => [f.name, f]));
    const ordered = [];
    for (const name of order) {
      if (byName.has(name)) {
        ordered.push(byName.get(name));
        byName.delete(name);
      }
    }
    return [...ordered, ...sortByNameMode([...byName.values()])];
  }

  /** Folders (in `parentPath`'s current display order per `sortFolders`), then files (always alphabetical per `sortMode`) - the one sort applied everywhere a folder's children are listed: the tree itself, and js/app.js's previous/next-note navigation (via `getSortedChildren` below), so both always agree. */
  function sortEntries(parentPath, entries) {
    const folders = entries.filter((e) => e.type === 'folder');
    const files = entries.filter((e) => e.type === 'file');
    return [...sortFolders(parentPath, folders), ...sortByNameMode(files)];
  }

  /** Same shape as js/app.js's own `findFolderChildren` - kept as a small local copy rather than a cross-module call, since this module doesn't otherwise depend on app.js at all. */
  function findChildren(entries, parentPath) {
    if (!parentPath) {
      return entries || [];
    }
    let list = entries;
    for (const segment of parentPath.split('/')) {
      const found = (list || []).find((e) => e.type === 'folder' && e.name === segment);
      if (!found) {
        return [];
      }
      list = found.children || [];
    }
    return list || [];
  }

  /** `parentPath`'s children in their current display order (docs/03-FEATURE-SPEC.md's previous/next-note navigation: "respecting the active sort order") - used by js/app.js to step to the adjacent note in the same folder. */
  function getSortedChildren(parentPath) {
    return sortEntries(parentPath, findChildren(lastEntries, parentPath));
  }

  /**
   * Applies a folder drag-reorder: `draggedPath` (a folder) is moved to
   * just before/after `targetPath` (a sibling folder under the same
   * parent), persists the resulting order as that parent's custom order,
   * switches off the sort-toggle override (see the block comment above),
   * and redraws.
   */
  function reorderFolder(parentPath, draggedPath, targetPath, zone) {
    const siblingFolders = findChildren(lastEntries, parentPath).filter((e) => e.type === 'folder');
    const currentOrder = sortFolders(parentPath, siblingFolders).map((f) => f.name);
    const draggedName = draggedPath.slice(draggedPath.lastIndexOf('/') + 1);
    const targetName = targetPath.slice(targetPath.lastIndexOf('/') + 1);

    const withoutDragged = currentOrder.filter((name) => name !== draggedName);
    let targetIndex = withoutDragged.indexOf(targetName);
    if (targetIndex === -1) {
      return;
    }
    if (zone === 'after') {
      targetIndex += 1;
    }
    withoutDragged.splice(targetIndex, 0, draggedName);

    customOrders[parentPath] = withoutDragged;
    sortOverrideActive = false;
    saveCustomOrders();
    localStorage.setItem(SORT_OVERRIDE_KEY, 'false');
    redrawFromLastRender();
  }

  /**
   * @param {HTMLElement} container
   * @param {Array} entries - tree array from GET /api/notes
   * @param {{
   *   onSelectFile: (path: string) => void,
   *   onMoveRequest: (entry: {path,name,type}, destinationFolderPath: string) => void,
   *   onContextMenu: (entry: {path,name,type}, x: number, y: number, anchorEl: HTMLElement) => void,
   * }} handlers
   */
  function render(container, entries, handlers) {
    currentHandlers = handlers;
    lastContainer = container;
    lastEntries = entries;
    container.innerHTML = '';

    if (!entries || entries.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'px-1 py-2 text-slate-400';
      empty.textContent = 'No notes yet. Use "+ New" to create one.';
      container.appendChild(empty);
      return;
    }

    container.appendChild(buildList(entries, handlers, ''));
  }

  function buildList(entries, handlers, parentPath) {
    const ul = document.createElement('ul');
    ul.className = 'space-y-0.5';

    const sorted = sortEntries(parentPath, entries);

    for (const entry of sorted) {
      ul.appendChild(buildNode(entry, handlers));
    }
    return ul;
  }

  function buildNode(entry, handlers) {
    const li = document.createElement('li');

    if (entry.type === 'folder') {
      li.appendChild(buildFolderNode(entry, handlers));
    } else {
      li.appendChild(buildFileNode(entry, handlers));
    }

    return li;
  }

  /** Wires the bits every row - file or folder - shares: drag source, keyboard activation/context-menu, and right-click. */
  function wireCommonRowBehavior(row, entry, handlers, onActivate) {
    row.dataset.path = entry.path;
    row.dataset.type = entry.type;
    row.draggable = true;
    row.tabIndex = 0;

    row.addEventListener('dragstart', (event) => {
      // Any state left over from a previous drag that ended without a
      // `dragend` (see `endDrag`) is cleared here too, so a new drag never
      // starts on top of a stale highlight or a stale `draggedEntry`.
      endDrag();
      draggedEntry = entry;
      event.dataTransfer.effectAllowed = 'move';
      // Firefox requires *some* data to be set for the drag to proceed at
      // all; the actual move uses `draggedEntry` above, not this.
      event.dataTransfer.setData('text/plain', entry.path);
      // Applying the "dragging" look on the next frame (rather than
      // synchronously) keeps it out of the browser's drag-image snapshot,
      // which is taken from the element's appearance at dragstart time.
      // Tracked so `dragend` can cancel it if it fires first (an
      // extremely fast drag, or a programmatic/assistive-tech-driven one,
      // could otherwise end *before* the next frame - leaving the class
      // added after cleanup already ran, and never removed again).
      row._dragRafId = requestAnimationFrame(() => addDragFeedback(row, 'tree-row-dragging'));
    });

    row.addEventListener('dragend', () => {
      if (row._dragRafId) {
        cancelAnimationFrame(row._dragRafId);
        row._dragRafId = null;
      }
      endDrag();
    });

    row.addEventListener('contextmenu', (event) => {
      event.preventDefault();
      row.focus();
      handlers.onContextMenu(entry, event.clientX, event.clientY, row);
    });

    row.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        onActivate();
      } else if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
        // The keyboard-accessible fallback for the right-click menu -
        // drag-and-drop has no keyboard equivalent, so "Move to..." here is
        // the only way to move an item without a mouse.
        event.preventDefault();
        const rect = row.getBoundingClientRect();
        handlers.onContextMenu(entry, rect.left, rect.bottom, row);
      }
    });
  }

  /** True if `folderEntry` would be an invalid drop target for whatever's currently being dragged (its own current parent, itself, or one of its own descendants). Mirrors js/app.js's authoritative check - this copy only drives the *visual* drag-over feedback, so a false negative here is at worst a missing highlight, never an unwanted move (app.js always re-checks before calling the API). */
  function isInvalidDropTarget(folderEntry, dragged = draggedEntry) {
    if (!dragged) {
      return true;
    }
    if (dragged.type === 'folder' && (folderEntry.path === dragged.path || folderEntry.path.startsWith(`${dragged.path}/`))) {
      return true;
    }
    const draggedParent = parentFolderOf(dragged.path);
    return folderEntry.path === draggedParent;
  }

  /** True if dragging `draggedEntry` (a folder) over `targetEntry` (a sibling folder under the same parent) could be a reorder rather than a reparent - the precondition for treating the row's top/bottom edge as a "drop between" zone instead of "drop onto." */
  function canReorderOnto(targetEntry) {
    return (
      !!draggedEntry &&
      draggedEntry.type === 'folder' &&
      draggedEntry.path !== targetEntry.path &&
      parentFolderOf(draggedEntry.path) === parentFolderOf(targetEntry.path)
    );
  }

  function parentFolderOf(path) {
    const idx = path.lastIndexOf('/');
    return idx >= 0 ? path.slice(0, idx) : '';
  }

  function buildFolderNode(entry, handlers) {
    const wrapper = document.createDocumentFragment();
    const isExpanded = expandedFolders.has(entry.path);

    const row = document.createElement('div');
    row.className =
      'tree-row flex cursor-pointer select-none items-center gap-1 rounded px-1 py-1 hover:bg-slate-100';
    row.setAttribute('role', 'treeitem');
    row.setAttribute('aria-expanded', String(isExpanded));

    const arrow = document.createElement('span');
    arrow.className = 'inline-block w-3 flex-shrink-0 text-slate-400';
    arrow.textContent = isExpanded ? '▾' : '▸';

    const label = document.createElement('span');
    label.className = 'tree-row-label truncate font-medium text-slate-700';
    label.textContent = entry.name;

    row.append(arrow, label);

    const childList = buildList(entry.children || [], handlers, entry.path);
    childList.classList.add('ml-4');
    childList.classList.toggle('hidden', !isExpanded);

    function toggle() {
      const willExpand = !expandedFolders.has(entry.path);
      if (willExpand) {
        expandedFolders.add(entry.path);
      } else {
        expandedFolders.delete(entry.path);
      }
      arrow.textContent = willExpand ? '▾' : '▸';
      row.setAttribute('aria-expanded', String(willExpand));
      childList.classList.toggle('hidden', !willExpand);
    }

    row.addEventListener('click', toggle);
    wireCommonRowBehavior(row, entry, handlers, toggle);

    // Folders (not individual notes - matches the live NoteDiscovery
    // reference, where only folder rows accept a drop) are drop targets -
    // either "drop onto" (move/reparent into this folder, the pre-existing
    // behavior) or, for a dragged folder over one of its own siblings,
    // "drop between" (reorder - docs/03-FEATURE-SPEC.md's "Sidebar folder
    // reorder"). The two are distinguished by *where* over the row the
    // pointer is: the top/bottom ~30% is a reorder-before/after zone, the
    // middle ~40% is the existing move-onto zone - matching this phase's
    // task brief ("a different drop target zone/threshold").
    row.addEventListener('dragover', (event) => {
      if (canReorderOnto(entry)) {
        const rect = row.getBoundingClientRect();
        const ratio = (event.clientY - rect.top) / rect.height;
        const zone = ratio < 0.3 ? 'before' : ratio > 0.7 ? 'after' : null;
        if (zone) {
          event.preventDefault();
          event.dataTransfer.dropEffect = 'move';
          row.classList.remove('tree-row-drop-target');
          row.classList.toggle('tree-row-reorder-before', zone === 'before');
          row.classList.toggle('tree-row-reorder-after', zone === 'after');
          dragFeedbackNodes.add(row);
          row._reorderZone = zone;
          return;
        }
      }
      row.classList.remove('tree-row-reorder-before', 'tree-row-reorder-after');
      row._reorderZone = null;
      if (isInvalidDropTarget(entry)) {
        return; // no preventDefault() - browser shows the "no drop" cursor
      }
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
      addDragFeedback(row, 'tree-row-drop-target');
    });
    row.addEventListener('dragleave', (event) => {
      // `dragleave` also fires when the pointer moves from the row onto
      // one of its own children (the arrow/label spans). Ignoring those
      // keeps the highlight steady instead of flickering off and back on
      // with every pixel of movement across the row.
      if (event.relatedTarget && row.contains(event.relatedTarget)) {
        return;
      }
      clearRowDropFeedback(row);
    });
    row.addEventListener('drop', (event) => {
      event.preventDefault();
      const zone = row._reorderZone;
      const dropped = draggedEntry;
      clearRowDropFeedback(row);
      if (!dropped) {
        return;
      }
      // Both branches below replace tree DOM (and the move can also pop an
      // error alert), so neither may run inside the drop dispatch - see
      // `afterDragEvent`. `dropped` is captured above because `endDrag`
      // will have cleared `draggedEntry` by the time these run.
      if (zone) {
        afterDragEvent(() => reorderFolder(parentFolderOf(entry.path), dropped.path, entry.path, zone));
        return;
      }
      if (!isInvalidDropTarget(entry, dropped)) {
        afterDragEvent(() => handlers.onMoveRequest(dropped, entry.path));
      }
    });

    wrapper.append(row, childList);
    return wrapper;
  }

  function buildFileNode(entry, handlers) {
    const row = document.createElement('div');
    row.className = 'tree-row cursor-pointer rounded px-1 py-1 pl-4 text-slate-700 hover:bg-slate-100';
    row.setAttribute('role', 'treeitem');
    if (entry.path === selectedPath) {
      row.classList.add('bg-blue-100', 'text-blue-800', 'hover:bg-blue-100');
    }

    const label = document.createElement('span');
    label.className = 'tree-row-label truncate';
    label.textContent = entry.name;
    row.appendChild(label);

    function activate() {
      handlers.onSelectFile(entry.path);
    }

    row.addEventListener('click', activate);
    wireCommonRowBehavior(row, entry, handlers, activate);

    return row;
  }

  /**
   * Wires the sidebar's "Drag=Move" hint element as the vault-root drop
   * zone (matches the live NoteDiscovery demo - see this element's own
   * comment in index.html). Called once at startup, not on every render,
   * since the element itself is static markup outside `#file-tree`.
   */
  function wireRootDropZone(element, labelElement) {
    const defaultLabel = labelElement.textContent;

    document.addEventListener('dragstart', () => {
      if (draggedEntry) {
        addDragFeedback(element, 'tree-root-drop-hint-armed');
        labelElement.textContent = 'Drop here to move to root';
      }
    });

    // Both of these run on the *bubble* phase at the document, i.e. after
    // whichever row handled the drop - so a row's own handler still sees
    // `draggedEntry`. `drop` is listened for alongside `dragend` because a
    // drop whose handler replaces the tree can detach the drag source
    // before the browser gets to fire `dragend` on it at all; see
    // `endDrag`. Running both is harmless (`endDrag` is idempotent) and
    // means the drag visuals are never left behind - including when the
    // move itself fails.
    const finishDrag = () => {
      endDrag();
      labelElement.textContent = defaultLabel;
    };
    document.addEventListener('dragend', finishDrag);
    document.addEventListener('drop', finishDrag);

    element.addEventListener('dragover', (event) => {
      if (!draggedEntry || parentFolderOf(draggedEntry.path) === '') {
        return; // nothing dragging, or it's already at the root - no-op
      }
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
      addDragFeedback(element, 'tree-root-drop-hint-over');
    });
    element.addEventListener('dragleave', (event) => {
      if (event.relatedTarget && element.contains(event.relatedTarget)) {
        return; // moved onto the hint's own icon/label, not out of it
      }
      element.classList.remove('tree-root-drop-hint-over');
    });
    element.addEventListener('drop', (event) => {
      event.preventDefault();
      element.classList.remove('tree-root-drop-hint-over');
      const dropped = draggedEntry;
      if (dropped && currentHandlers) {
        // Deferred for the same reason as the folder rows' own drop
        // handler - onMoveRequest rebuilds the tree.
        afterDragEvent(() => currentHandlers.onMoveRequest(dropped, ''));
      }
    });
  }

  /** Marks `path` as the active file in the tree (re-styles rows in place). */
  function setSelected(container, path) {
    selectedPath = path;
    const rows = container.querySelectorAll('[data-type="file"]');
    for (const row of rows) {
      const isSelected = row.dataset.path === path;
      row.classList.toggle('bg-blue-100', isSelected);
      row.classList.toggle('text-blue-800', isSelected);
      row.classList.toggle('hover:bg-blue-100', isSelected);
    }
  }

  /** Ensures every ancestor folder of `path` is expanded (e.g. after creating a note in a nested folder). */
  function expandAncestorsOf(path) {
    const segments = path.split('/');
    segments.pop(); // drop the file name itself
    let current = '';
    for (const segment of segments) {
      current = current ? `${current}/${segment}` : segment;
      expandedFolders.add(current);
    }
  }

  /** Like `expandAncestorsOf`, but also expands `path` itself (it names a folder, not a file - e.g. after creating or moving a folder). */
  function expandFolder(path) {
    const segments = path.split('/');
    let current = '';
    for (const segment of segments) {
      current = current ? `${current}/${segment}` : segment;
      expandedFolders.add(current);
    }
  }

  /** Flattens every folder path out of a `GET /api/notes` tree, depth-first - used by js/app.js's "Move to..." folder picker. */
  function listFolderPaths(entries) {
    const paths = [];
    (function walk(list) {
      for (const entry of list || []) {
        if (entry.type === 'folder') {
          paths.push(entry.path);
          walk(entry.children);
        }
      }
    })(entries);
    return paths;
  }

  return {
    render,
    setSelected,
    expandAncestorsOf,
    expandFolder,
    listFolderPaths,
    wireRootDropZone,
    getSortedChildren,
    getSortMode,
    cycleSortMode,
  };
})();
