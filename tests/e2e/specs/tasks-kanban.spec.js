// Tasks & Kanban (docs/features/tasks-kanban/PLAN.md). Written against the
// contract in that doc's §4 (REST API) and §5 (UI) - the backend for
// /api/tasks was being built concurrently by another agent, so this suite
// could not be run against a live server while it was authored. Follows
// specs/fixtures.js's conventions (uniqueName, the `api` fixture) so it
// slots into the same suite once the backend lands.
const { test, expect, uniqueName } = require('./fixtures');

/** Deletes a task by id via the REST API (best-effort - a failure here is
 * cleanup noise, not a test failure). Archiving, not deleting, is all the
 * API exposes (docs/features/tasks-kanban/PLAN.md §4 has no `DELETE
 * /api/tasks/{id}`) - the underlying note still needs a real delete. */
async function cleanupTask(request, baseURL, task) {
  if (!task?.path) return;
  await request.delete(`${baseURL}/api/notes/${task.path.split('/').map(encodeURIComponent).join('/')}`).catch(() => {});
}

async function createTaskViaApi(request, baseURL, body) {
  const res = await request.post(`${baseURL}/api/tasks`, { data: body });
  expect(res.ok(), `POST /api/tasks failed: ${res.status()} ${await res.text()}`).toBeTruthy();
  return res.json();
}

test.describe('Tasks & Kanban', () => {
  test('sidebar has a TASKS section above "Folders & Notes", with All Tasks and Kanban Board rows', async ({ page }) => {
    await page.goto('/');

    const tasksNav = page.locator('#tasks-nav');
    const foldersHeader = page.locator('.sidebar-subheader', { hasText: 'Folders & Notes' });
    await expect(tasksNav).toBeVisible();
    await expect(page.locator('#tasks-nav-all-btn')).toContainText('All Tasks');
    await expect(page.locator('#tasks-nav-board-btn')).toContainText('Kanban Board');
    await expect(foldersHeader).toBeVisible();

    // DOM order, not just visual position - the TASKS block's bounding box
    // sits above the "Folders & Notes" sub-header's.
    const tasksBox = await tasksNav.boundingBox();
    const foldersBox = await foldersHeader.boundingBox();
    expect(tasksBox.y).toBeLessThan(foldersBox.y);
  });

  test('Kanban board renders the configured status columns', async ({ page, request, baseURL }) => {
    const title = uniqueName('kanban-column-check');
    const created = await createTaskViaApi(request, baseURL, { title });

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();

    const board = page.locator('#tasks-board-view');
    await expect(board).toBeVisible();
    await expect(page.locator('.tasks-board-column')).not.toHaveCount(0);
    await expect(page.locator(`.tasks-card[data-task-id="${created.id}"]`)).toBeVisible();

    await cleanupTask(request, baseURL, created);
  });

  test('"+ New Task" creates a task through the modal', async ({ page, request, baseURL }) => {
    const title = uniqueName('kanban-create-task');

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await page.locator('#tasks-board-new-btn').click();

    const modal = page.locator('#task-modal');
    await expect(modal).toBeVisible();
    await page.locator('#task-modal-title-input').fill(title);
    await page.locator('#task-modal-save-btn').click();
    await expect(modal).toBeHidden();

    const card = page.locator('.tasks-card', { hasText: title });
    await expect(card).toBeVisible();

    // Clean up via the API - we don't have the generated id from the UI
    // alone, so look it up by title.
    const listRes = await request.get(`${baseURL}/api/tasks?includeArchived=true`);
    const { tasks } = await listRes.json();
    const created = tasks.find((t) => t.title === title);
    await cleanupTask(request, baseURL, created);
  });

  test('dragging a card to another column persists the new status', async ({ page, request, baseURL }) => {
    const title = uniqueName('kanban-drag');
    const configRes = await request.get(`${baseURL}/api/tasks/config`);
    const config = await configRes.json();
    const fromStatus = config.statuses[0];
    const toStatus = config.statuses[1] || config.statuses[0];
    const created = await createTaskViaApi(request, baseURL, { title, status: fromStatus });

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();

    const card = page.locator(`.tasks-card[data-task-id="${created.id}"]`);
    const targetColumnCards = page.locator(`.tasks-board-column-cards[data-status="${toStatus}"]`);
    await expect(card).toBeVisible();

    // Native HTML5 DnD needs a real dispatched drag sequence, not a plain
    // `.dragTo()` (which Playwright itself notes can be unreliable for
    // custom dragover-driven drop zones like this board's drop indicator).
    await card.hover();
    await page.mouse.down();
    const targetBox = await targetColumnCards.boundingBox();
    await page.mouse.move(targetBox.x + targetBox.width / 2, targetBox.y + 10, { steps: 5 });
    await page.mouse.up();

    await expect(page.locator(`.tasks-board-column-cards[data-status="${toStatus}"] .tasks-card[data-task-id="${created.id}"]`)).toBeVisible();

    const refetched = await (await request.get(`${baseURL}/api/tasks/${created.id}`)).json();
    expect(refetched.status).toBe(toStatus);

    await cleanupTask(request, baseURL, created);
  });

  test('toggling an acceptance-criterion checkbox in the modal persists', async ({ page, request, baseURL }) => {
    const title = uniqueName('kanban-ac-toggle');
    const created = await createTaskViaApi(request, baseURL, {
      title,
      acceptanceCriteria: ['First criterion', 'Second criterion'],
    });

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await page.locator(`.tasks-card[data-task-id="${created.id}"]`).click();

    const modal = page.locator('#task-modal');
    await expect(modal).toBeVisible();
    const firstCheckbox = page.locator('#task-modal-ac-list .task-ac-item').first().locator('input[type="checkbox"]');
    await expect(firstCheckbox).not.toBeChecked();
    await firstCheckbox.check();
    await page.locator('#task-modal-save-btn').click();
    await expect(modal).toBeHidden();

    const refetched = await (await request.get(`${baseURL}/api/tasks/${created.id}`)).json();
    expect(refetched.acceptanceCriteria[0].checked).toBe(true);

    await cleanupTask(request, baseURL, created);
  });

  test('All Tasks list sorts by column headers (numeric-aware ID sort)', async ({ page, request, baseURL }) => {
    const titleA = uniqueName('list-sort-a');
    const titleB = uniqueName('list-sort-b');
    const first = await createTaskViaApi(request, baseURL, { title: titleA });
    const second = await createTaskViaApi(request, baseURL, { title: titleB });

    await page.goto('/');
    await page.locator('#tasks-nav-all-btn').click();
    await expect(page.locator('#tasks-list-view')).toBeVisible();
    await expect(page.locator('#tasks-list-tbody tr')).not.toHaveCount(0);

    const idHeader = page.locator('#tasks-list-table th[data-sort-key="id"] button');
    await idHeader.click(); // ascending
    const firstRowIdAsc = await page.locator('#tasks-list-tbody tr').first().locator('td').first().textContent();
    await idHeader.click(); // descending
    const firstRowIdDesc = await page.locator('#tasks-list-tbody tr').first().locator('td').first().textContent();
    expect(firstRowIdAsc).not.toBe(firstRowIdDesc);

    await cleanupTask(request, baseURL, first);
    await cleanupTask(request, baseURL, second);
  });

  test('Pomodoro widget: start/pause/reset and settings persist across a reload', async ({ page }) => {
    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();

    const widget = page.locator('#pomodoro-widget');
    await expect(widget).toBeVisible();
    const startPauseBtn = page.locator('#pomodoro-start-pause-btn');
    const time = page.locator('#pomodoro-time');

    await expect(startPauseBtn).toHaveText('Start');
    await startPauseBtn.click();
    await expect(startPauseBtn).toHaveText('Pause');
    await page.waitForTimeout(1200);
    const runningTime = await time.textContent();

    await startPauseBtn.click(); // pause
    await expect(startPauseBtn).toHaveText('Start');

    // Change a setting, then reload - both the paused state and the
    // setting should survive (localStorage-backed, docs §9).
    await page.locator('#pomodoro-settings-btn').click();
    await page.locator('#pomodoro-set-work').fill('30');
    await page.locator('#pomodoro-set-work').dispatchEvent('change');

    await page.reload();
    await page.locator('#tasks-nav-board-btn').click();
    await expect(page.locator('#pomodoro-start-pause-btn')).toHaveText('Start');
    await page.locator('#pomodoro-settings-btn').click();
    await expect(page.locator('#pomodoro-set-work')).toHaveValue('30');

    // Reset clears back to a fresh focus session.
    await page.locator('#pomodoro-reset-btn').click();
    await expect(time).not.toHaveText(runningTime);
  });
});

// ---------------------------------------------------------------------------
// Phase 12 QA hardening. Every task these tests create carries a per-run
// unique title/label so they never depend on (or disturb) other tests' tasks
// in the shared vault, and each cleans up the note files it created.
// ---------------------------------------------------------------------------

/** Opens a vault note by clicking its sidebar row, expanding its parent folders first. */
async function openNoteFromTree(page, notePath) {
  await page.goto('/');
  const parts = notePath.split('/');
  for (let i = 1; i < parts.length; i++) {
    const folder = parts.slice(0, i).join('/');
    const row = page.locator(`#file-tree [data-path="${folder}"]`);
    await expect(row).toBeVisible();
    if ((await row.getAttribute('aria-expanded')) !== 'true') await row.click();
  }
  const noteRow = page.locator(`#file-tree [data-path="${notePath}"]`);
  await expect(noteRow).toBeVisible();
  await noteRow.click();
  await expect(page.locator('#note-editor-view')).toBeVisible();
}

async function getNote(request, baseURL, path) {
  const res = await request.get(`${baseURL}/api/notes/${path.split('/').map(encodeURIComponent).join('/')}`);
  return res.ok() ? res.json() : null;
}

async function putNote(request, baseURL, path, content) {
  const res = await request.put(`${baseURL}/api/notes/${path.split('/').map(encodeURIComponent).join('/')}`, { data: { content } });
  expect(res.ok(), `PUT ${path}: ${res.status()}`).toBeTruthy();
  return res.json();
}

async function bg(locator) {
  return locator.evaluate((el) => getComputedStyle(el).backgroundColor);
}

test.describe('Tasks & Kanban - QA hardening', () => {
  test('sidebar right-click "Convert to task" turns a plain note into a task (renamed, body kept as description)', async ({ page, request, baseURL, api }) => {
    const stem = uniqueName('convert-me');
    const notePath = `${stem}.md`;
    await api.saveNote(notePath, `# Heading\n\nSome plain body text for ${stem}.\n`);

    await page.goto('/');
    const row = page.locator(`#file-tree [data-path="${notePath}"]`);
    await expect(row).toBeVisible();
    await row.click({ button: 'right' });
    await expect(page.locator('#tree-context-convert-to-task')).toBeVisible();
    await page.locator('#tree-context-convert-to-task').click();

    // Conversion opens the task modal for the freshly-created task.
    const modal = page.locator('#task-modal');
    await expect(modal).toBeVisible();
    await expect(page.locator('#task-modal-title-input')).toHaveValue(stem);
    await expect(page.locator('#task-modal-description')).toHaveValue(new RegExp(`Some plain body text for ${stem}`));
    await page.locator('#task-modal-cancel-btn').click();

    const { tasks } = await (await request.get(`${baseURL}/api/tasks`)).json();
    const task = tasks.find((t) => t.title === stem);
    expect(task, 'converted task should be listed').toBeTruthy();
    expect(task.path).toBe(`${task.id} - ${stem}.md`);

    // Old file name is gone from the tree, the new one is present, and the
    // converted note no longer offers "Convert to task".
    await expect(page.locator(`#file-tree [data-path="${notePath}"]`)).toHaveCount(0);
    const newRow = page.locator(`#file-tree [data-path="${task.path}"]`);
    await expect(newRow).toBeVisible();
    await newRow.click({ button: 'right' });
    await expect(page.locator('#tree-context-menu')).toBeVisible();
    await expect(page.locator('#tree-context-convert-to-task')).toHaveCount(0);
    await page.keyboard.press('Escape');

    // On disk: real frontmatter, original body preserved.
    const onDisk = await getNote(request, baseURL, task.path);
    expect(onDisk.content).toMatch(/^---\nid: TASK-\d+\n/);
    expect(onDisk.content).toContain(`Some plain body text for ${stem}.`);

    await api.deleteNote(task.path);
  });

  test('editing a task note\'s inline title renames the file and frontmatter title', async ({ page, request, baseURL, api }) => {
    const title = uniqueName('inline-rename');
    const task = await createTaskViaApi(request, baseURL, { title, description: 'body' });
    await openNoteFromTree(page, task.path);

    const newTitle = uniqueName('inline-renamed');
    const titleInput = page.locator('#note-title-input');
    await expect(titleInput).toHaveValue(`${task.id} - ${title}`);
    await titleInput.fill(`${task.id} - ${newTitle}`);
    await titleInput.press('Enter');

    const newPath = `Task/${task.id} - ${newTitle}.md`;
    await expect(page.locator(`#file-tree [data-path="${newPath}"]`)).toBeVisible();
    await expect(page.locator(`#file-tree [data-path="${task.path}"]`)).toHaveCount(0);
    await expect(titleInput).toHaveValue(`${task.id} - ${newTitle}`);

    const refetched = await (await request.get(`${baseURL}/api/tasks/${task.id}`)).json();
    expect(refetched.title).toBe(newTitle);
    expect(refetched.path).toBe(newPath);
    const onDisk = await getNote(request, baseURL, newPath);
    expect(onDisk.content).toContain(`title: ${newTitle}`);
    expect(onDisk.content).not.toContain(`${task.id} - ${task.id}`);

    await api.deleteNote(newPath);
  });

  test('a task note\'s preview hides the YAML block and shows the task header', async ({ page, request, baseURL, api }) => {
    const title = uniqueName('preview-task');
    const task = await createTaskViaApi(request, baseURL, {
      title,
      description: 'Visible description text',
      labels: ['preview-label'],
      priority: 'high',
    });
    await openNoteFromTree(page, task.path);
    await page.locator('[data-view-mode="split"]').click();

    const header = page.locator('#task-preview-header');
    await expect(header).toBeVisible();
    await expect(header).toContainText(task.id);
    await expect(header).toContainText('Backlog');
    await expect(header).toContainText('high');
    await expect(header).toContainText('preview-label');

    const preview = page.locator('#preview');
    await expect(preview).toContainText('Visible description text');
    const previewText = await preview.innerText();
    expect(previewText).not.toContain('created_date');
    expect(previewText).not.toContain(`id: ${task.id}`);
    expect(previewText).not.toMatch(/^---/m);

    await api.deleteNote(task.path);
  });

  test('a plain note that starts with --- but is not a task renders exactly as before (no task header)', async ({ page, api }) => {
    const notePath = `${uniqueName('plain-frontmatter')}.md`;
    await api.saveNote(notePath, '---\ntitle: Not a task\ntags: [a, b]\n---\n\nPlain body paragraph.\n');
    await openNoteFromTree(page, notePath);
    await page.locator('[data-view-mode="split"]').click();

    await expect(page.locator('#preview')).toContainText('Plain body paragraph.');
    // The front-matter text is NOT swallowed for non-tasks (unchanged behaviour).
    await expect(page.locator('#preview')).toContainText('Not a task');
    await expect(page.locator('#task-preview-header')).toBeHidden();
    // And the editor still has the raw content untouched.
    await expect(page.locator('#editor')).toHaveValue(/^---\ntitle: Not a task/);

    await api.deleteNote(notePath);
  });

  test('editor 409 conflict: another writer changed the note, "Reload latest" shows the API content and nothing is overwritten', async ({ page, request, baseURL, api }) => {
    const notePath = `${uniqueName('conflict-reload')}.md`;
    await api.saveNote(notePath, 'original content');
    await openNoteFromTree(page, notePath);
    await page.locator('[data-view-mode="edit"]').click();
    const editor = page.locator('#editor');
    await expect(editor).toHaveValue('original content');

    // Someone else (board/MCP/other tab) saves a newer version.
    await page.waitForTimeout(50);
    await putNote(request, baseURL, notePath, 'content from the API');

    await editor.click();
    await page.keyboard.press('End');
    await page.keyboard.type(' plus my local typing');

    const confirmModal = page.locator('#rename-modal');
    await expect(confirmModal).toBeVisible({ timeout: 8_000 }); // autosave debounce (1.5 s) then 409
    await expect(page.locator('#rename-modal-title')).toHaveText('This note changed elsewhere');

    await page.locator('#rename-modal-confirm-btn').click(); // "Reload latest"
    await expect(confirmModal).toBeHidden();
    await expect(editor).toHaveValue('content from the API');

    // The API's version is still what is on disk - the local typing was discarded, not force-saved.
    const onDisk = await getNote(request, baseURL, notePath);
    expect(onDisk.content).toBe('content from the API');

    await api.deleteNote(notePath);
  });

  test('editor 409 conflict: choosing overwrite saves the local edits', async ({ page, request, baseURL, api }) => {
    const notePath = `${uniqueName('conflict-overwrite')}.md`;
    await api.saveNote(notePath, 'original content');
    await openNoteFromTree(page, notePath);
    await page.locator('[data-view-mode="edit"]').click();
    const editor = page.locator('#editor');

    await page.waitForTimeout(50);
    await putNote(request, baseURL, notePath, 'content from the API');
    await editor.click();
    await page.keyboard.press('End');
    await page.keyboard.type(' plus my local typing');

    const confirmModal = page.locator('#rename-modal');
    await expect(confirmModal).toBeVisible({ timeout: 8_000 });
    await page.locator('#rename-modal-cancel-btn').click(); // keep local edits -> force save
    await expect(confirmModal).toBeHidden();

    await expect
      .poll(async () => (await getNote(request, baseURL, notePath)).content, { timeout: 8_000 })
      .toBe('original content plus my local typing');

    await api.deleteNote(notePath);
  });

  test('reordering a card within its column persists (and survives a reload)', async ({ page, request, baseURL, api }) => {
    const status = 'Done';
    const a = await createTaskViaApi(request, baseURL, { title: uniqueName('reorder-a'), status });
    const b = await createTaskViaApi(request, baseURL, { title: uniqueName('reorder-b'), status });
    const c = await createTaskViaApi(request, baseURL, { title: uniqueName('reorder-c'), status });

    const orderOf = async () => {
      const board = await (await request.get(`${baseURL}/api/tasks/board`)).json();
      const col = board.columns.find((x) => x.status === status);
      return col.tasks.map((t) => t.id).filter((id) => [a.id, b.id, c.id].includes(id));
    };
    expect(await orderOf()).toEqual([a.id, b.id, c.id]);

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    const cardC = page.locator(`.tasks-card[data-task-id="${c.id}"]`);
    const cardA = page.locator(`.tasks-card[data-task-id="${a.id}"]`);
    await expect(cardC).toBeVisible();
    await cardC.scrollIntoViewIfNeeded();

    // Drag C onto the top half of A (drop index = A's position).
    await cardC.hover();
    await page.mouse.down();
    const aBox = await cardA.boundingBox();
    await page.mouse.move(aBox.x + aBox.width / 2, aBox.y + 4, { steps: 8 });
    await page.mouse.up();

    await expect.poll(orderOf).toEqual([c.id, a.id, b.id]);

    await page.reload();
    await page.locator('#tasks-nav-board-btn').click();
    const domOrder = await page
      .locator(`.tasks-board-column-cards[data-status="${status}"] .tasks-card`)
      .evaluateAll((els) => els.map((e) => e.dataset.taskId));
    expect(domOrder.filter((id) => [a.id, b.id, c.id].includes(id))).toEqual([c.id, a.id, b.id]);

    for (const t of [a, b, c]) await api.deleteNote(t.path);
  });

  test('board filters (label, text, priority) narrow the cards and column counts', async ({ page, request, baseURL, api }) => {
    const label = uniqueName('flt').replace(/[^a-z0-9-]/gi, '');
    const keep = await createTaskViaApi(request, baseURL, { title: uniqueName('filter-keep'), labels: [label], priority: 'high', assignee: ['@filter-user'] });
    const drop = await createTaskViaApi(request, baseURL, { title: uniqueName('filter-drop'), labels: ['other-label'], priority: 'low' });

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    const cardKeep = page.locator(`.tasks-card[data-task-id="${keep.id}"]`);
    const cardDrop = page.locator(`.tasks-card[data-task-id="${drop.id}"]`);
    await expect(cardKeep).toBeVisible();
    await expect(cardDrop).toBeVisible();

    // Label filter.
    await page.locator('#tasks-board-label-filter').selectOption(label);
    await expect(cardKeep).toBeVisible();
    await expect(cardDrop).toHaveCount(0);
    const backlogCount = page.locator('.tasks-board-column[data-status="Backlog"] .tasks-board-column-count');
    await expect(backlogCount).toHaveText('1');

    // Clearing restores both.
    await page.locator('#tasks-board-label-filter').selectOption('');
    await expect(cardDrop).toBeVisible();

    // Priority filter.
    await page.locator('#tasks-board-priority-filter').selectOption('low');
    await expect(cardDrop).toBeVisible();
    await expect(cardKeep).toHaveCount(0);
    await page.locator('#tasks-board-priority-filter').selectOption('');

    // Assignee filter.
    await page.locator('#tasks-board-assignee-filter').selectOption('@filter-user');
    await expect(cardKeep).toBeVisible();
    await expect(cardDrop).toHaveCount(0);
    await page.locator('#tasks-board-assignee-filter').selectOption('');

    // Text filter (debounced).
    await page.locator('#tasks-board-search').fill('filter-drop');
    await expect(cardDrop).toBeVisible();
    await expect(cardKeep).toHaveCount(0);

    for (const t of [keep, drop]) await api.deleteNote(t.path);
  });

  test('board and list views are mutually exclusive, and the Pomodoro settings popover starts closed', async ({ page }) => {
    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await expect(page.locator('#tasks-board-view')).toBeVisible();
    await expect(page.locator('#tasks-list-view')).toBeHidden();
    await expect(page.locator('.pomodoro-settings-popover')).toBeHidden();
    await page.locator('#pomodoro-settings-btn').click();
    await expect(page.locator('.pomodoro-settings-popover')).toBeVisible();

    await page.locator('#tasks-nav-all-btn').click();
    await expect(page.locator('#tasks-list-view')).toBeVisible();
    await expect(page.locator('#tasks-board-view')).toBeHidden();
  });

  test('dark theme restyles the board (column and card backgrounds differ from light)', async ({ page, request, baseURL, api }) => {
    const task = await createTaskViaApi(request, baseURL, { title: uniqueName('theme-card'), description: 'x' });

    await page.goto('/');
    await page.evaluate(() => localStorage.setItem('dotnotes-theme', 'light'));
    await page.reload();
    await page.locator('#tasks-nav-board-btn').click();

    const column = page.locator('.tasks-board-column').first();
    const card = page.locator(`.tasks-card[data-task-id="${task.id}"]`);
    await expect(card).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    const [colLight, cardLight] = [await bg(column), await bg(card)];

    await page.locator('#theme-toggle-btn').click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    const [colDark, cardDark] = [await bg(column), await bg(card)];

    expect(colDark).not.toBe(colLight);
    expect(cardDark).not.toBe(cardLight);

    await page.locator('#theme-toggle-btn').click(); // restore for later specs
    await api.deleteNote(task.path);
  });

  test('cards show id/title/excerpt/assignee/labels/AC/created date; column and sidebar counts match', async ({ page, request, baseURL, api }) => {
    const title = uniqueName('card-fields');
    const task = await createTaskViaApi(request, baseURL, {
      title,
      description: 'Excerpt text that should appear on the card',
      assignee: ['@carol'],
      labels: ['card-label'],
      priority: 'medium',
      acceptanceCriteria: ['one', 'two'],
    });

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    const card = page.locator(`.tasks-card[data-task-id="${task.id}"]`);
    await expect(card).toBeVisible();
    await expect(card.locator('.tasks-card-id')).toHaveText(task.id);
    await expect(card.locator('.tasks-card-title')).toHaveText(title);
    await expect(card.locator('.tasks-card-excerpt')).toContainText('Excerpt text that should appear');
    await expect(card.locator('.tasks-card-assignee')).toHaveText('@carol');
    await expect(card.locator('.tasks-chip')).toHaveText('card-label');
    await expect(card.locator('.tasks-card-ac')).toContainText('0/2');
    await expect(card.locator('.tasks-card-date')).not.toHaveText('');
    await expect(card.locator('.tasks-priority-badge')).toHaveText('medium');

    // Column count badges == cards in each column; sidebar counts == active tasks.
    const columns = page.locator('.tasks-board-column');
    const n = await columns.count();
    let total = 0;
    for (let i = 0; i < n; i++) {
      const col = columns.nth(i);
      const badge = Number(await col.locator('.tasks-board-column-count').textContent());
      expect(badge).toBe(await col.locator('.tasks-card').count());
      total += badge;
    }
    const { tasks } = await (await request.get(`${baseURL}/api/tasks`)).json();
    expect(total).toBe(tasks.length);
    await expect(page.locator('#tasks-nav-board-count')).toHaveText(String(tasks.length));
    await expect(page.locator('#tasks-nav-all-count')).toHaveText(String(tasks.length));

    await api.deleteNote(task.path);
  });

  test('task modal edits every field, ticks AC, "Open note" navigates to the note, Complete moves it out of the board', async ({ page, request, baseURL, api }) => {
    const title = uniqueName('modal-all');
    const task = await createTaskViaApi(request, baseURL, { title, acceptanceCriteria: ['first', 'second'] });

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await page.locator(`.tasks-card[data-task-id="${task.id}"]`).click();
    const modal = page.locator('#task-modal');
    await expect(modal).toBeVisible();

    const newTitle = `${title}-edited`;
    await page.locator('#task-modal-title-input').fill(newTitle);
    await page.locator('#task-modal-status').selectOption('In Progress');
    await page.locator('#task-modal-priority').selectOption('high');
    await page.locator('#task-modal-milestone').fill('v9.9');
    await page.locator('#task-modal-assignee-chips .task-chip-text-input').fill('@dave');
    await page.locator('#task-modal-assignee-chips .task-chip-text-input').press('Enter');
    await page.locator('#task-modal-labels-chips .task-chip-text-input').fill('lab-1');
    await page.locator('#task-modal-labels-chips .task-chip-text-input').press('Enter');
    await page.locator('#task-modal-description').fill('New **description**');
    await page.locator('.task-modal-more summary').click(); // plan/notes/summary live under "More"
    await page.locator('#task-modal-plan').fill('The plan');
    await page.locator('#task-modal-notes').fill('Some notes');
    await page.locator('#task-modal-summary').fill('Final words');
    await page.locator('#task-modal-ac-list .task-ac-item').nth(1).locator('input[type="checkbox"]').check();
    await page.locator('#task-modal-ac-new').fill('third added');
    await page.locator('#task-modal-ac-add-btn').click();
    await page.locator('#task-modal-save-btn').click();
    await expect(modal).toBeHidden();

    const saved = await (await request.get(`${baseURL}/api/tasks/${task.id}`)).json();
    expect(saved).toMatchObject({
      title: newTitle,
      status: 'In Progress',
      priority: 'high',
      milestone: 'v9.9',
      assignee: ['@dave'],
      labels: ['lab-1'],
      implementationPlan: 'The plan',
      implementationNotes: 'Some notes',
      finalSummary: 'Final words',
    });
    expect(saved.description.trim()).toBe('New **description**');
    expect(saved.acceptanceCriteria.map((a) => [a.text, a.checked])).toEqual([['first', false], ['second', true], ['third added', false]]);
    expect(saved.path).toBe(`Task/${task.id} - ${newTitle}.md`); // title change renamed the file

    // "Open note" link opens the note in the editor.
    await page.locator(`.tasks-card[data-task-id="${task.id}"]`).click();
    await expect(modal).toBeVisible();
    await page.locator('#task-modal-open-note-link').click();
    await expect(modal).toBeHidden();
    await expect(page.locator('#note-editor-view')).toBeVisible();
    await expect(page.locator('#editor')).toHaveValue(/^---\nid: /);
    await expect(page.locator('#note-title-input')).toHaveValue(`${task.id} - ${newTitle}`);

    // Complete from the board.
    await page.locator('#tasks-nav-board-btn').click();
    await page.locator(`.tasks-card[data-task-id="${task.id}"]`).click();
    await page.locator('#task-modal-complete-btn').click();
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(page.locator(`.tasks-card[data-task-id="${task.id}"]`)).toHaveCount(0);
    const completed = await (await request.get(`${baseURL}/api/tasks/${task.id}`)).json();
    expect(completed.completed).toBe(true);
    expect(completed.path).toBe(`Task/Completed/${task.id} - ${newTitle}.md`);

    // Still visible in All Tasks with "show completed".
    await page.locator('#tasks-nav-all-btn').click();
    await expect(page.locator('#tasks-list-tbody tr', { hasText: task.id })).toHaveCount(0);
    await page.locator('#tasks-list-show-completed').check();
    await expect(page.locator('#tasks-list-tbody tr', { hasText: task.id })).toHaveCount(1);

    await api.deleteNote(completed.path);
  });

  test('modal save merges with a concurrent change made elsewhere (patch semantics: untouched fields are not overwritten)', async ({ page, request, baseURL, api }) => {
    const task = await createTaskViaApi(request, baseURL, { title: uniqueName('merge'), description: 'original description' });

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await page.locator(`.tasks-card[data-task-id="${task.id}"]`).click();
    await expect(page.locator('#task-modal')).toBeVisible();

    // Meanwhile another client changes the description...
    const res = await request.patch(`${baseURL}/api/tasks/${task.id}`, { data: { description: 'changed by MCP', milestone: 'from-mcp' } });
    expect(res.ok()).toBeTruthy();

    // ...and the user only edits the priority in the (stale) modal.
    await page.locator('#task-modal-priority').selectOption('low');
    await page.locator('#task-modal-save-btn').click();
    await expect(page.locator('#task-modal')).toBeHidden();

    const merged = await (await request.get(`${baseURL}/api/tasks/${task.id}`)).json();
    expect(merged.priority).toBe('low');
    expect(merged.description.trim()).toBe('changed by MCP');
    expect(merged.milestone).toBe('from-mcp');

    await api.deleteNote(merged.path);
  });

  test('board picks up a change made outside the UI (revision polling) without a reload', async ({ page, request, baseURL, api }) => {
    const task = await createTaskViaApi(request, baseURL, { title: uniqueName('live-refresh') });
    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await expect(page.locator(`.tasks-board-column[data-status="Backlog"] .tasks-card[data-task-id="${task.id}"]`)).toBeVisible();

    const res = await request.post(`${baseURL}/api/tasks/${task.id}/move`, { data: { status: 'Done' } });
    expect(res.ok()).toBeTruthy();
    await expect(page.locator(`.tasks-board-column[data-status="Done"] .tasks-card[data-task-id="${task.id}"]`)).toBeVisible({ timeout: 8_000 });

    // Editing the file directly (as VS Code / a sync tool would) is picked up too.
    const note = await getNote(request, baseURL, task.path);
    await putNote(request, baseURL, task.path, note.content.replace('status: Done', 'status: In Progress'));
    await expect(page.locator(`.tasks-board-column[data-status="In Progress"] .tasks-card[data-task-id="${task.id}"]`)).toBeVisible({ timeout: 10_000 });

    await api.deleteNote(task.path);
  });

  test('All Tasks: status filter and text filter narrow the table', async ({ page, request, baseURL, api }) => {
    const a = await createTaskViaApi(request, baseURL, { title: uniqueName('list-todo'), status: 'To Do' });
    const b = await createTaskViaApi(request, baseURL, { title: uniqueName('list-done'), status: 'Done' });

    await page.goto('/');
    await page.locator('#tasks-nav-all-btn').click();
    await expect(page.locator('#tasks-list-tbody tr', { hasText: a.id })).toHaveCount(1);
    await expect(page.locator('#tasks-list-tbody tr', { hasText: b.id })).toHaveCount(1);

    await page.locator('#tasks-list-status-filter').selectOption('Done');
    await expect(page.locator('#tasks-list-tbody tr', { hasText: b.id })).toHaveCount(1);
    await expect(page.locator('#tasks-list-tbody tr', { hasText: a.id })).toHaveCount(0);
    await page.locator('#tasks-list-status-filter').selectOption('');

    await page.locator('#tasks-list-search').fill('list-todo');
    await expect(page.locator('#tasks-list-tbody tr', { hasText: a.id })).toHaveCount(1);
    await expect(page.locator('#tasks-list-tbody tr', { hasText: b.id })).toHaveCount(0);

    // Sorting by Title toggles direction.
    await page.locator('#tasks-list-search').fill('');
    await page.locator('#tasks-list-table th[data-sort-key="title"] button').click();
    const asc = await page.locator('#tasks-list-tbody tr td:nth-child(2)').allTextContents();
    expect(asc).toEqual([...asc].sort((x, y) => x.localeCompare(y, undefined, { sensitivity: 'base' })));

    for (const t of [a, b]) await api.deleteNote(t.path);
  });

  test('Pomodoro: keeps running across navigation, shows the nav indicator, advances phase with chime + notification, skip and cycle settings work', async ({ page }) => {
    // Record chimes/notifications instead of needing real audio/permission.
    await page.addInitScript(() => {
      window.__chimes = 0;
      window.__notifications = [];
      const Ctx = window.AudioContext || window.webkitAudioContext;
      if (Ctx) {
        const orig = Ctx.prototype.createOscillator;
        Ctx.prototype.createOscillator = function () { window.__chimes++; return orig.call(this); };
      }
      window.Notification = class { constructor(title, o) { window.__notifications.push([title, o && o.body]); } static get permission() { return 'granted'; } static requestPermission() { return Promise.resolve('granted'); } };
    });
    await page.goto('/');
    await page.evaluate(() => {
      localStorage.setItem('dotnotes-pomodoro-settings', JSON.stringify({ workMin: 25, shortBreakMin: 5, longBreakMin: 15, cyclesBeforeLong: 2, soundOn: true, notifyOn: true, autoStart: false, linkedTaskId: null }));
      localStorage.removeItem('dotnotes-pomodoro-state');
    });
    await page.reload();
    await page.locator('#tasks-nav-board-btn').click();

    await expect(page.locator('#pomodoro-phase')).toHaveText('Focus');
    await expect(page.locator('#pomodoro-time')).toHaveText('25:00');
    await page.locator('#pomodoro-start-pause-btn').click();
    await expect(page.locator('#pomodoro-start-pause-btn')).toHaveText('Pause');

    // Navigate away in-app: the timer keeps going, the top-nav indicator appears.
    await page.locator('#tasks-nav-all-btn').click();
    const indicator = page.locator('#pomodoro-nav-indicator');
    await expect(indicator).toBeVisible();
    await expect(indicator).toContainText('Focus');
    // Clicking it returns to the board with the timer still running.
    await indicator.click();
    await expect(page.locator('#tasks-board-view')).toBeVisible();
    await expect(page.locator('#pomodoro-start-pause-btn')).toHaveText('Pause');
    await expect(page.locator('#pomodoro-time')).not.toHaveText('25:00');

    // Fast-forward: make the running phase expire in ~1.5 s (state is keyed on an absolute endsAt).
    await page.evaluate(() => {
      const st = JSON.parse(localStorage.getItem('dotnotes-pomodoro-state'));
      st.endsAt = Date.now() + 1500;
      st.seq = (st.seq || 0) + 5;
      st.updatedAt = Date.now();
      localStorage.setItem('dotnotes-pomodoro-state', JSON.stringify(st));
    });
    await page.reload();
    await page.locator('#tasks-nav-board-btn').click();
    await expect(page.locator('#pomodoro-phase')).toHaveText('Short break', { timeout: 8_000 });
    await expect(page.locator('#pomodoro-time')).toHaveText('05:00');
    await expect(page.locator('#pomodoro-start-pause-btn')).toHaveText('Start'); // autoStart is off
    await expect(page.locator('.pomodoro-cycle-dot-filled')).toHaveCount(1);
    expect(await page.evaluate(() => window.__notifications.length)).toBeGreaterThanOrEqual(1);
    expect(await page.evaluate(() => window.__notifications[0][1])).toContain('Short break');

    // Skip: break -> focus; skip again: focus (2nd cycle) -> LONG break (cyclesBeforeLong = 2).
    await page.locator('#pomodoro-skip-btn').click();
    await expect(page.locator('#pomodoro-phase')).toHaveText('Focus');
    await page.locator('#pomodoro-skip-btn').click();
    await expect(page.locator('#pomodoro-phase')).toHaveText('Long break');
    await expect(page.locator('#pomodoro-time')).toHaveText('15:00');
    await expect(page.locator('.pomodoro-cycle-dot')).toHaveCount(2);

    // Durations are adjustable and persisted.
    await page.locator('#pomodoro-settings-btn').click();
    await page.locator('#pomodoro-set-long').fill('20');
    await page.locator('#pomodoro-set-long').dispatchEvent('change');
    await expect(page.locator('#pomodoro-time')).toHaveText('20:00');
    const stored = await page.evaluate(() => JSON.parse(localStorage.getItem('dotnotes-pomodoro-settings')));
    expect(stored).toMatchObject({ longBreakMin: 20, cyclesBeforeLong: 2, soundOn: true, notifyOn: true });
  });

  test('column "+" presets that column\'s status, and the modal validates a missing title', async ({ page, request, baseURL, api }) => {
    const title = uniqueName('col-plus');
    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await page.locator('.tasks-board-column[data-status="In Progress"] .tasks-board-column-add-btn').click();
    await expect(page.locator('#task-modal')).toBeVisible();
    await expect(page.locator('#task-modal-status')).toHaveValue('In Progress');

    // Empty title: blocked client-side with a visible message, nothing created.
    await page.locator('#task-modal-save-btn').click();
    await expect(page.locator('#task-modal-error')).toBeVisible();
    await expect(page.locator('#task-modal')).toBeVisible();

    await page.locator('#task-modal-title-input').fill(title);
    await page.locator('#task-modal-save-btn').click();
    await expect(page.locator('#task-modal')).toBeHidden();
    const card = page.locator('.tasks-board-column[data-status="In Progress"] .tasks-card', { hasText: title });
    await expect(card).toBeVisible();

    const { tasks } = await (await request.get(`${baseURL}/api/tasks`)).json();
    await api.deleteNote(tasks.find((t) => t.title === title).path);
  });

  test('reordering inside a FILTERED column places the card between the visible neighbours (hidden cards above do not shift it)', async ({ page, request, baseURL, api }) => {
    const status = 'Done';
    const label = uniqueName('fdnd').replace(/[^a-z0-9-]/gi, '');
    // A hidden (non-matching) card sits ABOVE the visible ones: a naive
    // "index among visible cards" sent to the server would count it wrongly.
    const hidden = await createTaskViaApi(request, baseURL, { title: uniqueName('f-hidden'), status, labels: ['zzz-other'] });
    const a = await createTaskViaApi(request, baseURL, { title: uniqueName('f-a'), status, labels: [label] });
    const b = await createTaskViaApi(request, baseURL, { title: uniqueName('f-b'), status, labels: [label] });
    const c = await createTaskViaApi(request, baseURL, { title: uniqueName('f-c'), status, labels: [label] });
    const mine = [hidden.id, a.id, b.id, c.id];

    const orderOf = async () => {
      const board = await (await request.get(`${baseURL}/api/tasks/board`)).json();
      return board.columns.find((x) => x.status === status).tasks.map((t) => t.id).filter((id) => mine.includes(id));
    };
    expect(await orderOf()).toEqual([hidden.id, a.id, b.id, c.id]);

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await page.locator('#tasks-board-label-filter').selectOption(label);
    const cardC = page.locator(`.tasks-card[data-task-id="${c.id}"]`);
    const cardB = page.locator(`.tasks-card[data-task-id="${b.id}"]`);
    await expect(page.locator(`.tasks-card[data-task-id="${hidden.id}"]`)).toHaveCount(0);

    await cardC.hover();
    await page.mouse.down();
    const bBox = await cardB.boundingBox();
    await page.mouse.move(bBox.x + bBox.width / 2, bBox.y + 4, { steps: 8 });
    await page.mouse.up();

    // Visible order must now be A, C, B - i.e. full order hidden, A, C, B.
    await expect.poll(orderOf).toEqual([hidden.id, a.id, c.id, b.id]);

    for (const t of [hidden, a, b, c]) await api.deleteNote(t.path);
  });
});
