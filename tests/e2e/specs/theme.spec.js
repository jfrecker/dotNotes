// docs/03-FEATURE-SPEC.md: "Light/dark theme toggle in the top nav". Spot-
// checks that the toggle actually flips `<html data-theme>` (js/theme.js)
// and that new Phase 10 surfaces - the Home view, the note editor header,
// and the formatting toolbar - visibly pick up the dark palette too, not
// just pre-existing Phase 0-9 UI (css/app.css's `[data-theme="dark"]`
// rules target `.bg-white`/`.text-slate-*` utility classes these new
// elements also use, but this had never been exercised in a real browser
// before this suite).
const { test, expect, uniqueName } = require('./fixtures');

async function bgColor(locator) {
  return locator.evaluate((el) => getComputedStyle(el).backgroundColor);
}

test.describe('Light/dark theme toggle', () => {
  test('toggling sets data-theme and changes a Home view folder card background', async ({ page, api }) => {
    const folderPath = uniqueName('theme-home-folder');
    await api.createFolder(folderPath);

    await page.goto('/');
    await page.evaluate(() => localStorage.removeItem('dotnotes-theme'));
    await page.reload();

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    const card = page.locator('.home-folder-card', { hasText: folderPath });
    await expect(card).toBeVisible();
    const cardLight = await bgColor(card);

    await page.locator('#theme-toggle-btn').click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    const cardDark = await bgColor(card);

    expect(cardDark).not.toBe(cardLight);

    // Toggling back restores light mode - and persists across a reload
    // (localStorage, per js/theme.js), unlike the session-only view mode.
    await page.locator('#theme-toggle-btn').click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

    await api.deleteTree(folderPath);
  });

  test('the note editor header and formatting toolbar also pick up dark mode', async ({ page, api }) => {
    const notePath = `${uniqueName('theme-editor')}.md`;
    await api.saveNote(notePath, '# themed note');

    await page.goto('/');
    await page.evaluate(() => localStorage.setItem('dotnotes-theme', 'light'));
    await page.reload();
    await page.locator(`#file-tree [data-path="${notePath}"]`).click();
    await page.locator('[data-view-mode="edit"]').click(); // toolbar visible

    const header = page.locator('.note-header');
    const toolbar = page.locator('#formatting-toolbar');
    const headerLight = await bgColor(header);
    const toolbarLight = await bgColor(toolbar);

    await page.locator('#theme-toggle-btn').click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

    const headerDark = await bgColor(header);
    const toolbarDark = await bgColor(toolbar);

    expect(headerDark).not.toBe(headerLight);
    expect(toolbarDark).not.toBe(toolbarLight);

    await api.deleteTree(notePath);
  });
});
