// docs/03-FEATURE-SPEC.md: deleting a folder from the sidebar's right-click
// menu (DELETE /api/folders/{**path} - recursive, behind a confirmation
// modal). Lives in the browser suite rather than DotNotes.Api.Tests because
// what's being checked is the *UI* path: context menu -> themed confirm
// modal -> API call -> tree refresh -> the open note being let go of.
const { test, expect, uniqueName } = require('./fixtures');

test.describe('Delete a folder from the sidebar', () => {
  test('deletes a non-empty folder after confirming, refreshes the tree, and clears the open note', async ({
    page,
    api,
  }) => {
    const root = uniqueName('folder-delete');
    const notePath = `${root}/nested/inside.md`;
    await api.saveNote(notePath, '# inside');

    await page.goto('/');
    const rootRow = page.locator(`#file-tree [data-path="${root}"]`);
    await expect(rootRow).toBeVisible();
    await rootRow.click(); // expand
    await page.locator(`#file-tree [data-path="${root}/nested"]`).click();

    // Open the nested note so the "editor follows the delete" path is
    // actually exercised, not just the tree refresh.
    await page.locator(`#file-tree [data-path="${notePath}"]`).click();
    await expect(page.locator('#note-editor-view')).toBeVisible();

    await rootRow.click({ button: 'right' });
    await page.locator('#tree-context-delete').click();

    const modal = page.locator('#rename-modal');
    await expect(modal).toBeVisible();
    await expect(page.locator('#rename-modal-description')).toContainText('everything inside it');
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(modal).toBeHidden();

    // Gone from the tree, gone from disk, and the editor went back Home
    // instead of sitting on a path that no longer exists.
    await expect(page.locator(`#file-tree [data-path="${root}"]`)).toHaveCount(0);
    await expect(page.locator('#home-view')).toBeVisible();
    const noteResponse = await page.request.get(`/api/notes/${notePath}`);
    expect(noteResponse.status()).toBe(404);
  });

  test('cancelling the confirmation leaves the folder in place', async ({ page, api }) => {
    const root = uniqueName('folder-delete-cancel');
    await api.saveNote(`${root}/keep.md`, '# keep');

    await page.goto('/');
    const rootRow = page.locator(`#file-tree [data-path="${root}"]`);
    await expect(rootRow).toBeVisible();

    await rootRow.click({ button: 'right' });
    await page.locator('#tree-context-delete').click();
    await page.locator('#rename-modal-cancel-btn').click();

    await expect(page.locator(`#file-tree [data-path="${root}"]`)).toBeVisible();
    const noteResponse = await page.request.get(`/api/notes/${root}/keep.md`);
    expect(noteResponse.status()).toBe(200);

    await api.deleteTree(root);
  });
});
