// docs/03-FEATURE-SPEC.md: "... with a keyboard-accessible right-click
// 'Move to…' fallback" and "Rename notes and folders (right-click
// 'Rename' ...)". Covers both the mouse right-click path and the
// keyboard-only path (js/tree.js: Menu key / Shift+F10), since the task
// brief calls out the keyboard equivalent by name.
const { test, expect, uniqueName, ensureExpanded } = require('./fixtures');

test.describe('Right-click / keyboard context menu', () => {
  test('right-click opens Rename + Move to…, and Rename actually renames the file', async ({ page, api }) => {
    const root = uniqueName('ctx-rename');
    const notePath = `${root}.md`;
    await api.saveNote(notePath, '# rename me');

    await page.goto('/');
    const row = page.locator(`#file-tree [data-path="${notePath}"]`);
    await expect(row).toBeVisible();
    await row.click({ button: 'right' });

    const menu = page.locator('#tree-context-menu');
    await expect(menu).toBeVisible();
    await expect(page.locator('#tree-context-rename')).toBeVisible();
    await expect(page.locator('#tree-context-move-to')).toBeVisible();

    await page.locator('#tree-context-rename').click();

    const renameModal = page.locator('#rename-modal');
    await expect(renameModal).toBeVisible();
    const newName = `${root}-renamed`;
    await page.locator('#rename-modal-input').fill(newName);
    await page.locator('#rename-modal-confirm-btn').click();

    await expect(renameModal).toBeHidden();
    await expect(page.locator(`#file-tree [data-path="${newName}.md"]`)).toBeVisible();
    await expect(page.locator(`#file-tree [data-path="${notePath}"]`)).toHaveCount(0);

    await api.deleteTree(`${newName}.md`);
  });

  test('the Menu key / Shift+F10 keyboard fallback opens the same context menu', async ({ page, api }) => {
    const root = uniqueName('ctx-keyboard');
    const notePath = `${root}.md`;
    await api.saveNote(notePath, '# keyboard access');

    await page.goto('/');
    const row = page.locator(`#file-tree [data-path="${notePath}"]`);
    await expect(row).toBeVisible();
    await row.focus();
    await page.keyboard.press('Shift+F10');

    await expect(page.locator('#tree-context-menu')).toBeVisible();
    await expect(page.locator('#tree-context-rename')).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(page.locator('#tree-context-menu')).toHaveCount(0);

    await api.deleteTree(notePath);
  });

  test('"Move to…" moves the note into the chosen folder', async ({ page, api }) => {
    const root = uniqueName('ctx-move-to');
    const notePath = `${root}.md`;
    const folderPath = `${root}-dest`;
    await api.saveNote(notePath, '# move me via menu');
    await api.createFolder(folderPath);

    await page.goto('/');
    const row = page.locator(`#file-tree [data-path="${notePath}"]`);
    await row.click({ button: 'right' });
    await page.locator('#tree-context-move-to').click();

    const moveMenu = page.locator('#move-to-menu');
    await expect(moveMenu).toBeVisible();
    await moveMenu.getByRole('menuitem', { name: folderPath }).click();

    await expect(page.locator(`#file-tree [data-path="${notePath}"]`)).toHaveCount(0);
    await ensureExpanded(page.locator(`#file-tree [data-path="${folderPath}"]`));
    await expect(page.locator(`#file-tree [data-path="${folderPath}/${root}.md"]`)).toBeVisible();

    await api.deleteTree(folderPath);
  });
});
