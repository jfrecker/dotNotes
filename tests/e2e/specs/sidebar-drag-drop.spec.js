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
});
