// docs/03-FEATURE-SPEC.md / Phase 11 bug #6: a single reusable themed
// `Modal` component (js/app.js, built on the `#rename-modal` markup)
// replaces every native `window.prompt()`/`window.confirm()`. New Note's
// modal path is already covered by `new-menu.spec.js` - this covers New
// Folder's create path, plus the cancel paths (Escape and the Cancel
// button) that must leave nothing behind, since a false-positive "created"
// here would be a silent data-integrity bug, not just a UI nit.
const { test, expect, uniqueName } = require('./fixtures');

test.describe('Themed modal: New Folder create/cancel', () => {
  test('New Folder creates a folder via the themed modal', async ({ page, api }) => {
    const root = uniqueName('modal-new-folder');
    const folderPath = `${root}-folder`;

    await page.goto('/');
    await page.locator('#new-menu-btn').click();
    await page.locator('#new-menu-folder').click();

    const modal = page.locator('#rename-modal');
    await expect(modal).toBeVisible();
    await expect(page.locator('#rename-modal-title')).toHaveText('New Folder');
    await page.locator('#rename-modal-input').fill(folderPath);
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(modal).toBeHidden();

    await expect(page.locator(`#file-tree [data-path="${folderPath}"]`)).toBeVisible();

    await api.deleteTree(folderPath);
  });

  test('pressing Escape cancels New Folder without creating anything', async ({ page }) => {
    const root = uniqueName('modal-escape-cancel');
    const folderPath = `${root}-folder`;

    await page.goto('/');
    await page.locator('#new-menu-btn').click();
    await page.locator('#new-menu-folder').click();

    const modal = page.locator('#rename-modal');
    await expect(modal).toBeVisible();
    await page.locator('#rename-modal-input').fill(folderPath);
    await page.keyboard.press('Escape');
    await expect(modal).toBeHidden();

    // Give any (incorrect) fire-and-forget create request a moment to land,
    // then assert it definitely did not - a silent stray folder from a
    // "cancel" that isn't actually a cancel would be exactly the kind of
    // data-integrity bug this suite exists to catch.
    await page.waitForTimeout(300);
    await expect(page.locator(`#file-tree [data-path="${folderPath}"]`)).toHaveCount(0);
    const res = await page.request.get('/api/notes');
    const body = JSON.stringify(await res.json());
    expect(body).not.toContain(folderPath);
  });

  test('clicking Cancel cancels New Folder without creating anything', async ({ page }) => {
    const root = uniqueName('modal-cancel-btn');
    const folderPath = `${root}-folder`;

    await page.goto('/');
    await page.locator('#new-menu-btn').click();
    await page.locator('#new-menu-folder').click();

    const modal = page.locator('#rename-modal');
    await expect(modal).toBeVisible();
    await page.locator('#rename-modal-input').fill(folderPath);
    await page.locator('#rename-modal-cancel-btn').click();
    await expect(modal).toBeHidden();

    await page.waitForTimeout(300);
    await expect(page.locator(`#file-tree [data-path="${folderPath}"]`)).toHaveCount(0);
    const res = await page.request.get('/api/notes');
    const body = JSON.stringify(await res.json());
    expect(body).not.toContain(folderPath);
  });
});
