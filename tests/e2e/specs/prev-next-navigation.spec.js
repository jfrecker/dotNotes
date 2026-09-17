// docs/03-FEATURE-SPEC.md / Phase 11 bug #3: the note editor header's two
// arrow icons (`#prev-note-btn`/`#next-note-btn`) step to the adjacent note
// within the current note's folder, in the sidebar's active sort order
// (js/tree.js's `getSortedChildren`, the one shared source of truth for
// order) - not undo/redo, a mislabel carried over from Phase 10. Disabled
// at the first/last note.
const { test, expect, uniqueName } = require('./fixtures');

test.describe('Previous/next-note navigation', () => {
  test('steps through a folder\'s notes in sort order and disables at both ends', async ({ page, api }) => {
    const root = uniqueName('prevnext');
    const folderPath = `${root}-folder`;
    // Alphabetically: note-a < note-b < note-c (default sort is ascending
    // with no prior localStorage state - each Playwright test gets a fresh
    // browser context).
    await api.saveNote(`${folderPath}/note-a.md`, '# a');
    await api.saveNote(`${folderPath}/note-b.md`, '# b');
    await api.saveNote(`${folderPath}/note-c.md`, '# c');

    await page.goto('/');
    const folderRow = page.locator(`#file-tree [data-path="${folderPath}"]`);
    await folderRow.click(); // expand

    const prevBtn = page.locator('#prev-note-btn');
    const nextBtn = page.locator('#next-note-btn');
    const titleInput = page.locator('#note-title-input');

    await page.locator(`#file-tree [data-path="${folderPath}/note-a.md"]`).click();
    await expect(titleInput).toHaveValue('note-a');
    await expect(prevBtn).toBeDisabled();
    await expect(nextBtn).toBeEnabled();

    await nextBtn.click();
    await expect(titleInput).toHaveValue('note-b');
    await expect(prevBtn).toBeEnabled();
    await expect(nextBtn).toBeEnabled();

    await nextBtn.click();
    await expect(titleInput).toHaveValue('note-c');
    await expect(prevBtn).toBeEnabled();
    await expect(nextBtn).toBeDisabled();

    await prevBtn.click();
    await expect(titleInput).toHaveValue('note-b');

    await prevBtn.click();
    await expect(titleInput).toHaveValue('note-a');
    await expect(prevBtn).toBeDisabled();

    await api.deleteTree(folderPath);
  });

  test('a lone note in its folder disables both prev and next', async ({ page, api }) => {
    const root = uniqueName('prevnext-lone');
    const folderPath = `${root}-folder`;
    await api.saveNote(`${folderPath}/only.md`, '# only');

    await page.goto('/');
    await page.locator(`#file-tree [data-path="${folderPath}"]`).click();
    await page.locator(`#file-tree [data-path="${folderPath}/only.md"]`).click();

    await expect(page.locator('#note-title-input')).toHaveValue('only');
    await expect(page.locator('#prev-note-btn')).toBeDisabled();
    await expect(page.locator('#next-note-btn')).toBeDisabled();

    await api.deleteTree(folderPath);
  });
});
