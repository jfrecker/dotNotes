// docs/03-FEATURE-SPEC.md / Phase 11 bugs #2 and #4 - optional bonus
// coverage per the task brief ("cheap, not explicitly required"). Split
// view's resize divider persists its ratio to localStorage as a flex-basis
// percentage; the sidebar-collapse button toggles `.sidebar-collapsed` on
// `#sidebar`, also persisted.
const { test, expect, uniqueName } = require('./fixtures');

test.describe('Split divider + sidebar collapse', () => {
  test('dragging the split divider changes the editor/preview flex-basis and persists it', async ({ page, api }) => {
    const notePath = `${uniqueName('split-divider')}.md`;
    await api.saveNote(notePath, '# hello');

    await page.goto('/');
    await page.locator(`#file-tree [data-path="${notePath}"]`).click();
    await page.locator('[data-view-mode="split"]').click();

    const divider = page.locator('#split-divider');
    await expect(divider).toBeVisible();
    const before = await page.locator('#editor-pane').evaluate((el) => el.style.flexBasis);

    const box = await divider.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 - 150, box.y + box.height / 2, { steps: 5 });
    await page.mouse.up();

    const after = await page.locator('#editor-pane').evaluate((el) => el.style.flexBasis);
    expect(after).not.toBe(before);

    const stored = await page.evaluate(() => localStorage.getItem('dotnotes-split-ratio'));
    expect(stored).not.toBeNull();

    await api.deleteTree(notePath);
  });

  test('the sidebar-toggle button collapses and expands the sidebar, persisting state', async ({ page }) => {
    await page.goto('/');
    const sidebar = page.locator('#sidebar');
    const toggleBtn = page.locator('#sidebar-toggle-btn');

    await expect(sidebar).not.toHaveClass(/sidebar-collapsed/);

    await toggleBtn.click();
    await expect(sidebar).toHaveClass(/sidebar-collapsed/);
    await expect(toggleBtn).toHaveAttribute('aria-pressed', 'true');
    let stored = await page.evaluate(() => localStorage.getItem('dotnotes-sidebar-collapsed'));
    expect(stored).toBe('true');

    await toggleBtn.click();
    await expect(sidebar).not.toHaveClass(/sidebar-collapsed/);
    await expect(toggleBtn).toHaveAttribute('aria-pressed', 'false');
    stored = await page.evaluate(() => localStorage.getItem('dotnotes-sidebar-collapsed'));
    expect(stored).toBe('false');
  });
});
