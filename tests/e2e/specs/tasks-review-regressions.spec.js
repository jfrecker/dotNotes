// Regression tests for the Tasks & Kanban code-review findings (archive
// confirm hidden behind the task modal, filtered drag-and-drop, editor
// following a converted note, Pomodoro settings/two-tab behaviour,
// overlapping saves, unlisted-status columns). Every test creates uniquely
// named data and cleans up after itself, like specs/tasks-kanban.spec.js.
const { test, expect, uniqueName } = require('./fixtures');

const enc = (path) => path.split('/').map(encodeURIComponent).join('/');

async function createTask(request, baseURL, body) {
  const res = await request.post(`${baseURL}/api/tasks`, { data: body });
  expect(res.ok(), `POST /api/tasks failed: ${res.status()} ${await res.text()}`).toBeTruthy();
  return res.json();
}

async function deleteNote(request, baseURL, path) {
  if (path) await request.delete(`${baseURL}/api/notes/${enc(path)}`).catch(() => {});
}

async function getTask(request, baseURL, id) {
  return (await request.get(`${baseURL}/api/tasks/${id}`)).json();
}

async function boardColumnIds(request, baseURL, status) {
  const board = await (await request.get(`${baseURL}/api/tasks/board`)).json();
  return board.columns.find((c) => c.status === status).tasks.map((t) => t.id);
}

async function taskConfig(request, baseURL) {
  return (await request.get(`${baseURL}/api/tasks/config`)).json();
}

/** Real mouse drag (native HTML5 DnD needs a real pointer sequence). */
async function dragCard(page, cardLocator, targetLocator, yFraction) {
  await cardLocator.hover();
  await page.mouse.down();
  const box = await targetLocator.boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height * yFraction, { steps: 8 });
  await page.mouse.up();
}

test.describe('Tasks review regressions', () => {
  test('Complete: the confirm dialog is visible above the task modal, Escape closes only the confirm, and Complete completes', async ({ page, request, baseURL }) => {
    const created = await createTask(request, baseURL, { title: uniqueName('complete-confirm') });
    try {
      await page.goto('/');
      await page.locator('#tasks-nav-board-btn').click();
      await page.locator(`.tasks-card[data-task-id="${created.id}"]`).click();
      const taskModal = page.locator('#task-modal');
      await expect(taskModal).toBeVisible();

      await page.locator('#task-modal-complete-btn').click();
      const confirm = page.locator('#rename-modal');
      await expect(confirm).toBeVisible();

      // Stacking: the confirm button is the top-most element at its own centre.
      const confirmBtn = page.locator('#rename-modal-confirm-btn');
      const onTop = await confirmBtn.evaluate((el) => {
        const r = el.getBoundingClientRect();
        const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
        return hit === el || el.contains(hit);
      });
      expect(onTop, 'confirm button must not be covered by the task modal').toBeTruthy();

      // Escape closes only the confirm; the task modal stays open.
      await page.keyboard.press('Escape');
      await expect(confirm).toBeHidden();
      await expect(taskModal).toBeVisible();

      await page.locator('#task-modal-complete-btn').click();
      await expect(confirm).toBeVisible();
      await confirmBtn.click();
      await expect(taskModal).toBeHidden();
      await expect(page.locator(`.tasks-card[data-task-id="${created.id}"]`)).toHaveCount(0);

      const completed = await getTask(request, baseURL, created.id);
      expect(completed.completed).toBe(true);
    } finally {
      const latest = await getTask(request, baseURL, created.id).catch(() => null);
      await deleteNote(request, baseURL, latest?.path || created.path);
    }
  });

  test('filtered board: dropping at the end of the visible cards lands right after the last visible card, not at the column end', async ({ page, request, baseURL }) => {
    const config = await taskConfig(request, baseURL);
    const status = config.statuses[0];
    const label = uniqueName('flt-end');
    const other = uniqueName('flt-other');
    // Column order: A(x) B(other) C(x) D(other)
    const a = await createTask(request, baseURL, { title: 'A-' + label, status, labels: [label] });
    const b = await createTask(request, baseURL, { title: 'B-' + label, status, labels: [other] });
    const c = await createTask(request, baseURL, { title: 'C-' + label, status, labels: [label] });
    const d = await createTask(request, baseURL, { title: 'D-' + label, status, labels: [other] });
    const mine = [a, b, c, d];
    try {
      await page.goto('/');
      await page.locator('#tasks-nav-board-btn').click();
      await page.locator('#tasks-board-label-filter').selectOption(label);
      await expect(page.locator(`.tasks-card[data-task-id="${a.id}"]`)).toBeVisible();
      await expect(page.locator(`.tasks-card[data-task-id="${b.id}"]`)).toHaveCount(0);

      // Drag A onto the lower half of C (the last visible card): the intended
      // position is directly after C, i.e. B, C, A, D - not B, C, D, A.
      await dragCard(page, page.locator(`.tasks-card[data-task-id="${a.id}"]`), page.locator(`.tasks-card[data-task-id="${c.id}"]`), 0.85);

      await expect
        .poll(async () => (await boardColumnIds(request, baseURL, status)).filter((id) => mine.some((t) => t.id === id)))
        .toEqual([b.id, c.id, a.id, d.id]);
    } finally {
      for (const t of mine) await deleteNote(request, baseURL, t.path);
    }
  });

  test('filtered board: a cross-column drop between two visible cards lands right after the upper one (hidden cards in between are respected)', async ({ page, request, baseURL }) => {
    const config = await taskConfig(request, baseURL);
    test.skip(config.statuses.length < 2, 'needs two configured statuses');
    const [fromStatus, toStatus] = config.statuses;
    const label = uniqueName('flt-mid');
    const other = uniqueName('flt-other');
    const e = await createTask(request, baseURL, { title: 'E-' + label, status: fromStatus, labels: [label] });
    // Target column: F(x) G(other) H(x)
    const f = await createTask(request, baseURL, { title: 'F-' + label, status: toStatus, labels: [label] });
    const g = await createTask(request, baseURL, { title: 'G-' + label, status: toStatus, labels: [other] });
    const h = await createTask(request, baseURL, { title: 'H-' + label, status: toStatus, labels: [label] });
    const mine = [e, f, g, h];
    try {
      await page.goto('/');
      await page.locator('#tasks-nav-board-btn').click();
      await page.locator('#tasks-board-label-filter').selectOption(label);
      await expect(page.locator(`.tasks-card[data-task-id="${h.id}"]`)).toBeVisible();

      // Upper half of H = between F and H in the visible list.
      await dragCard(page, page.locator(`.tasks-card[data-task-id="${e.id}"]`), page.locator(`.tasks-card[data-task-id="${h.id}"]`), 0.2);

      await expect
        .poll(async () => (await boardColumnIds(request, baseURL, toStatus)).filter((id) => mine.some((t) => t.id === id)))
        .toEqual([f.id, e.id, g.id, h.id]);
      expect((await getTask(request, baseURL, e.id)).status).toBe(toStatus);
    } finally {
      for (const t of mine) await deleteNote(request, baseURL, t.path);
    }
  });

  test('converting the note that is open in the editor: the editor follows the new path and a further edit saves without a 409 dialog', async ({ page, request, baseURL, api }) => {
    const stem = uniqueName('convert-open');
    const notePath = `${stem}.md`;
    await api.saveNote(notePath, `# Open note\n\nbody for ${stem}\n`);
    let newPath = null;
    try {
      await page.goto('/');
      await page.locator(`#file-tree [data-path="${notePath}"]`).click();
      await expect(page.locator('#note-editor-view')).toBeVisible();
      await expect(page.locator('#editor')).toHaveValue(/Open note/);

      await page.locator(`#file-tree [data-path="${notePath}"]`).click({ button: 'right' });
      await page.locator('#tree-context-convert-to-task').click();
      const modal = page.locator('#task-modal');
      await expect(modal).toBeVisible();
      await page.locator('#task-modal-cancel-btn').click();
      await expect(modal).toBeHidden();

      const { tasks } = await (await request.get(`${baseURL}/api/tasks`)).json();
      const task = tasks.find((t) => t.title === stem);
      expect(task).toBeTruthy();
      newPath = task.path;

      // The editor now shows the new file name and the (task) file content.
      await expect(page.locator('#note-title-input')).toHaveValue(`${task.id} - ${stem}`);
      await expect(page.locator('#editor')).toHaveValue(/status:/);

      // A further edit autosaves to the NEW path with no conflict prompt.
      await page.locator('#editor').click();
      await page.keyboard.press('Control+End');
      await page.keyboard.type('\nextra line after convert');
      await expect(page.locator('#save-status')).toHaveText('Saved', { timeout: 8_000 });
      await expect(page.locator('#rename-modal')).toBeHidden();
      const onDisk = await (await request.get(`${baseURL}/api/notes/${enc(newPath)}`)).json();
      expect(onDisk.content).toContain('extra line after convert');
      expect((await request.get(`${baseURL}/api/notes/${enc(notePath)}`)).status()).toBe(404);
    } finally {
      await deleteNote(request, baseURL, notePath);
      await deleteNote(request, baseURL, newPath);
    }
  });

  test('note moved elsewhere while open: a distinct prompt offers Close note / Keep editing and never recreates the old path', async ({ page, request, baseURL, api }) => {
    const stem = uniqueName('gone-note');
    const notePath = `${stem}.md`;
    const movedPath = `${stem}-moved.md`;
    await api.saveNote(notePath, 'original');
    try {
      await page.goto('/');
      await page.locator(`#file-tree [data-path="${notePath}"]`).click();
      await expect(page.locator('#editor')).toHaveValue('original');

      const mv = await request.post(`${baseURL}/api/notes/${enc(notePath)}/move`, { data: { destinationPath: movedPath } });
      expect(mv.ok()).toBeTruthy();

      await page.locator('#editor').click();
      await page.keyboard.press('End');
      await page.keyboard.type(' edited');

      await expect(page.locator('#rename-modal')).toBeVisible({ timeout: 8_000 });
      await expect(page.locator('#rename-modal-title')).toHaveText('This note was moved or deleted elsewhere');
      await expect(page.locator('#rename-modal-confirm-btn')).toHaveText('Close note');
      await expect(page.locator('#rename-modal-cancel-btn')).toHaveText('Keep editing');

      await page.locator('#rename-modal-cancel-btn').click(); // Keep editing
      await expect(page.locator('#rename-modal')).toBeHidden();
      expect((await request.get(`${baseURL}/api/notes/${enc(notePath)}`)).status()).toBe(404);

      // Trigger another save attempt, then Close note -> Home view.
      await page.locator('#editor').click();
      await page.keyboard.press('End');
      await page.keyboard.type('!');
      await expect(page.locator('#rename-modal')).toBeVisible({ timeout: 8_000 });
      await page.locator('#rename-modal-confirm-btn').click();
      await expect(page.locator('#home-view')).toBeVisible();
      expect((await request.get(`${baseURL}/api/notes/${enc(notePath)}`)).status()).toBe(404);
    } finally {
      await deleteNote(request, baseURL, notePath);
      await deleteNote(request, baseURL, movedPath);
    }
  });

  test('overlapping saves are serialised: an autosave in flight plus a navigate-away flush does not raise a false 409', async ({ page, request, baseURL, api }) => {
    const notePath = `${uniqueName('overlap-save')}.md`;
    await api.saveNote(notePath, 'start');
    try {
      await page.goto('/');
      await page.locator(`#file-tree [data-path="${notePath}"]`).click();
      await expect(page.locator('#editor')).toHaveValue('start');

      // Slow every PUT so the autosave is still in flight when we navigate.
      await page.route('**/api/notes/**', async (route) => {
        if (route.request().method() === 'PUT') {
          await new Promise((r) => setTimeout(r, 900));
        }
        await route.continue();
      });

      await page.locator('#editor').click();
      await page.keyboard.press('End');
      await page.keyboard.type(' one');
      await page.waitForTimeout(1_800); // autosave (1.5 s) fires; its PUT is now in flight
      await page.keyboard.type(' two');
      await page.locator('#home-logo-btn').click(); // flushAutosave while the first PUT is in flight

      await expect(page.locator('#home-view')).toBeVisible();
      await expect(page.locator('#rename-modal')).toBeHidden();
      await expect
        .poll(async () => (await (await request.get(`${baseURL}/api/notes/${enc(notePath)}`)).json()).content, { timeout: 8_000 })
        .toBe('start one two');
      await expect(page.locator('#rename-modal')).toBeHidden();
    } finally {
      await page.unroute('**/api/notes/**').catch(() => {});
      await deleteNote(request, baseURL, notePath);
    }
  });

  test('Pomodoro: changing linked task / sound / auto-start never touches a paused countdown', async ({ page, request, baseURL }) => {
    const created = await createTask(request, baseURL, { title: uniqueName('pomo-link') });
    try {
      await page.goto('/');
      await page.locator('#tasks-nav-board-btn').click();
      const startPause = page.locator('#pomodoro-start-pause-btn');
      const time = page.locator('#pomodoro-time');

      await startPause.click();
      await page.waitForTimeout(2_300);
      await startPause.click(); // pause part-way
      await expect(startPause).toHaveText('Start');
      const paused = await time.textContent();
      expect(paused).not.toBe('25:00');

      await page.locator('#pomodoro-settings-btn').click();
      await page.locator('#pomodoro-set-task').selectOption(created.id);
      await expect(time).toHaveText(paused);
      await page.locator('#pomodoro-set-sound').uncheck();
      await page.locator('#pomodoro-set-autostart').check();
      await expect(time).toHaveText(paused);
      // Even the current phase's own duration must not reset a paused-mid-way timer.
      await page.locator('#pomodoro-set-work').fill('40');
      await page.locator('#pomodoro-set-work').dispatchEvent('change');
      await expect(time).toHaveText(paused);

      // A fresh idle timer, however, does reflect a new duration.
      await page.locator('#pomodoro-reset-btn').click();
      await expect(time).toHaveText('40:00');
      await page.locator('#pomodoro-set-work').fill('30');
      await page.locator('#pomodoro-set-work').dispatchEvent('change');
      await expect(time).toHaveText('30:00');
    } finally {
      await deleteNote(request, baseURL, created.path);
    }
  });

  test('Pomodoro in two tabs: a phase ends once (one cycle counted), and the other tab adopts the state', async ({ context, page }) => {
    const seed = {
      phase: 'focus',
      running: true,
      endsAt: Date.now() + 3_500,
      remainingMs: null,
      cycleCount: 0,
      seq: 5,
      updatedAt: Date.now(),
    };
    await page.goto('/');
    await page.evaluate((state) => {
      localStorage.setItem('dotnotes-pomodoro-state', JSON.stringify({ ...state, endsAt: Date.now() + 3_500 }));
    }, seed);
    const second = await context.newPage();
    await page.reload();
    await second.goto('/');

    // Both tabs pass endsAt; give both several ticks.
    await page.waitForTimeout(7_000);

    const stored = await page.evaluate(() => JSON.parse(localStorage.getItem('dotnotes-pomodoro-state')));
    expect(stored.cycleCount).toBe(1);
    expect(stored.phase).toBe('shortBreak');
    expect(stored.running).toBe(false);

    // The second tab shows the same (adopted) state.
    await second.locator('#tasks-nav-board-btn').click();
    await expect(second.locator('#pomodoro-phase')).toHaveText('Short break');
    await second.close();
  });

  test('a status that is not in Tasks:Statuses lands in Backlog with a raw-status badge, and the modal keeps the status', async ({ page, request, baseURL }) => {
    const stem = uniqueName('unlisted');
    const id = `UNL-${Math.floor(Math.random() * 90000) + 10000}`;
    const notePath = `${id} - ${stem}.md`;
    const status = `Blocked-${stem.slice(-5)}`;
    const content = `---\nid: ${id}\ntitle: ${stem}\nstatus: ${status}\nordinal: 1000\n---\n\n## Description\n\n<!-- SECTION:DESCRIPTION:BEGIN -->\nhello\n<!-- SECTION:DESCRIPTION:END -->\n`;
    const put = await request.put(`${baseURL}/api/notes/${enc(notePath)}`, { data: { content } });
    expect(put.ok()).toBeTruthy();
    try {
      await page.goto('/');
      await page.locator('#tasks-nav-board-btn').click();
      // No more per-status "unlisted" columns: it lands in the Backlog column.
      const backlogColumn = page.locator('.tasks-board-column-backlog');
      await expect(backlogColumn).toBeVisible({ timeout: 8_000 });
      const card = backlogColumn.locator(`.tasks-card[data-task-id="${id}"]`);
      await expect(card).toBeVisible();
      await expect(card.locator('.tasks-card-status-badge')).toHaveText(status);
      // Backlog is a normal column: it still has its own "+".
      await expect(backlogColumn.locator('.tasks-board-column-add-btn')).toBeVisible();

      // The modal for this task offers its current (unlisted) raw status and saving keeps it.
      await card.click();
      await expect(page.locator('#task-modal')).toBeVisible();
      await expect(page.locator('#task-modal-status')).toHaveValue(status);
      await page.locator('#task-modal-milestone').fill('m1');
      await page.locator('#task-modal-save-btn').click();
      await expect(page.locator('#task-modal')).toBeHidden();
      const after = await getTask(request, baseURL, id);
      expect(after.status).toBe(status);
      expect(after.milestone).toBe('m1');
    } finally {
      const latest = await getTask(request, baseURL, id).catch(() => null);
      await deleteNote(request, baseURL, latest?.path || notePath);
    }
  });
});
