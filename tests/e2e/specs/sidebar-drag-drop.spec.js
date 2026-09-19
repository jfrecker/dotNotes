// docs/03-FEATURE-SPEC.md: "Sidebar drag-and-drop: drag notes and folders
// onto a folder (or the vault root) to move them ... dragging a folder onto
// itself/its own descendant is refused." (js/tree.js's native HTML5
// drag-and-drop, driven here via Playwright's page.dragAndDrop(), which
// dispatches the real dragstart/dragover/drop event sequence against
// elements with draggable=true - not a synthetic call straight into
// app.js's handlers.)
const { test, expect, uniqueName, ensureExpanded } = require('./fixtures');

test.describe('Sidebar drag-and-drop', () => {
  test('dragging a note onto a folder row moves it there', async ({ page, api }) => {
    const root = uniqueName('dnd-note-onto-folder');
    const notePath = `${root}-note.md`;
    const folderPath = `${root}-folder`;
    await api.saveNote(notePath, '# drag me');
    await api.createFolder(folderPath);

    await page.goto('/');
    const noteRow = page.locator(`#file-tree [data-path="${notePath}"]`);
    const folderRow = page.locator(`#file-tree [data-path="${folderPath}"]`);
    await expect(noteRow).toBeVisible();
    await expect(folderRow).toBeVisible();

    await noteRow.dragTo(folderRow);

    // The note is gone from its old root-level spot and now lives nested
    // under the folder (expand it to see the moved row).
    await expect(page.locator(`#file-tree [data-path="${notePath}"]`)).toHaveCount(0);
    await ensureExpanded(folderRow);
    await expect(page.locator(`#file-tree [data-path="${folderPath}/${notePath}"]`)).toBeVisible();

    await api.deleteTree(root);
  });

  test('dragging a note onto the root drop zone moves it to the vault root', async ({ page, api }) => {
    const root = uniqueName('dnd-note-onto-root');
    const folderPath = `${root}-folder`;
    const notePath = `${folderPath}/note.md`;
    await api.saveNote(notePath, '# start nested');

    await page.goto('/');
    const folderRow = page.locator(`#file-tree [data-path="${folderPath}"]`);
    await expect(folderRow).toBeVisible();
    await folderRow.click(); // expand
    const noteRow = page.locator(`#file-tree [data-path="${notePath}"]`);
    await expect(noteRow).toBeVisible();

    await noteRow.dragTo(page.locator('#tree-root-drop-zone'));

    await expect(page.locator(`#file-tree [data-path="${notePath}"]`)).toHaveCount(0);
    await expect(page.locator(`#file-tree [data-path="note.md"]`)).toBeVisible();

    await api.deleteTree(root);
    await api.deleteTree('note.md');
  });

  test('dragging a folder onto its own descendant is refused - nothing moves', async ({ page, api }) => {
    const root = uniqueName('dnd-folder-onto-descendant');
    const parentPath = `${root}-parent`;
    const childPath = `${parentPath}/child`;
    const leafNotePath = `${childPath}/leaf.md`;
    await api.saveNote(leafNotePath, '# leaf');

    await page.goto('/');
    const parentRow = page.locator(`#file-tree [data-path="${parentPath}"]`);
    await expect(parentRow).toBeVisible();
    await parentRow.click(); // expand to reveal "child"
    const childRow = page.locator(`#file-tree [data-path="${childPath}"]`);
    await expect(childRow).toBeVisible();

    await parentRow.dragTo(childRow);

    // Nothing changed: the parent folder is still at its original path, and
    // the leaf note underneath it is unaffected - the definitive,
    // content-preserving check that no move (and no accidental data loss)
    // happened, not just a UI-level "still looks the same".
    const note = await (await page.request.get(`/api/notes/${leafNotePath}`)).json();
    expect(note.path).toBe(leafNotePath);
    expect(note.content).toBe('# leaf');
    await expect(page.locator(`#file-tree [data-path="${parentPath}"]`)).toBeVisible();

    await api.deleteTree(root);
  });

  // Regression: a drop handler that rebuilt the tree *synchronously*
  // detached the drag source mid-drop, so the browser never fired
  // `dragend` on it. `draggedEntry` and every drag-feedback class then
  // stayed set for the rest of the page's life, and the browser's own
  // drag session was left unterminated - which is what made the sidebar
  // stop responding after dragging a folder (js/tree.js's `endDrag`).
  test('a folder reorder drop ends the drag cleanly - no leftover drag state', async ({ page, api }) => {
    const root = uniqueName('dnd-drag-state');
    for (const name of ['sub-a', 'sub-b', 'sub-c']) {
      await api.createFolder(`${root}/${name}`);
    }

    await page.goto('/');
    const rootRow = page.locator(`#file-tree [data-path="${root}"]`);
    await expect(rootRow).toBeVisible();
    await rootRow.click(); // expand

    await page.evaluate(() => {
      window.__dragEnded = false;
      document.addEventListener('dragend', () => { window.__dragEnded = true; });
    });

    // Drop onto sub-a's top edge: the reorder path, the one that redraws
    // the tree from inside the drop.
    const subARow = page.locator(`#file-tree [data-path="${root}/sub-a"]`);
    const box = await subARow.boundingBox();
    await page
      .locator(`#file-tree [data-path="${root}/sub-c"]`)
      .dragTo(subARow, { targetPosition: { x: 5, y: Math.max(1, Math.floor(box.height * 0.1)) } });

    await expect.poll(() => page.evaluate(() => window.__dragEnded)).toBe(true);
    // Every drag-feedback class is gone, including the root drop zone's
    // armed state and its swapped-out label.
    await expect(page.locator('#tree-root-drop-zone')).not.toHaveClass(/tree-root-drop-hint-(armed|over)/);
    await expect(page.locator('#tree-root-drop-label')).toHaveText('Drag = Move');
    await expect(
      page.locator('.tree-row-dragging, .tree-row-drop-target, .tree-row-reorder-before, .tree-row-reorder-after'),
    ).toHaveCount(0);
    // And the page is still interactive: a plain click still expands.
    await rootRow.click();
    await expect(rootRow).toHaveAttribute('aria-expanded', 'false');

    await api.deleteTree(root);
  });

  test('a name collision at the destination is refused with the backend\'s message, and nothing moves', async ({
    page,
    api,
  }) => {
    const root = uniqueName('dnd-collision');
    await api.saveNote(`${root}/dupe/keep.md`, '# original');
    await api.createFolder(`${root}/dest/dupe`);

    await page.goto('/');
    const rootRow = page.locator(`#file-tree [data-path="${root}"]`);
    await expect(rootRow).toBeVisible();
    await rootRow.click();

    const messages = [];
    page.on('dialog', (dialog) => {
      messages.push(dialog.message());
      dialog.dismiss();
    });

    await page
      .locator(`#file-tree [data-path="${root}/dupe"]`)
      .dragTo(page.locator(`#file-tree [data-path="${root}/dest"]`));

    await expect.poll(() => messages.length).toBe(1);
    expect(messages[0]).toContain('already exists');
    // The note is still where it started - the move really was refused,
    // not just reported as refused.
    const note = await (await page.request.get(`/api/notes/${root}/dupe/keep.md`)).json();
    expect(note.content).toBe('# original');

    await api.deleteTree(root);
  });
});
