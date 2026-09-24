// v0.2.1 Tasks & Kanban changes (see the shared contract doc for this
// release): Task/ folder rename, Backlog-first columns with a raw-status
// badge instead of "unlisted" columns, Complete (replacing Archive) with a
// Completed subfolder + collision suffixing, and the "+New"/folder
// right-click Task entry points. Follows specs/fixtures.js's conventions.
const { test, expect, uniqueName, ensureExpanded } = require('./fixtures');

async function createTaskViaApi(request, baseURL, body) {
  const res = await request.post(`${baseURL}/api/tasks`, { data: body });
  expect(res.ok(), `POST /api/tasks failed: ${res.status()} ${await res.text()}`).toBeTruthy();
  return res.json();
}

async function getTask(request, baseURL, id) {
  return (await request.get(`${baseURL}/api/tasks/${id}`)).json();
}

async function bg(locator) {
  return locator.evaluate((el) => getComputedStyle(el).backgroundColor);
}

/** Real mouse drag (native HTML5 DnD needs a real pointer sequence, not `.dragTo()`). */
async function dragCard(page, cardLocator, targetLocator, yFraction = 0.5) {
  await cardLocator.hover();
  await page.mouse.down();
  const box = await targetLocator.boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height * yFraction, { steps: 8 });
  await page.mouse.up();
}

test.describe('Tasks & Kanban v0.2.1', () => {
  test('"+ New" > New Task opens the task modal with folder "Task" read-only and creates the note under Task/ in Backlog', async ({ page, request, baseURL, api }) => {
    const title = uniqueName('new-menu-task');

    await page.goto('/');
    await page.locator('#new-menu-btn').click();
    await expect(page.locator('#new-menu')).toBeVisible();
    await page.locator('#new-menu-task').click();

    const modal = page.locator('#task-modal');
    await expect(modal).toBeVisible();
    await expect(page.locator('#task-modal-folder-row')).toBeVisible();
    await expect(page.locator('#task-modal-folder-value')).toHaveText('Task');

    await page.locator('#task-modal-title-input').fill(title);
    await page.locator('#task-modal-save-btn').click();
    await expect(modal).toBeHidden();

    const listRes = await request.get(`${baseURL}/api/tasks?includeCompleted=true`);
    const { tasks } = await listRes.json();
    const created = tasks.find((t) => t.title === title);
    expect(created, 'task should have been created').toBeTruthy();
    expect(created.path).toMatch(/^Task\/TASK-\d+ - .*\.md$/);
    expect(created.status).toBe('Backlog');

    await page.locator('#refresh-tree-btn').click();
    await ensureExpanded(page.locator('#file-tree [data-path="Task"]'));
    await expect(page.locator(`#file-tree [data-path="${created.path}"]`)).toBeVisible();

    await page.locator('#tasks-nav-board-btn').click();
    const backlogColumn = page.locator('.tasks-board-column-backlog');
    await expect(backlogColumn.locator(`.tasks-card[data-task-id="${created.id}"]`)).toBeVisible();

    await api.deleteNote(created.path);
  });

  test('folder right-click New Note creates inside the folder with a locked prefix; New Task shows that folder read-only and the note appears on the board', async ({ page, request, baseURL, api }) => {
    const folder = uniqueName('ctx-folder');
    await api.createFolder(folder);

    await page.goto('/');
    await page.locator(`#file-tree [data-path="${folder}"]`).click({ button: 'right' });
    const menu = page.locator('#tree-context-menu');
    await expect(menu).toBeVisible();
    await expect(page.locator('#tree-context-new-note')).toBeVisible();
    await expect(page.locator('#tree-context-new-task')).toBeVisible();

    // New Note: locked folder prefix, only asks for a name.
    await page.locator('#tree-context-new-note').click();
    const renameModal = page.locator('#rename-modal');
    await expect(renameModal).toBeVisible();
    await expect(page.locator('#rename-modal-locked-prefix')).toHaveText(`In folder: ${folder}`);
    const noteName = uniqueName('ctx-note');
    await page.locator('#rename-modal-input').fill(noteName);
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(renameModal).toBeHidden();
    const notePath = `${folder}/${noteName}.md`;
    await expect(page.locator(`#file-tree [data-path="${notePath}"]`)).toBeVisible();

    // New Task: read-only folder row, note lands in that folder (not Task/).
    await page.locator(`#file-tree [data-path="${folder}"]`).click({ button: 'right' });
    await expect(menu).toBeVisible();
    await page.locator('#tree-context-new-task').click();
    const taskModal = page.locator('#task-modal');
    await expect(taskModal).toBeVisible();
    await expect(page.locator('#task-modal-folder-value')).toHaveText(folder);
    const taskTitle = uniqueName('ctx-task');
    await page.locator('#task-modal-title-input').fill(taskTitle);
    await page.locator('#task-modal-save-btn').click();
    await expect(taskModal).toBeHidden();

    const listRes = await request.get(`${baseURL}/api/tasks?includeCompleted=true`);
    const { tasks } = await listRes.json();
    const created = tasks.find((t) => t.title === taskTitle);
    expect(created.path).toBe(`${folder}/${created.id} - ${taskTitle}.md`);

    await page.locator('#tasks-nav-board-btn').click();
    await expect(page.locator(`.tasks-card[data-task-id="${created.id}"]`)).toBeVisible();

    await api.deleteNote(notePath);
    await api.deleteNote(created.path);
  });

  test('card Complete button (green, --color-success in both themes) moves the note into Completed/, hides it from the board, and does not open the modal', async ({ page, request, baseURL, api }) => {
    const task = await createTaskViaApi(request, baseURL, { title: uniqueName('complete-card') });

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    const card = page.locator(`.tasks-card[data-task-id="${task.id}"]`);
    await expect(card).toBeVisible();
    const completeBtn = card.locator('.tasks-card-complete-btn');

    // Colour check in both themes.
    await page.evaluate(() => localStorage.setItem('dotnotes-theme', 'light'));
    await page.reload();
    await page.locator('#tasks-nav-board-btn').click();
    await expect(card).toBeVisible();
    const successLight = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--color-success').trim());
    const btnBgLight = await bg(completeBtn);
    const successLightRgb = await page.evaluate((c) => {
      const d = document.createElement('div');
      d.style.color = c;
      document.body.appendChild(d);
      const rgb = getComputedStyle(d).color;
      d.remove();
      return rgb;
    }, successLight);
    expect(btnBgLight).toBe(successLightRgb);

    await page.locator('#theme-toggle-btn').click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    const successDark = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--color-success').trim());
    const btnBgDark = await bg(completeBtn);
    const successDarkRgb = await page.evaluate((c) => {
      const d = document.createElement('div');
      d.style.color = c;
      document.body.appendChild(d);
      const rgb = getComputedStyle(d).color;
      d.remove();
      return rgb;
    }, successDark);
    expect(btnBgDark).toBe(successDarkRgb);
    await page.locator('#theme-toggle-btn').click(); // restore for later specs

    // Clicking the Complete button does not open the modal.
    await completeBtn.click();
    await expect(page.locator('#task-modal')).toBeHidden();
    const confirm = page.locator('#rename-modal');
    await expect(confirm).toBeVisible();
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(confirm).toBeHidden();

    await expect(card).toHaveCount(0);
    const completed = await getTask(request, baseURL, task.id);
    expect(completed.completed).toBe(true);
    expect(completed.path).toBe(`Task/Completed/${task.id} - ${task.title}.md`);

    // Visible in All Tasks with "Show completed".
    await page.locator('#tasks-nav-all-btn').click();
    await expect(page.locator('#tasks-list-tbody tr', { hasText: task.id })).toHaveCount(0);
    await page.locator('#tasks-list-show-completed').check();
    const row = page.locator('#tasks-list-tbody tr', { hasText: task.id });
    await expect(row).toHaveCount(1);
    await expect(row).toHaveClass(/tasks-list-row-completed/);

    await api.deleteNote(completed.path);
  });

  test('modal Complete button and sidebar "Complete task" also move the note into Completed/', async ({ page, request, baseURL, api }) => {
    const modalTask = await createTaskViaApi(request, baseURL, { title: uniqueName('complete-modal') });
    const sidebarTask = await createTaskViaApi(request, baseURL, { title: uniqueName('complete-sidebar') });

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await page.locator(`.tasks-card[data-task-id="${modalTask.id}"]`).click();
    await expect(page.locator('#task-modal')).toBeVisible();
    await page.locator('#task-modal-complete-btn').click();
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(page.locator('#task-modal')).toBeHidden();
    const completedModal = await getTask(request, baseURL, modalTask.id);
    expect(completedModal.completed).toBe(true);
    expect(completedModal.path).toBe(`Task/Completed/${modalTask.id} - ${modalTask.title}.md`);

    // Sidebar "Complete task" on the task-file row.
    await page.locator('#refresh-tree-btn').click();
    await ensureExpanded(page.locator('#file-tree [data-path="Task"]'));
    const row = page.locator(`#file-tree [data-path="${sidebarTask.path}"]`);
    await expect(row).toBeVisible();
    await row.click({ button: 'right' });
    await expect(page.locator('#tree-context-menu')).toBeVisible();
    await page.locator('#tree-context-complete-task').click();
    await expect(page.locator('#rename-modal')).toBeVisible();
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(page.locator('#rename-modal')).toBeHidden();
    const completedSidebar = await getTask(request, baseURL, sidebarTask.id);
    expect(completedSidebar.completed).toBe(true);
    expect(completedSidebar.path).toBe(`Task/Completed/${sidebarTask.id} - ${sidebarTask.title}.md`);
    // No "Complete task" entry on an already-completed task note.
    await page.locator('#refresh-tree-btn').click();
    await ensureExpanded(page.locator('#file-tree [data-path="Task"]'));
    await ensureExpanded(page.locator('#file-tree [data-path="Task/Completed"]'));
    const completedRow = page.locator(`#file-tree [data-path="${completedSidebar.path}"]`);
    await expect(completedRow).toBeVisible();
    await completedRow.click({ button: 'right' });
    await expect(page.locator('#tree-context-menu')).toBeVisible();
    await expect(page.locator('#tree-context-complete-task')).toHaveCount(0);
    await page.keyboard.press('Escape');

    await api.deleteNote(completedModal.path);
    await api.deleteNote(completedSidebar.path);
  });

  test('completing a task whose target filename already exists in Completed/ gets " (2)" and never overwrites', async ({ page, request, baseURL, api }) => {
    // A task's completed destination is `<folder>/Completed/<id> - <title>.md`
    // - ids are always unique, so the only realistic way to hit this
    // collision is something *else* already occupying that exact filename
    // (e.g. a plain note, or a task completed/renamed by hand) - simulate
    // that directly rather than relying on two tasks somehow sharing an id.
    const stem = uniqueName('collide');
    const task = await createTaskViaApi(request, baseURL, { title: stem });
    const collisionPath = `Task/Completed/${task.id} - ${stem}.md`;
    await api.saveNote(collisionPath, 'pre-existing file occupying the destination name');

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await page.locator(`.tasks-card[data-task-id="${task.id}"] .tasks-card-complete-btn`).click();
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(page.locator(`.tasks-card[data-task-id="${task.id}"]`)).toHaveCount(0);

    const completed = await getTask(request, baseURL, task.id);
    expect(completed.completed).toBe(true);
    expect(completed.path).toBe(`Task/Completed/${task.id} - ${stem} (2).md`);

    // The pre-existing file at the original destination name is untouched.
    const collisionNote = await (await request.get(`${baseURL}/api/notes/${collisionPath.split('/').map(encodeURIComponent).join('/')}`)).json();
    expect(collisionNote.content).toBe('pre-existing file occupying the destination name');

    await api.deleteNote(collisionPath);
    await api.deleteNote(completed.path);
  });

  test('Backlog column is first; an unknown-status task badges into it; Backlog <-> To Do drag persists after reload', async ({ page, request, baseURL, api }) => {
    const enc = (p) => p.split('/').map(encodeURIComponent).join('/');
    const stem = uniqueName('unknown-status');
    const id = `UNK-${Math.floor(Math.random() * 90000) + 10000}`;
    const notePath = `${id} - ${stem}.md`;
    const status = `Weird-${stem.slice(-5)}`;
    const content = `---\nid: ${id}\ntitle: ${stem}\nstatus: ${status}\nordinal: 1000\n---\n\n## Description\n\n<!-- SECTION:DESCRIPTION:BEGIN -->\nx\n<!-- SECTION:DESCRIPTION:END -->\n`;
    const put = await request.put(`${baseURL}/api/notes/${enc(notePath)}`, { data: { content } });
    expect(put.ok()).toBeTruthy();

    const dragTask = await createTaskViaApi(request, baseURL, { title: uniqueName('backlog-drag') });

    try {
      await page.goto('/');
      await page.locator('#tasks-nav-board-btn').click();

      const firstColumn = page.locator('.tasks-board-column').first();
      await expect(firstColumn).toHaveClass(/tasks-board-column-backlog/);

      const backlogColumn = page.locator('.tasks-board-column-backlog');
      const badgedCard = backlogColumn.locator(`.tasks-card[data-task-id="${id}"]`);
      await expect(badgedCard).toBeVisible();
      await expect(badgedCard.locator('.tasks-card-status-badge')).toHaveText(status);

      // Drag Backlog -> To Do.
      const card = page.locator(`.tasks-card[data-task-id="${dragTask.id}"]`);
      const todoCards = page.locator('.tasks-board-column-cards[data-status="To Do"]');
      await expect(card).toBeVisible();
      await dragCard(page, card, todoCards);
      await expect(page.locator(`.tasks-board-column-cards[data-status="To Do"] .tasks-card[data-task-id="${dragTask.id}"]`)).toBeVisible();
      let refetched = await getTask(request, baseURL, dragTask.id);
      expect(refetched.status).toBe('To Do');

      // Drag back To Do -> Backlog.
      const backlogCards = backlogColumn.locator('.tasks-board-column-cards');
      await dragCard(page, card, backlogCards);
      await expect(backlogColumn.locator(`.tasks-card[data-task-id="${dragTask.id}"]`)).toBeVisible();
      refetched = await getTask(request, baseURL, dragTask.id);
      expect(refetched.status).toBe('Backlog');

      await page.reload();
      await page.locator('#tasks-nav-board-btn').click();
      await expect(page.locator('.tasks-board-column-backlog').locator(`.tasks-card[data-task-id="${dragTask.id}"]`)).toBeVisible();
    } finally {
      const latest = await getTask(request, baseURL, id).catch(() => null);
      await api.deleteNote(latest?.path || notePath);
      await api.deleteNote(dragTask.path);
    }
  });

  test('a task placed in a Completed folder never shows on the board', async ({ page, request, baseURL, api }) => {
    const stem = uniqueName('preexisting-completed');
    const id = `PEC-${Math.floor(Math.random() * 90000) + 10000}`;
    const notePath = `Task/Completed/${id} - ${stem}.md`;
    const content = `---\nid: ${id}\ntitle: ${stem}\nstatus: Done\nordinal: 1000\n---\n\n## Description\n\n<!-- SECTION:DESCRIPTION:BEGIN -->\nx\n<!-- SECTION:DESCRIPTION:END -->\n`;
    await api.saveNote(notePath, content);

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    await expect(page.locator(`.tasks-card[data-task-id="${id}"]`)).toHaveCount(0);

    await page.locator('#tasks-nav-all-btn').click();
    await page.locator('#tasks-list-show-completed').check();
    await expect(page.locator('#tasks-list-tbody tr', { hasText: id })).toHaveCount(1);

    await api.deleteNote(notePath);
  });

  test('starting a drag on the card Complete button does not drag the card', async ({ page, request, baseURL, api }) => {
    const task = await createTaskViaApi(request, baseURL, { title: uniqueName('no-drag-from-complete') });
    try {
      await page.goto('/');
      await page.locator('#tasks-nav-board-btn').click();
      const card = page.locator(`.tasks-card[data-task-id="${task.id}"]`);
      await expect(card).toBeVisible();
      const btnBox = await card.locator('.tasks-card-complete-btn').boundingBox();
      const target = await page.locator('.tasks-board-column-cards[data-status="In Progress"]').boundingBox();

      await page.mouse.move(btnBox.x + btnBox.width / 2, btnBox.y + btnBox.height / 2);
      await page.mouse.down();
      await page.mouse.move(target.x + target.width / 2, target.y + 40, { steps: 10 });
      await page.mouse.up();
      await page.waitForTimeout(600);

      expect((await getTask(request, baseURL, task.id)).status).toBe('Backlog');
      await expect(page.locator('.tasks-board-column-backlog').locator(`.tasks-card[data-task-id="${task.id}"]`)).toBeVisible();
      // No confirm dialog either: the drag never became a click on the button.
      await expect(page.locator('#rename-modal')).toBeHidden();
      // Dragging the card body still works (control).
      await dragCard(page, card, page.locator('.tasks-board-column-cards[data-status="In Progress"]'));
      await expect
        .poll(async () => (await getTask(request, baseURL, task.id)).status)
        .toBe('In Progress');
    } finally {
      const latest = await getTask(request, baseURL, task.id).catch(() => null);
      await api.deleteNote(latest?.path || task.path);
    }
  });

  test('editing an unrelated field of a task with an empty status keeps the status empty (no silent rewrite to Backlog) and the modal has no blank option', async ({ page, request, baseURL, api }) => {
    const stem = uniqueName('empty-status');
    const id = `EMP-${Math.floor(Math.random() * 90000) + 10000}`;
    const notePath = `${id} - ${stem}.md`;
    const content = `---\nid: ${id}\ntitle: ${stem}\nstatus:\nordinal: 1000\n---\n\n## Description\n\n<!-- SECTION:DESCRIPTION:BEGIN -->\nx\n<!-- SECTION:DESCRIPTION:END -->\n`;
    await api.saveNote(notePath, content);
    try {
      await page.goto('/');
      await page.locator('#tasks-nav-board-btn').click();
      const card = page.locator('.tasks-board-column-backlog').locator(`.tasks-card[data-task-id="${id}"]`);
      await expect(card).toBeVisible();
      await expect(card.locator('.tasks-card-status-badge')).toHaveCount(0);

      await card.click();
      await expect(page.locator('#task-modal')).toBeVisible();
      const optionValues = await page.locator('#task-modal-status option').evaluateAll((els) => els.map((e) => e.value));
      expect(optionValues).toEqual(['Backlog', 'To Do', 'In Progress', 'Done']);
      await page.locator('#task-modal-milestone').fill('m-empty');
      await page.locator('#task-modal-save-btn').click();
      await expect(page.locator('#task-modal')).toBeHidden();

      const after = await getTask(request, baseURL, id);
      expect(after.milestone).toBe('m-empty');
      expect(after.status || '').toBe('');
      const onDisk = await (await request.get(`${baseURL}/api/notes/${encodeURIComponent(after.path)}`)).json();
      expect(onDisk.content).not.toMatch(/^status:\s*Backlog/m);
    } finally {
      const latest = await getTask(request, baseURL, id).catch(() => null);
      await api.deleteNote(latest?.path || notePath);
    }
  });

  test('folder context menu hides "New Task" on a Completed folder and inside one, but keeps "New Note"', async ({ page, api }) => {
    const root = uniqueName('ctx-completed');
    await api.createFolder(`${root}/Completed/Sub`);

    await page.goto('/');
    await ensureExpanded(page.locator(`#file-tree [data-path="${root}"]`));
    await ensureExpanded(page.locator(`#file-tree [data-path="${root}/Completed"]`));

    for (const path of [`${root}/Completed`, `${root}/Completed/Sub`]) {
      await page.locator(`#file-tree [data-path="${path}"]`).click({ button: 'right' });
      await expect(page.locator('#tree-context-menu')).toBeVisible();
      await expect(page.locator('#tree-context-new-note')).toBeVisible();
      await expect(page.locator('#tree-context-new-task')).toHaveCount(0);
      await page.keyboard.press('Escape');
      await expect(page.locator('#tree-context-menu')).toHaveCount(0);
    }
    // A normal folder still offers it.
    await page.locator(`#file-tree [data-path="${root}"]`).click({ button: 'right' });
    await expect(page.locator('#tree-context-new-task')).toBeVisible();
    await page.keyboard.press('Escape');
  });

  test('the Pomodoro widget stays top-right and visible alongside the 4-column board', async ({ page, request, baseURL }) => {
    const config = await (await request.get(`${baseURL}/api/tasks/config`)).json();
    expect(config.statuses).toEqual(['Backlog', 'To Do', 'In Progress', 'Done']);
    expect(config.folder).toBe('Task');
    expect(config.backlogStatus).toBe('Backlog');
    expect(config.completedStatus).toBe('Done');
    expect(config.completedFolder).toBe('Completed');

    await page.goto('/');
    await page.locator('#tasks-nav-board-btn').click();
    const pomodoro = page.locator('#pomodoro-widget');
    await expect(pomodoro).toBeVisible();
    const columns = page.locator('.tasks-board-column');
    await expect(columns).toHaveCount(4);
    const pomodoroBox = await pomodoro.boundingBox();
    const titleBox = await page.locator('#tasks-board-view .tasks-view-title').boundingBox();
    const firstColumnBox = await columns.first().boundingBox();
    // Top-right of the header row: to the right of the title, above the columns.
    expect(pomodoroBox.x).toBeGreaterThan(titleBox.x);
    expect(pomodoroBox.y).toBeLessThan(firstColumnBox.y);
  });
});
