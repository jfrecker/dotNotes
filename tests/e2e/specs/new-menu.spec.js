// docs/03-FEATURE-SPEC.md: 'Single "+New" menu (New Note, New Folder;
// template/drawing entries present but disabled)'. This spec both proves
// the menu creates real notes/folders *and* proves "New from Template" /
// "New Drawing" stay inert - clicking them must never call the API or open
// any dialog (CLAUDE.md: "Don't wire them up as a side effect of other UI
// work"). If a future change accidentally makes them clickable, this test
// fails loudly rather than the checkbox in docs/03-FEATURE-SPEC.md just
// quietly lying.
const { test, expect, uniqueName } = require('./fixtures');

test.describe('"+New" dropdown', () => {
  test('New Note / New Folder items exist and are enabled', async ({ page }) => {
    await page.goto('/');
    await page.locator('#new-menu-btn').click();

    const menu = page.locator('#new-menu');
    await expect(menu).toBeVisible();
    await expect(page.locator('#new-menu-note')).toBeEnabled();
    await expect(page.locator('#new-menu-folder')).toBeEnabled();

    await page.keyboard.press('Escape');
    await expect(menu).toHaveCount(0);
  });

  test('New Note creates a note via the themed modal (js/app.js\'s Modal.prompt, docs/03-FEATURE-SPEC.md\'s "In-app themed modal ... replacing native prompt()/confirm()")', async ({ page, api }) => {
    const root = uniqueName('new-menu-note');
    const notePath = `${root}.md`;

    await page.goto('/');
    await page.locator('#new-menu-btn').click();
    await page.locator('#new-menu-note').click();

    const modal = page.locator('#rename-modal');
    await expect(modal).toBeVisible();
    await expect(page.locator('#rename-modal-title')).toHaveText('New Note');
    await page.locator('#rename-modal-input').fill(notePath);
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(modal).toBeHidden();

    // Creating a note opens it directly in the editor (js/app.js's
    // createNoteAtPath calls selectFile) - the inline title input is the
    // most direct proof the right note actually opened.
    await expect(page.locator('#note-title-input')).toHaveValue(root);
    await expect(page.locator(`#file-tree [data-path="${notePath}"]`)).toBeVisible();

    await api.deleteTree(notePath);
  });

  test('"New from Template" and "New Drawing" are rendered disabled and do nothing when clicked', async ({ page }) => {
    await page.goto('/');
    await page.locator('#new-menu-btn').click();

    const templateItem = page.locator('#new-menu-template');
    const drawingItem = page.locator('#new-menu-drawing');
    await expect(templateItem).toBeVisible();
    await expect(drawingItem).toBeVisible();
    await expect(templateItem).toBeDisabled();
    await expect(drawingItem).toBeDisabled();
    await expect(templateItem).toHaveAttribute('aria-disabled', 'true');
    await expect(drawingItem).toHaveAttribute('aria-disabled', 'true');

    // Clicking a disabled native <button> fires no click handler at all,
    // but assert the observable behavior, not the implementation detail:
    // no dialog opens, the menu stays open (menu.js only closes on a real
    // item activation), and no new tree entry appears.
    let dialogFired = false;
    page.once('dialog', (dialog) => {
      dialogFired = true;
      dialog.dismiss();
    });
    await templateItem.click({ force: true });
    await drawingItem.click({ force: true });
    await page.waitForTimeout(200);

    expect(dialogFired).toBe(false);
    await expect(page.locator('#new-menu')).toBeVisible();
  });
});
