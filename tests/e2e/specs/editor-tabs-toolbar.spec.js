// docs/03-FEATURE-SPEC.md: "Editor view-mode toggle (Edit-only / Split /
// Preview-only) ..." and "Markdown formatting toolbar in Edit and Split
// modes" (hidden in Preview - js/app.js's applyViewMode).
const { test, expect, uniqueName } = require('./fixtures');

test.describe('Edit/Split/Preview tabs + formatting toolbar', () => {
  test('toolbar is visible in Edit and Split, hidden in Preview', async ({ page, api }) => {
    const notePath = `${uniqueName('tabs-toolbar')}.md`;
    await api.saveNote(notePath, '# hello');

    await page.goto('/');
    await page.locator(`#file-tree [data-path="${notePath}"]`).click();

    const toolbar = page.locator('#formatting-toolbar');
    const editorPane = page.locator('#editor-pane');
    const previewPane = page.locator('#preview-pane');

    await page.locator('[data-view-mode="edit"]').click();
    await expect(toolbar).toBeVisible();
    await expect(editorPane).toBeVisible();
    await expect(previewPane).toBeHidden();

    await page.locator('[data-view-mode="split"]').click();
    await expect(toolbar).toBeVisible();
    await expect(editorPane).toBeVisible();
    await expect(previewPane).toBeVisible();

    await page.locator('[data-view-mode="preview"]').click();
    await expect(toolbar).toBeHidden();
    await expect(editorPane).toBeHidden();
    await expect(previewPane).toBeVisible();

    await api.deleteTree(notePath);
  });

  test('the Bold toolbar button wraps the current selection in Edit mode', async ({ page, api }) => {
    const notePath = `${uniqueName('toolbar-bold')}.md`;
    await api.saveNote(notePath, 'hello world');

    await page.goto('/');
    await page.locator(`#file-tree [data-path="${notePath}"]`).click();
    await page.locator('[data-view-mode="edit"]').click();

    const editor = page.locator('#editor');
    await editor.click();
    // Select the word "hello" (offsets 0-5) via keyboard, browser-agnostic.
    await editor.evaluate((el) => el.setSelectionRange(0, 5));
    await page.locator('[data-format="bold"]').click();

    await expect(editor).toHaveValue('**hello** world');

    await api.deleteTree(notePath);
  });
});
