// docs/03-FEATURE-SPEC.md: "Home / folder-browse view: breadcrumb trail,
// app name + tagline at the root only, 'X notes, Y folders' summary,
// folder card grid that navigates on click."
const { test, expect, uniqueName } = require('./fixtures');

test.describe('Home / folder-browse view', () => {
  test('root shows the app title/tagline and a folder card per top-level folder', async ({ page, api }) => {
    const root = uniqueName('home-root');
    const folderPath = `${root}-folder`;
    await api.saveNote(`${folderPath}/inside.md`, '# inside');

    await page.goto('/');

    await expect(page.locator('.home-app-name')).toHaveText('dotNotes');
    await expect(page.locator('.home-tagline')).toBeVisible();
    await expect(page.locator('#home-breadcrumb')).toHaveText('Home');

    const card = page.locator('.home-folder-card', { hasText: folderPath });
    await expect(card).toBeVisible();

    await api.deleteTree(folderPath);
  });

  test('clicking a folder card navigates into it, hides the app title, and updates the breadcrumb', async ({
    page,
    api,
  }) => {
    const root = uniqueName('home-nav');
    const folderPath = `${root}-folder`;
    await api.saveNote(`${folderPath}/inside.md`, '# inside');

    await page.goto('/');
    await page.locator('.home-folder-card', { hasText: folderPath }).click();

    // App title/tagline only shown at the root (docs/03-FEATURE-SPEC.md).
    await expect(page.locator('#home-app-title')).toBeHidden();
    await expect(page.locator('#home-breadcrumb')).toContainText('Home');
    await expect(page.locator('#home-breadcrumb')).toContainText(folderPath);
    await expect(page.locator('#home-summary-text')).toContainText('1 note');

    // Breadcrumb click back to Home restores the root view.
    await page.locator('.home-breadcrumb-link', { hasText: 'Home' }).click();
    await expect(page.locator('#home-app-title')).toBeVisible();
    await expect(page.locator('#home-breadcrumb')).toHaveText('Home');

    await api.deleteTree(folderPath);
  });
});
