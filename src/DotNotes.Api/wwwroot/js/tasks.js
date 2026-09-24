// Tasks & Kanban (docs/features/tasks-kanban/PLAN.md) - the Kanban board,
// All Tasks list, task create/edit modal, sidebar nav/counts, live-refresh
// polling, and the small bits of notes-integration (convert-to-task,
// task-frontmatter preview header, task-note rename) that js/app.js calls
// into. Kept in its own file/module (like js/tree.js, js/search.js) rather
// than folded into app.js, per this phase's task brief.
const Tasks = (() => {
  const BOARD_POLL_MS = 3000; // "poll a revision counter every 3s while a tasks view is visible"
  const COUNT_POLL_MS = 10000; // cheap background refresh of the sidebar badges otherwise

  // --- DOM ------------------------------------------------------------------

  const navAllBtn = document.getElementById('tasks-nav-all-btn');
  const navBoardBtn = document.getElementById('tasks-nav-board-btn');
  const navAllCountEl = document.getElementById('tasks-nav-all-count');
  const navBoardCountEl = document.getElementById('tasks-nav-board-count');

  const boardViewEl = document.getElementById('tasks-board-view');
  const boardColumnsEl = document.getElementById('tasks-board-columns');
  const boardErrorEl = document.getElementById('tasks-board-error');
  const boardSearchEl = document.getElementById('tasks-board-search');
  const boardLabelFilterEl = document.getElementById('tasks-board-label-filter');
  const boardAssigneeFilterEl = document.getElementById('tasks-board-assignee-filter');
  const boardPriorityFilterEl = document.getElementById('tasks-board-priority-filter');
  const boardNewBtn = document.getElementById('tasks-board-new-btn');
  const pomodoroWidgetEl = document.getElementById('pomodoro-widget');

  const listViewEl = document.getElementById('tasks-list-view');
  const listTbodyEl = document.getElementById('tasks-list-tbody');
  const listErrorEl = document.getElementById('tasks-list-error');
  const listSearchEl = document.getElementById('tasks-list-search');
  const listStatusFilterEl = document.getElementById('tasks-list-status-filter');
  const listLabelFilterEl = document.getElementById('tasks-list-label-filter');
  const listAssigneeFilterEl = document.getElementById('tasks-list-assignee-filter');
  const listPriorityFilterEl = document.getElementById('tasks-list-priority-filter');
  const listShowCompletedEl = document.getElementById('tasks-list-show-completed');
  const listNewBtn = document.getElementById('tasks-list-new-btn');
  const listTableEl = document.getElementById('tasks-list-table');

  const sidebarFooterVersionEl = document.getElementById('app-version-label');

  const modalOverlayEl = document.getElementById('task-modal');
  const modalTitleInputEl = document.getElementById('task-modal-title-input');
  const modalErrorEl = document.getElementById('task-modal-error');
  const modalStatusSelectEl = document.getElementById('task-modal-status');
  const modalPrioritySelectEl = document.getElementById('task-modal-priority');
  const modalMilestoneEl = document.getElementById('task-modal-milestone');
  const modalAssigneeChipsEl = document.getElementById('task-modal-assignee-chips');
  const modalLabelsChipsEl = document.getElementById('task-modal-labels-chips');
  const modalDependenciesChipsEl = document.getElementById('task-modal-dependencies-chips');
  const modalDescriptionEl = document.getElementById('task-modal-description');
  const modalAcListEl = document.getElementById('task-modal-ac-list');
  const modalAcNewEl = document.getElementById('task-modal-ac-new');
  const modalAcAddBtn = document.getElementById('task-modal-ac-add-btn');
  const modalPlanEl = document.getElementById('task-modal-plan');
  const modalNotesEl = document.getElementById('task-modal-notes');
  const modalSummaryEl = document.getElementById('task-modal-summary');
  const modalMetaEl = document.getElementById('task-modal-meta');
  const modalOpenNoteLinkEl = document.getElementById('task-modal-open-note-link');
  const modalFolderRowEl = document.getElementById('task-modal-folder-row');
  const modalFolderValueEl = document.getElementById('task-modal-folder-value');
  const modalCompleteBtn = document.getElementById('task-modal-complete-btn');
  const modalCancelBtn = document.getElementById('task-modal-cancel-btn');
  const modalSaveBtn = document.getElementById('task-modal-save-btn');
  const modalCloseBtn = document.getElementById('task-modal-close-btn');

  // --- module state -----------------------------------------------------

  // Case-sensitive - mirrors TaskFolders.IsInCompletedFolder on the backend
  // (docs' v0.2.1 contract). js/app.js keeps its own copy for the sidebar
  // tree's context menu, which has no reach into this module's closure.
  const COMPLETED_FOLDER = 'Completed';

  // { folder, idPrefix, statuses[], defaultStatus, backlogStatus,
  //   completedStatus, completedFolder, priorities[] }
  let config = null;
  let allTasksCache = []; // includeCompleted:true - sidebar counts, filter option lists, isTaskPath, rename lookup
  let cachedPathSet = new Set();
  let currentView = null; // null | 'board' | 'list'
  let lastRevision = null;
  let pollTimer = null;
  let countPollTimer = null;
  let onOpenNote = null; // (path) => void, from init()
  let onTreeReload = null; // () => Promise<void>, from init()
  let onPrepareNoteChange = null; // (path) => Promise<void> - flush the editor if it has `path` open, from init()
  let onFollowNoteChange = null; // (oldPath, newPath) => Promise<void> - reopen the editor on the file's new path, from init()

  let listSortKey = 'id';
  let listSortDir = 'asc';

  // --- helpers ------------------------------------------------------------

  function showError(el, message) {
    if (!el) return;
    if (!message) {
      el.classList.add('hidden');
      el.textContent = '';
      return;
    }
    el.textContent = message;
    el.classList.remove('hidden');
  }

  function formatDateShort(value) {
    if (!value) return '';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return String(value).slice(0, 10);
    return date.toLocaleDateString(undefined, { dateStyle: 'medium' });
  }

  function uniqueSorted(values) {
    return [...new Set(values.filter(Boolean))].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
  }

  /** Rebuilds a `<select>`'s options from `values`, preserving the current selection if it's still valid. */
  function populateSelect(selectEl, values, placeholder) {
    if (!selectEl) return;
    const previous = selectEl.value;
    selectEl.innerHTML = '';
    const placeholderOpt = document.createElement('option');
    placeholderOpt.value = '';
    placeholderOpt.textContent = placeholder;
    selectEl.appendChild(placeholderOpt);
    for (const value of values) {
      const opt = document.createElement('option');
      opt.value = value;
      opt.textContent = value;
      selectEl.appendChild(opt);
    }
    if (values.includes(previous)) {
      selectEl.value = previous;
    }
  }

  // --- id-aware sort (numeric-aware "ID" column, docs §5 "All Tasks view") --

  function idSortKey(id) {
    const match = /^(.*?)(\d+(?:\.\d+)*)$/.exec(String(id || ''));
    if (!match) {
      return { prefix: String(id || ''), numbers: [] };
    }
    return { prefix: match[1], numbers: match[2].split('.').map(Number) };
  }

  function compareIds(a, b) {
    const ka = idSortKey(a);
    const kb = idSortKey(b);
    if (ka.prefix !== kb.prefix) {
      return ka.prefix.localeCompare(kb.prefix, undefined, { sensitivity: 'base' });
    }
    const len = Math.max(ka.numbers.length, kb.numbers.length);
    for (let i = 0; i < len; i++) {
      const diff = (ka.numbers[i] ?? -1) - (kb.numbers[i] ?? -1);
      if (diff !== 0) return diff;
    }
    return 0;
  }

  function compareTasks(a, b, key) {
    let result;
    switch (key) {
      case 'id':
        result = compareIds(a.id, b.id);
        break;
      case 'labels':
        result = (a.labels || []).join(', ').localeCompare((b.labels || []).join(', '), undefined, { sensitivity: 'base' });
        break;
      case 'assignee':
        result = (a.assignee || []).join(', ').localeCompare((b.assignee || []).join(', '), undefined, { sensitivity: 'base' });
        break;
      case 'createdDate':
        result = new Date(a.createdDate || 0) - new Date(b.createdDate || 0);
        break;
      default:
        result = String(a[key] || '').localeCompare(String(b[key] || ''), undefined, { sensitivity: 'base' });
    }
    return result;
  }

  // --- YAML-frontmatter task detection (docs/06-DATA-MODEL.md / PLAN §5's
  // "Notes integration") - a lightweight line-based parser, not a general
  // YAML implementation: it only needs to pull out a handful of known scalar/
  // list fields well enough to render the compact preview header. Detection
  // itself (id + status both present) matches the backend's own rule. ------

  function stripQuotes(raw) {
    const s = raw.trim();
    if (s.length >= 2 && ((s[0] === "'" && s[s.length - 1] === "'") || (s[0] === '"' && s[s.length - 1] === '"'))) {
      return s.slice(1, -1);
    }
    return s;
  }

  function parseSimpleYamlBlock(text) {
    const result = {};
    let currentListKey = null;
    for (const rawLine of text.split(/\r?\n/)) {
      const listMatch = /^\s*-\s*(.*)$/.exec(rawLine);
      if (listMatch && currentListKey) {
        result[currentListKey] = result[currentListKey] || [];
        result[currentListKey].push(stripQuotes(listMatch[1]));
        continue;
      }
      const kvMatch = /^([A-Za-z_][A-Za-z0-9_]*):\s*(.*)$/.exec(rawLine);
      if (!kvMatch) {
        currentListKey = null;
        continue;
      }
      const key = kvMatch[1];
      const rest = kvMatch[2].trim();
      if (rest === '') {
        currentListKey = key; // possibly followed by "- item" lines
        continue;
      }
      currentListKey = null;
      if (rest === '[]') {
        result[key] = [];
      } else if (rest.startsWith('[') && rest.endsWith(']')) {
        result[key] = rest
          .slice(1, -1)
          .split(',')
          .map((s) => stripQuotes(s))
          .filter((s) => s !== '');
      } else {
        result[key] = stripQuotes(rest);
      }
    }
    return result;
  }

  /**
   * Detects task frontmatter (`id` + `status` both present, per
   * docs/06-DATA-MODEL.md's detection rule) at the start of `content` and,
   * if found, returns `{ id, status, priority, labels, body }` where `body`
   * is `content` with the YAML block removed. Returns `null` for a plain
   * note (including one that begins with `---` but isn't a task) - the
   * caller renders it exactly as before.
   */
  function extractTaskFrontmatter(content) {
    if (!content) return null;
    const match = /^\uFEFF?---\r?\n([\s\S]*?)\r?\n---[ \t]*\r?\n?/.exec(content);
    if (!match) return null;
    const fields = parseSimpleYamlBlock(match[1]);
    if (!fields.id || !fields.status) return null;
    return {
      id: fields.id,
      status: fields.status,
      priority: fields.priority || '',
      labels: Array.isArray(fields.labels) ? fields.labels : [],
      body: content.slice(match[0].length),
    };
  }

  // --- compact task preview header (PLAN §5's "Notes integration") --------

  function renderTaskPreviewHeader(taskInfo) {
    const headerEl = document.getElementById('task-preview-header');
    if (!headerEl) return;
    headerEl.innerHTML = '';
    headerEl.classList.remove('hidden');

    const idEl = document.createElement('span');
    idEl.className = 'task-preview-header-id';
    idEl.textContent = taskInfo.id;
    headerEl.appendChild(idEl);

    const statusEl = document.createElement('span');
    statusEl.className = 'task-status-badge';
    statusEl.textContent = taskInfo.status;
    headerEl.appendChild(statusEl);

    if (taskInfo.priority) {
      const priorityEl = document.createElement('span');
      priorityEl.className = `tasks-priority-badge tasks-priority-${escapeClassSuffix(taskInfo.priority)}`;
      priorityEl.textContent = taskInfo.priority;
      headerEl.appendChild(priorityEl);
    }

    for (const label of taskInfo.labels) {
      const chip = document.createElement('span');
      chip.className = 'tasks-chip';
      chip.textContent = label;
      headerEl.appendChild(chip);
    }

    const link = document.createElement('button');
    link.type = 'button';
    link.className = 'task-preview-header-link';
    link.textContent = 'Open on board →';
    link.addEventListener('click', () => showBoard());
    headerEl.appendChild(link);
  }

  function hideTaskPreviewHeader() {
    const headerEl = document.getElementById('task-preview-header');
    if (!headerEl) return;
    headerEl.classList.add('hidden');
    headerEl.innerHTML = '';
  }

  function escapeClassSuffix(value) {
    return String(value).toLowerCase().replace(/[^a-z0-9]+/g, '-');
  }

  // --- sidebar nav / counts -------------------------------------------------

  function updateActiveNav() {
    navAllBtn?.classList.toggle('tasks-nav-row-active', currentView === 'list');
    navBoardBtn?.classList.toggle('tasks-nav-row-active', currentView === 'board');
  }

  /** `task.completed`, defensively falling back to the old `archived` field name for a server that hasn't rolled forward yet. */
  function isCompleted(task) {
    return task.completed ?? task.archived ?? false;
  }

  async function refreshTaskCache() {
    try {
      const result = await Api.listTasks({ includeCompleted: true });
      allTasksCache = Array.isArray(result?.tasks) ? result.tasks : [];
    } catch (err) {
      console.error('Failed to load tasks for sidebar counts', err);
      return;
    }
    cachedPathSet = new Set(allTasksCache.map((t) => t.path));
    const activeCount = allTasksCache.filter((t) => !isCompleted(t)).length;
    if (navAllCountEl) navAllCountEl.textContent = String(activeCount);
    if (navBoardCountEl) navBoardCountEl.textContent = String(activeCount);
  }

  // --- view switching -------------------------------------------------------

  function stopPolling() {
    clearInterval(pollTimer);
    pollTimer = null;
  }

  function startPolling(reload) {
    stopPolling();
    pollTimer = setInterval(async () => {
      if (document.visibilityState !== 'visible') return;
      try {
        const { revision } = await Api.getTaskRevision();
        if (revision !== lastRevision) {
          lastRevision = revision;
          await reload();
          await refreshTaskCache();
        }
      } catch (err) {
        console.error('Task revision poll failed', err);
      }
    }, BOARD_POLL_MS);
  }

  /** Hides both task views - called by js/app.js whenever a note or the Home view is shown. */
  function hide() {
    boardViewEl?.classList.add('hidden');
    listViewEl?.classList.add('hidden');
    if (currentView) {
      currentView = null;
      stopPolling();
      Pomodoro.unmount();
      updateActiveNav();
    }
    Pomodoro.render();
  }

  async function showBoard() {
    hideOtherAppViews();
    boardViewEl?.classList.remove('hidden');
    listViewEl?.classList.add('hidden');
    currentView = 'board';
    updateActiveNav();
    Pomodoro.mount(pomodoroWidgetEl, () => allTasksCache.filter((t) => !isCompleted(t)).map((t) => ({ id: t.id, title: t.title })));
    Pomodoro.render();
    await refreshTaskCache();
    populateFilterOptions();
    await loadBoard();
    startPolling(loadBoard);
  }

  async function showList() {
    hideOtherAppViews();
    listViewEl?.classList.remove('hidden');
    boardViewEl?.classList.add('hidden');
    currentView = 'list';
    updateActiveNav();
    Pomodoro.unmount();
    Pomodoro.render();
    await refreshTaskCache();
    populateFilterOptions();
    await loadList();
    startPolling(loadList);
  }

  /** Hides home/note-editor (js/app.js owns those elements, but both views need to be mutually exclusive with them). */
  function hideOtherAppViews() {
    document.getElementById('home-view')?.classList.add('hidden');
    document.getElementById('note-editor-view')?.classList.add('hidden');
  }

  function populateFilterOptions() {
    const labels = uniqueSorted(allTasksCache.flatMap((t) => t.labels || []));
    const assignees = uniqueSorted(allTasksCache.flatMap((t) => t.assignee || []));
    const priorities = uniqueSorted((config?.priorities || []).length ? config.priorities : allTasksCache.map((t) => t.priority));
    const statuses = (config?.statuses && config.statuses.length) ? config.statuses : uniqueSorted(allTasksCache.map((t) => t.status));

    populateSelect(boardLabelFilterEl, labels, 'All labels');
    populateSelect(boardAssigneeFilterEl, assignees, 'All assignees');
    populateSelect(boardPriorityFilterEl, priorities, 'All priorities');
    populateSelect(listLabelFilterEl, labels, 'All labels');
    populateSelect(listAssigneeFilterEl, assignees, 'All assignees');
    populateSelect(listPriorityFilterEl, priorities, 'All priorities');
    populateSelect(listStatusFilterEl, statuses, 'All statuses');
  }

  // --- board ------------------------------------------------------------

  function boardFilters() {
    return {
      q: boardSearchEl?.value.trim() || '',
      label: boardLabelFilterEl?.value || '',
      assignee: boardAssigneeFilterEl?.value || '',
      priority: boardPriorityFilterEl?.value || '',
    };
  }

  async function loadBoard() {
    showError(boardErrorEl, null);
    let result;
    try {
      result = await Api.getBoard(boardFilters());
    } catch (err) {
      showError(boardErrorEl, `Could not load the board: ${err.message}`);
      return;
    }
    lastRevision = result.revision;
    renderBoardColumns(result.columns || []);
  }

  let draggedTaskId = null;
  let draggedFromStatus = null;

  function buildPriorityBadge(priority) {
    if (!priority) return null;
    const el = document.createElement('span');
    el.className = `tasks-priority-badge tasks-priority-${escapeClassSuffix(priority)}`;
    el.textContent = priority;
    return el;
  }

  /**
   * True for a board column whose tasks with an empty/unrecognised/backlog
   * raw status all land in it (v0.2.1 - replaces the old "unlisted status"
   * trailing-column concept: that column is now just the (always-present,
   * leftmost) Backlog column). Falls back to comparing against
   * `config.backlogStatus` for a server response that predates `isBacklog`.
   */
  function isBacklogColumn(column) {
    return column.isBacklog ?? column.status === (config?.backlogStatus || 'Backlog');
  }

  /** Small "Blocked"/whatever-status badge for a Backlog-column card whose raw status isn't the Backlog status itself (v0.2.1). */
  function buildCardStatusBadge(task) {
    const backlogStatus = config?.backlogStatus || 'Backlog';
    if (!task.status || task.status.toLowerCase() === backlogStatus.toLowerCase()) return null;
    const el = document.createElement('span');
    el.className = 'tasks-card-status-badge';
    el.textContent = task.status;
    return el;
  }

  /**
   * Green "Complete" action on a card (v0.2.1). Must never start a drag or
   * open the task modal - both the card's own `dragstart` and `click`
   * listeners live on the card element and this button is a descendant of
   * it, so every event that could trigger either is stopped here.
   */
  function buildCardCompleteButton(task, card) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'tasks-card-complete-btn';
    btn.title = 'Mark as done and move into a Completed subfolder';
    btn.setAttribute('aria-label', `Complete ${task.id}`);
    btn.draggable = false;
    btn.textContent = 'Complete';
    // A mousedown/pointerdown that starts on this button never fires a
    // `dragstart` on the button itself (it's draggable=false), but the
    // browser's native HTML5 DnD still picks the nearest draggable
    // ancestor - this card - as the drag source, since drag-source
    // selection happens outside JS event bubbling and isn't stopped by
    // `stopPropagation()` here. So the card's own `dragstart` handler
    // checks this flag and cancels the drag outright when it's set.
    btn.addEventListener('mousedown', (event) => {
      event.stopPropagation();
      if (card) card.dataset.suppressDrag = 'true';
    });
    btn.addEventListener('mouseup', () => {
      if (card) delete card.dataset.suppressDrag;
    });
    btn.addEventListener('dragstart', (event) => {
      event.preventDefault();
      event.stopPropagation();
    });
    btn.addEventListener('keydown', (event) => {
      // Stop the card's own keydown handler (Enter -> open modal) from also
      // firing while this button has focus.
      if (event.key === 'Enter' || event.key === ' ') {
        event.stopPropagation();
      }
    });
    btn.addEventListener('click', (event) => {
      event.stopPropagation();
      completeTaskWithConfirm(task);
    });
    return btn;
  }

  function buildCard(task, { isBacklog } = {}) {
    const card = document.createElement('div');
    card.className = 'tasks-card';
    card.draggable = true;
    card.tabIndex = 0;
    card.dataset.taskId = task.id;
    card.setAttribute('role', 'button');
    card.setAttribute('aria-label', `${task.id} - ${task.title}`);

    const top = document.createElement('div');
    top.className = 'tasks-card-top';
    const idEl = document.createElement('span');
    idEl.className = 'tasks-card-id';
    idEl.textContent = task.id;
    top.appendChild(idEl);
    const priorityBadge = buildPriorityBadge(task.priority);
    if (priorityBadge) top.appendChild(priorityBadge);
    if (isBacklog) {
      const statusBadge = buildCardStatusBadge(task);
      if (statusBadge) top.appendChild(statusBadge);
    }
    card.appendChild(top);

    const title = document.createElement('div');
    title.className = 'tasks-card-title';
    title.textContent = task.title;
    card.appendChild(title);

    if (task.excerpt) {
      const excerpt = document.createElement('div');
      excerpt.className = 'tasks-card-excerpt';
      excerpt.textContent = task.excerpt;
      card.appendChild(excerpt);
    }

    if ((task.labels || []).length) {
      const row = document.createElement('div');
      row.className = 'tasks-chip-row';
      for (const label of task.labels) {
        const chip = document.createElement('span');
        chip.className = 'tasks-chip';
        chip.textContent = label;
        row.appendChild(chip);
      }
      card.appendChild(row);
    }

    const meta = document.createElement('div');
    meta.className = 'tasks-card-meta';
    if ((task.assignee || []).length) {
      const assignee = document.createElement('span');
      assignee.className = 'tasks-card-assignee';
      assignee.textContent = task.assignee.join(', ');
      meta.appendChild(assignee);
    }
    if (task.acTotal > 0) {
      const ac = document.createElement('span');
      ac.className = 'tasks-card-ac';
      ac.textContent = `✓ ${task.acChecked}/${task.acTotal}`;
      meta.appendChild(ac);
    }
    const date = document.createElement('span');
    date.className = 'tasks-card-date';
    date.textContent = formatDateShort(task.createdDate);
    meta.appendChild(date);
    card.appendChild(meta);

    if (!isCompleted(task)) {
      card.appendChild(buildCardCompleteButton(task, card));
    }

    // Any mousedown that reaches the card itself did NOT start on the
    // Complete button (that button stops propagation of its own mousedown),
    // so clear a suppress flag left stale by an earlier press-and-release
    // on the button that ended outside it.
    card.addEventListener('mousedown', () => {
      delete card.dataset.suppressDrag;
    });
    card.addEventListener('click', () => openModalById(task.id));
    card.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        openModalById(task.id);
      }
    });
    card.addEventListener('dragstart', (event) => {
      // A drag that started on the Complete button (see
      // buildCardCompleteButton) must not drag the card - the browser still
      // fires this event because the button is inside a draggable=true
      // ancestor even though it's itself draggable=false.
      if (card.dataset.suppressDrag) {
        delete card.dataset.suppressDrag; // single-use: the mouseup that follows may land outside the button
        event.preventDefault();
        return;
      }
      draggedTaskId = task.id;
      draggedFromStatus = card.closest('.tasks-board-column')?.dataset.status || null;
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/plain', task.id);
      requestAnimationFrame(() => card.classList.add('tasks-card-dragging'));
    });
    card.addEventListener('dragend', () => {
      card.classList.remove('tasks-card-dragging');
      clearDropIndicators();
      draggedTaskId = null;
      draggedFromStatus = null;
    });

    return card;
  }

  function clearDropIndicators() {
    document.querySelectorAll('.tasks-card-drop-indicator').forEach((el) => el.remove());
  }

  /**
   * Where a drop at `clientY` lands among the *visible* cards of `cardsEl`
   * (ignoring the dragged card): `index` among them, plus the ids of the
   * visible neighbours either side (null at the ends).
   */
  function computeDropAnchor(cardsEl, clientY) {
    const cards = [...cardsEl.querySelectorAll('.tasks-card')].filter((c) => c.dataset.taskId !== draggedTaskId);
    let index = cards.length;
    for (let i = 0; i < cards.length; i++) {
      const rect = cards[i].getBoundingClientRect();
      if (clientY < rect.top + rect.height / 2) {
        index = i;
        break;
      }
    }
    return {
      index,
      prevId: index > 0 ? cards[index - 1].dataset.taskId : null,
      nextId: index < cards.length ? cards[index].dataset.taskId : null,
    };
  }

  function showDropIndicator(cardsEl, index) {
    const cards = [...cardsEl.querySelectorAll('.tasks-card')].filter((c) => c.dataset.taskId !== draggedTaskId);
    const ref = cards[index] || null;
    // Reuse the existing indicator when it is already in the right spot
    // instead of removing/re-creating it on every dragover (~20x/second).
    const all = document.querySelectorAll('.tasks-card-drop-indicator');
    const existing = all.length === 1 && all[0].parentElement === cardsEl ? all[0] : null;
    if (existing && existing.nextElementSibling === ref && (ref !== null || existing === cardsEl.lastElementChild)) {
      return;
    }
    clearDropIndicators();
    const indicator = document.createElement('div');
    indicator.className = 'tasks-card-drop-indicator';
    cardsEl.insertBefore(indicator, ref);
  }

  function hasActiveBoardFilters() {
    return Object.values(boardFilters()).some(Boolean);
  }

  /**
   * Resolves the drop `anchor` (positions among the *visible* cards) to a
   * position in the target column's FULL, unfiltered order - the server
   * counts every task in the column, so a filtered board's visible index
   * would land the card in the wrong place. Returns `{ index, beforeId }`
   * (both undefined = append to the end).
   */
  async function resolveDropPosition(taskId, status, anchor) {
    if (!hasActiveBoardFilters()) {
      return { index: anchor.index, beforeId: anchor.nextId || undefined };
    }
    const full = await Api.getBoard({});
    const column = (full.columns || []).find((c) => c.status === status);
    const ids = (column?.tasks || []).map((t) => t.id).filter((id) => id !== taskId);
    let index;
    if (anchor.prevId && ids.includes(anchor.prevId)) {
      index = ids.indexOf(anchor.prevId) + 1; // right after the last visible card above the drop point
    } else if (anchor.nextId && ids.includes(anchor.nextId)) {
      index = ids.indexOf(anchor.nextId); // top of the visible cards: right before the first one
    } else {
      index = ids.length; // nothing visible to anchor to (or the board changed): append
    }
    if (index >= ids.length) {
      return { index: undefined, beforeId: undefined };
    }
    return { index, beforeId: ids[index] };
  }

  async function performMoveTask(taskId, status, anchor) {
    try {
      const { index, beforeId } = await resolveDropPosition(taskId, status, anchor);
      await Api.moveTask(taskId, status, index, beforeId);
    } catch (err) {
      showError(boardErrorEl, `Could not move ${taskId}: ${err.message}`);
    }
    await loadBoard();
    await refreshTaskCache();
  }

  function wireColumnDropZone(cardsEl, status) {
    // Every column (Backlog included, v0.2.1) now accepts drops from any
    // other column - there's no more "unlisted" column that only accepts
    // its own cards back.
    const dropAllowed = () => !!draggedTaskId;
    cardsEl.addEventListener('dragover', (event) => {
      if (!draggedTaskId) return;
      if (!dropAllowed()) {
        event.dataTransfer.dropEffect = 'none';
        return; // no preventDefault -> the browser shows "no drop"
      }
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
      showDropIndicator(cardsEl, computeDropAnchor(cardsEl, event.clientY).index);
    });
    cardsEl.addEventListener('dragleave', (event) => {
      if (event.relatedTarget && cardsEl.contains(event.relatedTarget)) return;
      clearDropIndicators();
    });
    cardsEl.addEventListener('drop', (event) => {
      if (!dropAllowed()) return;
      event.preventDefault();
      const anchor = computeDropAnchor(cardsEl, event.clientY);
      clearDropIndicators();
      const taskId = draggedTaskId;
      draggedTaskId = null;
      if (!taskId) return;
      // Optimistic move: relocate the card's DOM node immediately, then
      // reconcile with the server's response (docs/features/tasks-kanban/
      // PLAN.md §5: "optimistic update then refresh").
      const card = document.querySelector(`.tasks-card[data-task-id="${cssEscapeAttr(taskId)}"]`);
      if (card) {
        const refCards = [...cardsEl.querySelectorAll('.tasks-card')].filter((c) => c !== card);
        cardsEl.insertBefore(card, refCards[anchor.index] || null);
      }
      performMoveTask(taskId, status, anchor);
    });
  }

  function cssEscapeAttr(value) {
    return window.CSS && CSS.escape ? CSS.escape(value) : String(value).replace(/["\\]/g, '\\$&');
  }

  function renderBoardColumns(columns) {
    boardColumnsEl.innerHTML = '';
    for (const column of columns) {
      const isBacklog = isBacklogColumn(column);
      const columnEl = document.createElement('div');
      columnEl.className = 'tasks-board-column';
      columnEl.dataset.status = column.status;
      if (isBacklog) columnEl.classList.add('tasks-board-column-backlog');

      const header = document.createElement('div');
      header.className = 'tasks-board-column-header';
      const titleEl = document.createElement('span');
      titleEl.className = 'tasks-board-column-title';
      titleEl.textContent = column.status;
      const countEl = document.createElement('span');
      countEl.className = 'tasks-board-column-count';
      countEl.textContent = String((column.tasks || []).length);
      header.append(titleEl, countEl);
      const addBtn = document.createElement('button');
      addBtn.type = 'button';
      addBtn.className = 'tasks-board-column-add-btn';
      addBtn.title = `Add a task to ${column.status}`;
      addBtn.textContent = '+';
      addBtn.addEventListener('click', () => openModal(null, { presetStatus: column.status }));
      header.append(addBtn);
      columnEl.appendChild(header);

      const cardsEl = document.createElement('div');
      cardsEl.className = 'tasks-board-column-cards';
      cardsEl.dataset.status = column.status;
      for (const task of column.tasks || []) {
        cardsEl.appendChild(buildCard(task, { isBacklog }));
      }
      wireColumnDropZone(cardsEl, column.status);
      columnEl.appendChild(cardsEl);

      boardColumnsEl.appendChild(columnEl);
    }
  }

  // --- All Tasks list -------------------------------------------------------

  function listFilters() {
    return {
      q: listSearchEl?.value.trim() || '',
      status: listStatusFilterEl?.value || '',
      label: listLabelFilterEl?.value || '',
      assignee: listAssigneeFilterEl?.value || '',
      priority: listPriorityFilterEl?.value || '',
      includeCompleted: !!listShowCompletedEl?.checked,
    };
  }

  let lastListTasks = [];

  async function loadList() {
    showError(listErrorEl, null);
    try {
      const result = await Api.listTasks(listFilters());
      lastRevision = result.revision;
      lastListTasks = result.tasks || [];
    } catch (err) {
      showError(listErrorEl, `Could not load tasks: ${err.message}`);
      return;
    }
    renderList();
  }

  function renderList() {
    const sorted = [...lastListTasks].sort((a, b) => {
      const cmp = compareTasks(a, b, listSortKey);
      return listSortDir === 'desc' ? -cmp : cmp;
    });

    listTbodyEl.innerHTML = '';
    for (const task of sorted) {
      const row = document.createElement('tr');
      row.className = 'tasks-list-row';
      if (isCompleted(task)) {
        row.classList.add('tasks-list-row-completed');
      }
      row.addEventListener('click', () => openModalById(task.id));

      const cells = [
        task.id,
        task.title,
        task.status,
        task.priority || '',
        (task.labels || []).join(', '),
        (task.assignee || []).join(', '),
        task.milestone || '',
        formatDateShort(task.createdDate),
      ];
      for (const value of cells) {
        const td = document.createElement('td');
        td.textContent = value;
        row.appendChild(td);
      }
      listTbodyEl.appendChild(row);
    }

    for (const th of listTableEl.querySelectorAll('th[data-sort-key]')) {
      const btn = th.querySelector('.tasks-list-sort-btn');
      const isActive = th.dataset.sortKey === listSortKey;
      btn.classList.toggle('tasks-list-sort-btn-active', isActive);
      btn.textContent = btn.textContent.replace(/ [\u25B2\u25BC]$/, '') + (isActive ? (listSortDir === 'asc' ? ' \u25B2' : ' \u25BC') : '');
    }
  }

  listTableEl?.addEventListener('click', (event) => {
    const th = event.target.closest('th[data-sort-key]');
    if (!th) return;
    const key = th.dataset.sortKey;
    if (listSortKey === key) {
      listSortDir = listSortDir === 'asc' ? 'desc' : 'asc';
    } else {
      listSortKey = key;
      listSortDir = 'asc';
    }
    renderList();
  });

  // --- chip inputs (assignee / labels / dependencies) ------------------

  function setupChipInput(containerEl, placeholder) {
    let items = [];
    let inputEl = null;

    function render() {
      containerEl.innerHTML = '';
      for (const item of items) {
        const chip = document.createElement('span');
        chip.className = 'task-chip-pill';
        const label = document.createElement('span');
        label.textContent = item;
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'task-chip-remove';
        remove.textContent = '\u00d7';
        remove.setAttribute('aria-label', `Remove ${item}`);
        remove.addEventListener('click', () => {
          items = items.filter((v) => v !== item);
          render();
        });
        chip.append(label, remove);
        containerEl.appendChild(chip);
      }
      inputEl = document.createElement('input');
      inputEl.type = 'text';
      inputEl.className = 'task-chip-text-input';
      inputEl.placeholder = items.length ? '' : placeholder || '';
      inputEl.addEventListener('keydown', (event) => {
        if (event.key === 'Enter' || event.key === ',') {
          event.preventDefault();
          const value = inputEl.value.trim().replace(/,$/, '');
          if (value && !items.includes(value)) {
            items = [...items, value];
            render();
          } else {
            inputEl.value = '';
          }
        } else if (event.key === 'Backspace' && !inputEl.value && items.length) {
          items = items.slice(0, -1);
          render();
        }
      });
      containerEl.appendChild(inputEl);
    }

    render();
    return {
      setValue(values) {
        items = [...(values || [])];
        render();
      },
      getValue: () => [...items],
    };
  }

  const assigneeChipInput = modalAssigneeChipsEl ? setupChipInput(modalAssigneeChipsEl, 'Add assignee…') : null;
  const labelsChipInput = modalLabelsChipsEl ? setupChipInput(modalLabelsChipsEl, 'Add label…') : null;
  const dependenciesChipInput = modalDependenciesChipsEl ? setupChipInput(modalDependenciesChipsEl, 'Add TASK-id…') : null;

  // --- acceptance-criteria checklist editor --------------------------------

  let acItems = []; // [{ text, checked }]

  function renderAcList() {
    modalAcListEl.innerHTML = '';
    acItems.forEach((item, index) => {
      const li = document.createElement('li');
      li.className = 'task-ac-item';

      const checkbox = document.createElement('input');
      checkbox.type = 'checkbox';
      checkbox.checked = !!item.checked;
      checkbox.addEventListener('change', () => {
        item.checked = checkbox.checked;
      });

      const textInput = document.createElement('input');
      textInput.type = 'text';
      textInput.className = 'task-ac-item-text';
      textInput.value = item.text || '';
      textInput.addEventListener('input', () => {
        item.text = textInput.value;
      });

      const removeBtn = document.createElement('button');
      removeBtn.type = 'button';
      removeBtn.className = 'task-chip-remove';
      removeBtn.textContent = '\u00d7';
      removeBtn.setAttribute('aria-label', 'Remove this acceptance criterion');
      removeBtn.addEventListener('click', () => {
        acItems.splice(index, 1);
        renderAcList();
      });

      li.append(checkbox, textInput, removeBtn);
      modalAcListEl.appendChild(li);
    });
  }

  modalAcAddBtn?.addEventListener('click', () => {
    const text = modalAcNewEl.value.trim();
    if (!text) return;
    acItems.push({ text, checked: false });
    modalAcNewEl.value = '';
    renderAcList();
  });
  modalAcNewEl?.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      modalAcAddBtn.click();
    }
  });

  // --- task modal -------------------------------------------------------

  let lastFocusedBeforeModal = null;
  let editingTask = null; // the full Task last loaded into the modal, or null in create mode
  let editingFolder = null; // create-mode only: folder the new task will be created into (read-only row)
  let modalStatusTouched = false; // true once the user changes #task-modal-status this session (v0.2.1)
  modalStatusSelectEl?.addEventListener('change', () => {
    modalStatusTouched = true;
  });

  function populateModalSelects() {
    const statuses = (config?.statuses && config.statuses.length) ? config.statuses : ['To Do', 'In Progress', 'Done'];
    modalStatusSelectEl.innerHTML = '';
    for (const status of statuses) {
      const opt = document.createElement('option');
      opt.value = status;
      opt.textContent = status;
      modalStatusSelectEl.appendChild(opt);
    }
    // An existing task with an unknown/legacy status (docs/06-DATA-MODEL.md's
    // Backlog-column rule) still needs to be selectable - but an empty/null
    // raw status is not a real status to offer as a choice (v0.2.1: leaving
    // this blank-option out, combined with saveModal's raw-status
    // preservation below, is what stops an unrelated field edit from
    // silently rewriting an empty status to the default one).
    if (editingTask && editingTask.status && !statuses.includes(editingTask.status)) {
      const opt = document.createElement('option');
      opt.value = editingTask.status;
      opt.textContent = editingTask.status;
      modalStatusSelectEl.appendChild(opt);
    }

    const priorities = (config?.priorities && config.priorities.length) ? config.priorities : ['high', 'medium', 'low'];
    modalPrioritySelectEl.innerHTML = '<option value="">(none)</option>';
    for (const priority of priorities) {
      const opt = document.createElement('option');
      opt.value = priority;
      opt.textContent = priority;
      modalPrioritySelectEl.appendChild(opt);
    }
  }

  function openModal(task, options = {}) {
    editingTask = task || null;
    // Create-mode only: the folder this task will be created into - shown
    // as a read-only row (v0.2.1), never editable from this modal.
    editingFolder = task ? null : (options.folder || config?.folder || 'Task');
    showError(modalErrorEl, null);
    populateModalSelects();

    // v0.2.1: tracks whether the user has actually touched the status
    // dropdown in this modal session - see the `change` listener below and
    // currentModalPatch()'s raw-status preservation.
    modalStatusTouched = false;

    modalTitleInputEl.value = task?.title || '';
    modalStatusSelectEl.value = task?.status || options.presetStatus || config?.defaultStatus || modalStatusSelectEl.options[0]?.value || '';
    modalPrioritySelectEl.value = task?.priority || '';
    modalMilestoneEl.value = task?.milestone || '';
    assigneeChipInput?.setValue(task?.assignee || []);
    labelsChipInput?.setValue(task?.labels || []);
    dependenciesChipInput?.setValue(task?.dependencies || []);
    modalDescriptionEl.value = task?.description || '';
    acItems = (task?.acceptanceCriteria || []).map((item) => ({ text: item.text, checked: !!item.checked }));
    renderAcList();
    modalPlanEl.value = task?.implementationPlan || '';
    modalNotesEl.value = task?.implementationNotes || '';
    modalSummaryEl.value = task?.finalSummary || '';

    if (modalFolderRowEl) {
      modalFolderRowEl.classList.toggle('hidden', !!task);
      if (!task && modalFolderValueEl) modalFolderValueEl.textContent = editingFolder;
    }
    modalCompleteBtn.classList.toggle('hidden', !task || isCompleted(task));
    if (task?.path) {
      modalOpenNoteLinkEl.classList.remove('hidden');
      modalOpenNoteLinkEl.textContent = `Open note (${task.path})`;
    } else {
      modalOpenNoteLinkEl.classList.add('hidden');
    }

    const metaParts = [];
    if (task?.createdDate) metaParts.push(`Created ${formatDateShort(task.createdDate)}`);
    if (task?.updatedAt) metaParts.push(`Updated ${formatDateShort(task.updatedAt)}`);
    modalMetaEl.textContent = metaParts.join(' · ');

    lastFocusedBeforeModal = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    modalOverlayEl.classList.remove('hidden');
    modalTitleInputEl.focus();
  }

  async function openModalById(id) {
    try {
      const task = await Api.getTask(id);
      openModal(task);
    } catch (err) {
      window.alert(`Could not load ${id}: ${err.message}`);
    }
  }

  function closeModal() {
    modalOverlayEl.classList.add('hidden');
    editingTask = null;
    editingFolder = null;
    if (lastFocusedBeforeModal && document.contains(lastFocusedBeforeModal)) {
      lastFocusedBeforeModal.focus();
    }
    lastFocusedBeforeModal = null;
  }

  function currentModalPatch() {
    // v0.2.1: an existing task with an empty or unrecognised raw status has
    // no real matching entry in the dropdown (see populateModalSelects) - if
    // the user never actually touched the status control, saving an
    // unrelated field must not silently rewrite that status to whatever the
    // select happened to default to (e.g. Backlog).
    const statuses = (config?.statuses && config.statuses.length) ? config.statuses : [];
    const preserveRawStatus =
      editingTask && !modalStatusTouched && (!editingTask.status || !statuses.includes(editingTask.status));
    return {
      title: modalTitleInputEl.value.trim(),
      status: preserveRawStatus ? editingTask.status : modalStatusSelectEl.value,
      priority: modalPrioritySelectEl.value,
      milestone: modalMilestoneEl.value,
      assignee: assigneeChipInput.getValue(),
      labels: labelsChipInput.getValue(),
      dependencies: dependenciesChipInput.getValue(),
      description: modalDescriptionEl.value,
      acceptanceCriteria: acItems.map((item) => ({ text: item.text, checked: item.checked })),
      implementationPlan: modalPlanEl.value,
      implementationNotes: modalNotesEl.value,
      finalSummary: modalSummaryEl.value,
    };
  }

  /** Only the fields that actually changed vs. `editingTask` - docs §4's "PATCH only changed fields." */
  function diffPatch(original, candidate) {
    const patch = {};
    for (const key of Object.keys(candidate)) {
      const newValue = candidate[key];
      if (key === 'acceptanceCriteria') {
        const originalAc = (original.acceptanceCriteria || []).map((a) => ({ text: a.text, checked: !!a.checked }));
        if (JSON.stringify(originalAc) !== JSON.stringify(newValue)) {
          patch[key] = newValue;
        }
        continue;
      }
      if (Array.isArray(newValue)) {
        if (JSON.stringify(original[key] || []) !== JSON.stringify(newValue)) {
          patch[key] = newValue;
        }
      } else if ((original[key] || '') !== (newValue || '')) {
        patch[key] = newValue;
      }
    }
    return patch;
  }

  async function afterMutate() {
    await refreshTaskCache();
    if (currentView === 'board') {
      await loadBoard();
    } else if (currentView === 'list') {
      await loadList();
    }
  }

  async function saveModal() {
    const patch = currentModalPatch();
    if (!patch.title) {
      showError(modalErrorEl, 'Title is required.');
      return;
    }
    showError(modalErrorEl, null);
    try {
      const wasCreate = !editingTask;
      let followFrom = null;
      let followTo = null;
      if (editingTask) {
        const changed = diffPatch(editingTask, patch);
        if (Object.keys(changed).length > 0) {
          // The editor may have this very note open: flush it first so no
          // edit is lost, and reopen it afterwards (a title change renames
          // the file; any change makes its loaded content/updatedAt stale).
          followFrom = editingTask.path;
          await onPrepareNoteChange?.(followFrom);
          const updated = await Api.updateTask(editingTask.id, changed);
          followTo = updated?.path || followFrom;
        }
      } else {
        await Api.createTask({
          title: patch.title,
          status: patch.status,
          folder: editingFolder || undefined,
          description: patch.description,
          assignee: patch.assignee,
          labels: patch.labels,
          priority: patch.priority || undefined,
          milestone: patch.milestone,
          dependencies: patch.dependencies,
          acceptanceCriteria: acItems.map((item) => item.text),
        });
      }
      closeModal();
      await afterMutate();
      if (followFrom || wasCreate) {
        await onTreeReload?.();
      }
      if (followFrom) {
        await onFollowNoteChange?.(followFrom, followTo);
      }
    } catch (err) {
      showError(modalErrorEl, err.message);
    }
  }

  /**
   * Shared "Complete task" flow (v0.2.1 - replaces the old red Archive
   * action): confirm, `POST .../complete` (moves the note into a
   * `Completed` subfolder next to it), then bring the board/list/tree/open
   * editor up to date. Used by the modal's Complete button, a card's own
   * Complete button, and the sidebar's "Complete task" context-menu item -
   * one flow, three entry points, per this phase's task brief.
   */
  async function completeTaskWithConfirm(task, { onDone } = {}) {
    const confirmed = await window.Modal.confirm({
      title: 'Complete task',
      description: `Mark "${task.id} - ${task.title}" as done? It will be moved into a Completed subfolder next to it.`,
      confirmLabel: 'Complete',
    });
    if (!confirmed) return;
    const { id, path: oldPath } = task;
    try {
      // The editor may have this note open: flush it before the file moves,
      // then follow it to its new path so the editor never sits on a dead
      // path.
      await onPrepareNoteChange?.(oldPath);
      const completed = await Api.completeTask(id);
      onDone?.();
      await afterMutate();
      await onTreeReload?.();
      await onFollowNoteChange?.(oldPath, completed?.path || oldPath);
    } catch (err) {
      window.alert(`Could not complete "${id}": ${err.message}`);
    }
  }

  async function completeEditingTask() {
    if (!editingTask) return;
    const task = editingTask;
    await completeTaskWithConfirm(task, {
      onDone: () => {
        // Only close the modal once the API call actually succeeded, and
        // only if it's still open on the same task (a poll-driven refresh
        // could have closed/replaced it while the confirm dialog was up).
        if (editingTask === task) closeModal();
      },
    });
  }

  /**
   * "Complete task" from the sidebar's task-file context menu
   * (js/app.js) - looks the task up by note path (refreshing the cache
   * first if it isn't there yet) and runs the same flow as the card/modal
   * buttons.
   */
  async function completeTaskByPath(path) {
    let task = allTasksCache.find((t) => t.path === path);
    if (!task) {
      await refreshTaskCache();
      task = allTasksCache.find((t) => t.path === path);
    }
    if (!task) {
      window.alert('This note is not a task.');
      return;
    }
    await completeTaskWithConfirm(task);
  }

  modalSaveBtn?.addEventListener('click', saveModal);
  modalCancelBtn?.addEventListener('click', closeModal);
  modalCloseBtn?.addEventListener('click', closeModal);
  modalCompleteBtn?.addEventListener('click', completeEditingTask);
  modalOpenNoteLinkEl?.addEventListener('click', (event) => {
    event.preventDefault();
    if (editingTask?.path && onOpenNote) {
      const notePath = editingTask.path; // closeModal() clears editingTask
      closeModal();
      onOpenNote(notePath);
    }
  });
  modalOverlayEl?.addEventListener('mousedown', (event) => {
    if (event.target === modalOverlayEl) {
      closeModal();
    }
  });
  document.addEventListener('keydown', (event) => {
    if (modalOverlayEl.classList.contains('hidden')) return;
    // The shared confirm/prompt dialog (e.g. Complete's confirmation) sits on
    // top of this modal and owns Escape/Tab/Ctrl+S while it's open.
    if (window.Modal?.isOpen?.()) return;
    if (event.key === 'Tab') {
      trapFocus(event);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      closeModal();
    } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      saveModal();
    }
  });

  /** Keeps Tab/Shift+Tab cycling inside the open task modal. */
  function trapFocus(event) {
    const focusable = [...modalOverlayEl.querySelectorAll('button, [href], input, select, textarea, [tabindex]')].filter(
      (el) => !el.disabled && el.tabIndex >= 0 && el.offsetParent !== null,
    );
    if (!focusable.length) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (!modalOverlayEl.contains(document.activeElement)) {
      event.preventDefault();
      first.focus();
    } else if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  // --- filter wiring ----------------------------------------------------

  function debounce(fn, ms) {
    let timer = null;
    return (...args) => {
      clearTimeout(timer);
      timer = setTimeout(() => fn(...args), ms);
    };
  }

  const debouncedLoadBoard = debounce(loadBoard, 250);
  const debouncedLoadList = debounce(loadList, 250);

  boardSearchEl?.addEventListener('input', debouncedLoadBoard);
  boardLabelFilterEl?.addEventListener('change', loadBoard);
  boardAssigneeFilterEl?.addEventListener('change', loadBoard);
  boardPriorityFilterEl?.addEventListener('change', loadBoard);
  boardNewBtn?.addEventListener('click', () => openModal(null, {}));

  listSearchEl?.addEventListener('input', debouncedLoadList);
  listStatusFilterEl?.addEventListener('change', loadList);
  listLabelFilterEl?.addEventListener('change', loadList);
  listAssigneeFilterEl?.addEventListener('change', loadList);
  listPriorityFilterEl?.addEventListener('change', loadList);
  listShowCompletedEl?.addEventListener('change', loadList);
  listNewBtn?.addEventListener('click', () => openModal(null, {}));

  navAllBtn?.addEventListener('click', showList);
  navBoardBtn?.addEventListener('click', showBoard);

  // --- notes integration (docs/features/tasks-kanban/PLAN.md §5's "Notes
  // integration") --------------------------------------------------------

  function isTaskPath(path) {
    return cachedPathSet.has(path);
  }

  async function convertNote(path) {
    try {
      const status = config?.defaultStatus || config?.statuses?.[0];
      // If the editor has this note open, flush it before the file is
      // renamed (converting renames `<id> - <title>.md`), then follow it.
      await onPrepareNoteChange?.(path);
      const result = await Api.convertNoteToTask(path, status);
      await onTreeReload?.();
      await refreshTaskCache();
      await onFollowNoteChange?.(path, result.path);
      openModal(result);
    } catch (err) {
      window.alert(`Could not convert "${path}" to a task: ${err.message}`);
    }
  }

  /**
   * Renames a task note via `PATCH /api/tasks/{id} { title }` so frontmatter
   * and filename stay in sync (docs/features/tasks-kanban/PLAN.md §5) -
   * called from js/app.js's inline-title commit instead of the plain
   * note move/rename flow when the open note is a task. Returns the
   * updated `Task` (its `path` reflects the rename).
   */
  async function renameTaskTitle(path, newTitle) {
    let task = allTasksCache.find((t) => t.path === path);
    if (!task) {
      await refreshTaskCache();
      task = allTasksCache.find((t) => t.path === path);
    }
    if (!task) {
      throw new Error('This note is not a task.');
    }
    // The editor's inline title shows the file name ("TASK-1 - Fix login"),
    // so strip a leading "<id> - " before it becomes the frontmatter title -
    // otherwise the file would be renamed "TASK-1 - TASK-1 - Fix login.md".
    let title = String(newTitle).trim();
    const idPrefix = `${task.id} - `;
    if (title.toLowerCase().startsWith(idPrefix.toLowerCase())) {
      title = title.slice(idPrefix.length).trim();
    }
    const result = await Api.updateTask(task.id, { title });
    await refreshTaskCache();
    return result;
  }

  // --- version footer (docs/features/tasks-kanban/PLAN.md §10) ------------

  function formatVersion(raw) {
    if (!raw) return '';
    let version = String(raw).split('+')[0]; // strip build metadata after '+'
    const parts = version.split('.');
    if (parts.length === 4 && parts[3] === '0') {
      version = parts.slice(0, 3).join('.');
    }
    return version;
  }

  async function loadVersion() {
    try {
      const appConfig = await Api.getConfig();
      if (sidebarFooterVersionEl && appConfig?.version) {
        sidebarFooterVersionEl.textContent = `v${formatVersion(appConfig.version)}`;
      }
    } catch (err) {
      console.error('Failed to load /api/config', err);
    }
  }

  // --- init -----------------------------------------------------------------

  async function init(options = {}) {
    onOpenNote = options.onOpenNote || null;
    onTreeReload = options.onTreeReload || null;
    onPrepareNoteChange = options.prepareNoteChange || null;
    onFollowNoteChange = options.followNoteChange || null;

    Pomodoro.init(document.getElementById('pomodoro-nav-indicator'), () => showBoard());

    try {
      config = await Api.getTaskConfig();
    } catch (err) {
      console.error('Failed to load /api/tasks/config', err);
      config = {
        folder: 'Task',
        statuses: ['Backlog', 'To Do', 'In Progress', 'Done'],
        priorities: ['high', 'medium', 'low'],
        defaultStatus: null,
        backlogStatus: 'Backlog',
        completedStatus: 'Done',
        completedFolder: COMPLETED_FOLDER,
      };
    }

    await loadVersion();
    await refreshTaskCache();
    populateFilterOptions();

    clearInterval(countPollTimer);
    countPollTimer = setInterval(() => {
      if (!currentView && document.visibilityState === 'visible') {
        refreshTaskCache();
      }
    }, COUNT_POLL_MS);
  }

  return {
    init,
    showBoard,
    showList,
    hide,
    isTaskPath,
    convertNote,
    renameTaskTitle,
    extractTaskFrontmatter,
    renderTaskPreviewHeader,
    hideTaskPreviewHeader,
    openTaskById: openModalById,
    isViewActive: () => currentView !== null,
    // --- v0.2.1: the "+ New" menu's "New Task" and the folder context
    // menu's "New Task" both open this same create-mode modal - see
    // js/app.js's `openNewMenu`/`handleTreeContextMenu`.
    openCreateTask: (options = {}) => openModal(null, options),
    // The sidebar "+New" menu's default folder when nothing more specific
    // applies (js/app.js has no reach into this module's `config`).
    getDefaultFolder: () => config?.folder || 'Task',
    // The sidebar task-file context menu's "Complete task" item.
    completeTaskByPath,
  };
})();
