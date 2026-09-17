// docs/03-FEATURE-SPEC.md's Home/folder-browse view + Phase 11 bug #1:
// "Folder-browse view: render note cards (not just folder cards) for notes
// directly in the current folder; true empty-state only when a folder has
// zero notes and zero subfolders." Before this fix, js/app.js's
// renderFolderGrid only ever built `.home-folder-card` elements for
// subfolders and silently dropped any notes living directly in the browsed
// folder, so a folder containing only notes rendered as if it were empty.
const { test, expect, uniqueName } = require('./fixtures');

test.describe('Home view: note cards in folder-browse', () => {
  test('a folder containing only notes (no subfolders) renders a note card per note, and clicking one opens it', async ({
    page,
    api,
  }) => {
    const root = uniqueName('home-notes-only');
    const folderPath = `${root}-folder`;
    await api.saveNote(`${folderPath}/alpha.md`, '# alpha');
    await api.saveNote(`${folderPath}/beta.md`, '# beta');

    await page.goto('/');
    await page.locator('.home-folder-card', { hasText: folderPath }).click();

    // Not the empty-state message - two real note cards.
    await expect(page.locator('.home-grid-empty')).toHaveCount(0);
    const noteCards = page.locator('.home-note-card');
    await expect(noteCards).toHaveCount(2);
    await expect(page.locator('.home-note-card', { hasText: 'alpha' })).toBeVisible();
    await expect(page.locator('.home-note-card', { hasText: 'beta' })).toBeVisible();
    await expect(page.locator('#home-summary-text')).toContainText('2 notes');

    await page.locator('.home-note-card', { hasText: 'alpha' }).click();

    // Clicking a note card opens the note directly in the editor, same as
    // clicking it in the sidebar tree.
    await expect(page.locator('#note-title-input')).toHaveValue('alpha');

    await api.deleteTree(folderPath);
  });

  test('a folder with both notes and subfolders renders both card types', async ({ page, api }) => {
    const root = uniqueName('home-mixed');
    const folderPath = `${root}-folder`;
    await api.saveNote(`${folderPath}/note-one.md`, '# one');
    await api.createFolder(`${folderPath}/sub-folder`);

    await page.goto('/');
    await page.locator('.home-folder-card', { hasText: folderPath }).click();

    await expect(page.locator('.home-grid-empty')).toHaveCount(0);
    await expect(page.locator('.home-note-card', { hasText: 'note-one' })).toBeVisible();
    // Subfolder cards share the `.home-folder-card` class but are not also
    // `.home-note-card` - assert the folder card exists and is distinct.
    const folderCard = page.locator('.home-folder-card:not(.home-note-card)', { hasText: 'sub-folder' });
    await expect(folderCard).toBeVisible();

    await api.deleteTree(folderPath);
  });

  test('a folder with zero notes and zero subfolders shows the true empty-state message', async ({ page, api }) => {
    const root = uniqueName('home-empty');
    const folderPath = `${root}-folder`;
    await api.createFolder(folderPath);

    await page.goto('/');
    await page.locator('.home-folder-card', { hasText: folderPath }).click();

    await expect(page.locator('.home-grid-empty')).toBeVisible();
    await expect(page.locator('.home-grid-empty')).toContainText('No notes or subfolders here yet.');
    await expect(page.locator('.home-note-card')).toHaveCount(0);
    await expect(page.locator('.home-folder-card')).toHaveCount(0);
    await expect(page.locator('#home-summary-text')).toContainText('0 notes');

    await api.deleteTree(folderPath);
  });
});
