// Folder right-click "New Subfolder": locked-prefix prompt, created inside
// the right-clicked folder, parent expanded and new folder highlighted;
// cancel creates nothing; duplicate/invalid names rejected in the prompt.
const { test, expect, uniqueName } = require('./fixtures');

/** Child names of `folderPath`, read from GET /api/notes (null if the folder doesn't exist). */
async function childNames(api, folderPath) {
  let list = await (await api.request.get(`${api.baseURL}/api/notes`)).json();
  for (const segment of folderPath.split('/')) {
    const folder = list.find((e) => e.type === 'folder' && e.name === segment);
    if (!folder) {
      return null;
    }
    list = folder.children || [];
  }
  return list.map((e) => e.name);
}

async function openNewSubfolderPrompt(page, folder) {
  await page.locator(`#file-tree [data-path="${folder}"]`).click({ button: 'right' });
  await expect(page.locator('#tree-context-menu')).toBeVisible();
  await page.locator('#tree-context-new-subfolder').click();
  await expect(page.locator('#rename-modal')).toBeVisible();
  await expect(page.locator('#rename-modal-locked-prefix')).toHaveText(`In folder: ${folder}`);
}

test.describe('Folder context menu: New Subfolder', () => {
  test('sits after New Task and before Rename', async ({ page, api }) => {
    const folder = uniqueName('subfolder-order');
    await api.createFolder(folder);

    await page.goto('/');
    await page.locator(`#file-tree [data-path="${folder}"]`).click({ button: 'right' });
    const ids = await page.locator('#tree-context-menu [id^="tree-context-"]').evaluateAll((els) => els.map((e) => e.id));
    const at = (id) => ids.indexOf(id);
    expect(at('tree-context-new-subfolder')).toBe(at('tree-context-new-task') + 1);
    expect(at('tree-context-rename')).toBe(at('tree-context-new-subfolder') + 1);

    await api.deleteTree(folder);
  });

  test('creates the folder inside the right-clicked folder, expands the parent and highlights it', async ({ page, api }) => {
    const parent = uniqueName('subfolder-parent');
    const child = uniqueName('child');
    await api.createFolder(parent);

    await page.goto('/');
    await openNewSubfolderPrompt(page, parent);
    await page.locator('#rename-modal-input').fill(`  ${child}  `);
    await page.locator('#rename-modal-confirm-btn').click();
    await expect(page.locator('#rename-modal')).toBeHidden();

    const row = page.locator(`#file-tree [data-type="folder"][data-path="${parent}/${child}"]`);
    await expect(row).toBeVisible();
    await expect(row).toBeFocused();
    await expect(page.locator(`#file-tree [data-path="${parent}"]`)).toHaveAttribute('aria-expanded', 'true');
    expect(await childNames(api, parent)).toEqual([child]);

    await api.deleteTree(parent);
  });

  test('Cancel and Escape create nothing', async ({ page, api }) => {
    const parent = uniqueName('subfolder-cancel');
    await api.createFolder(parent);

    await page.goto('/');
    await openNewSubfolderPrompt(page, parent);
    await page.locator('#rename-modal-input').fill('never-created');
    await page.locator('#rename-modal-cancel-btn').click();
    await expect(page.locator('#rename-modal')).toBeHidden();

    await openNewSubfolderPrompt(page, parent);
    await page.locator('#rename-modal-input').fill('never-created');
    await page.keyboard.press('Escape');
    await expect(page.locator('#rename-modal')).toBeHidden();

    expect(await childNames(api, parent)).toEqual([]);

    await api.deleteTree(parent);
  });

  test('rejects empty, invalid, "."/".." and duplicate names in the prompt', async ({ page, api }) => {
    const parent = uniqueName('subfolder-invalid');
    await api.createFolder(`${parent}/existing`);

    await page.goto('/');
    await openNewSubfolderPrompt(page, parent);
    const input = page.locator('#rename-modal-input');
    const confirm = page.locator('#rename-modal-confirm-btn');
    const error = page.locator('#rename-modal-error');

    for (const bad of ['   ', 'a/b', '..', '.', 'bad|name', 'existing']) {
      await input.fill(bad);
      await confirm.click();
      await expect(error).toBeVisible();
      await expect(page.locator('#rename-modal')).toBeVisible();
    }
    await expect(error).toContainText('already exists');

    await page.keyboard.press('Escape');
    expect(await childNames(api, parent)).toEqual(['existing']);

    await api.deleteTree(parent);
  });
});
