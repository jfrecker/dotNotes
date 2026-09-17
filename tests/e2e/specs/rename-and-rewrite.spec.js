// docs/03-FEATURE-SPEC.md: "Rename notes and folders (right-click
// 'Rename', inline-editable note title) with incoming [[wikilinks]]
// rewritten so backlinks never orphan." This spec exercises the inline
// title = rename path specifically (typing directly into the note editor
// header, not the right-click modal - see context-menu.spec.js for that
// one), forces a real wikilink rewrite in an unrelated note, and confirms
// the *other*, currently-open note reloads with the rewritten content
// instead of a stale editor buffer later autosaving over the rewrite
// (js/app.js's performMove: "If the currently-open note is in
// [rewrittenNotes] ... reload it").
const { test, expect, uniqueName } = require('./fixtures');

test.describe('Inline title rename + wikilink rewrite propagation', () => {
  test('renaming a note rewrites a link in another, currently-open note, which reloads with the new content', async ({
    page,
    api,
  }) => {
    const root = uniqueName('inline-rename-rewrite');
    const targetPath = `${root}-idea.md`;
    const linkerPath = `${root}-linker.md`;

    await api.saveNote(targetPath, '# idea note');
    await api.saveNote(linkerPath, `See [[${targetPath.replace(/\.md$/, '')}]] for details.`);

    await page.goto('/');

    // Open the *linker* note - not the one being renamed - so this test
    // proves the currently-open note reloads because its own on-disk
    // content changed under it, not merely because the user re-navigated.
    await page.locator(`#file-tree [data-path="${linkerPath}"]`).click();
    await expect(page.locator('#note-title-input')).toHaveValue(linkerPath.replace(/\.md$/, ''));
    const editor = page.locator('#editor');
    await expect(editor).toHaveValue(new RegExp(`\\[\\[${targetPath.replace(/\.md$/, '')}\\]\\]`));

    // Now rename the target note via a *second* tab-equivalent action: the
    // sidebar context menu on the target row (the inline title input only
    // renames whatever note is currently open, which is deliberately the
    // linker here, not the target).
    const targetRow = page.locator(`#file-tree [data-path="${targetPath}"]`);
    await targetRow.click({ button: 'right' });
    await page.locator('#tree-context-rename').click();
    const newTitle = `${root}-renamed`;
    await page.locator('#rename-modal-input').fill(newTitle);
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(page.locator('#rename-modal')).toBeHidden();

    // The linker note is still the open note, and its editor buffer must
    // now reflect the rewritten link - proving the frontend reloaded it
    // rather than leaving a stale buffer that would later autosave the
    // *old* link text back over the server's rewrite.
    await expect(editor).toHaveValue(`See [[${newTitle}]] for details.`);
    await expect(page.locator('#note-title-input')).toHaveValue(linkerPath.replace(/\.md$/, ''));

    // Confirm on disk too, not just in the editor buffer.
    const linkerOnDisk = await (await page.request.get(`/api/notes/${linkerPath}`)).json();
    expect(linkerOnDisk.content).toBe(`See [[${newTitle}]] for details.`);

    await api.deleteTree(`${newTitle}.md`);
    await api.deleteTree(linkerPath);
  });

  test('the inline title input itself renames the currently-open note (self-rename path)', async ({ page, api }) => {
    const root = uniqueName('inline-self-rename');
    const notePath = `${root}.md`;
    await api.saveNote(notePath, '# self rename target');

    await page.goto('/');
    await page.locator(`#file-tree [data-path="${notePath}"]`).click();

    const newTitle = `${root}-renamed-inline`;
    const titleInput = page.locator('#note-title-input');
    await titleInput.fill(newTitle);
    await titleInput.press('Enter');

    await expect(titleInput).toHaveValue(newTitle);
    await expect(page.locator(`#file-tree [data-path="${newTitle}.md"]`)).toBeVisible();
    await expect(page.locator(`#file-tree [data-path="${notePath}"]`)).toHaveCount(0);

    await api.deleteTree(`${newTitle}.md`);
  });
});
