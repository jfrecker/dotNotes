// docs/03-FEATURE-SPEC.md / Phase 11 bug #5: drag-to-reorder folders among
// siblings (a custom manual order, persisted per-parent in localStorage via
// js/tree.js's `reorderFolder`) coexists with the pre-existing drag-onto-
// center move/reparent (already covered by sidebar-drag-drop.spec.js - not
// duplicated here). The two are distinguished purely by *where* over the
// target row the drop lands: the top/bottom ~30% is the reorder-before/
// after zone, the middle ~40% is the move-onto zone. `#sort-toggle-btn`
// cycles ascending/descending and, per the task brief, overrides any
// level's custom order back to alphabetical until the next manual drag.
const { test, expect, uniqueName } = require('./fixtures');

/** Direct-child folder paths of `#file-tree`, in current DOM (= display)
 * order - `querySelectorAll` always returns elements in document order, so
 * this doubles as an assertion of on-screen sibling order without needing
 * to touch `js/tree.js` internals. */
async function folderOrderUnder(page, prefix) {
  return page
    .locator(`#file-tree [data-type="folder"][data-path^="${prefix}/"]`)
    .evaluateAll((els) => els.map((el) => el.dataset.path));
}

test.describe('Folder drag-to-reorder + sort toggle', () => {
  test('dragging onto a sibling\'s top edge reorders (not reparents), and the sort toggle overrides it back to alphabetical', async ({
    page,
    api,
  }) => {
    const root = uniqueName('reorder-root');
    const subA = `${root}/sub-a`;
    const subB = `${root}/sub-b`;
    const subC = `${root}/sub-c`;
    await api.createFolder(subA);
    await api.createFolder(subB);
    await api.createFolder(subC);

    await page.goto('/');
    const rootRow = page.locator(`#file-tree [data-path="${root}"]`);
    await expect(rootRow).toBeVisible();
    await rootRow.click(); // expand

    // Default alphabetical-ascending order.
    await expect.poll(() => folderOrderUnder(page, root)).toEqual([subA, subB, subC]);

    const subARow = page.locator(`#file-tree [data-path="${subA}"]`);
    const subCRow = page.locator(`#file-tree [data-path="${subC}"]`);
    const box = await subARow.boundingBox();

    // Drop onto sub-a's *top* edge (well within the top ~30% reorder-before
    // zone), dragging sub-c - this must reorder sub-c to just before sub-a,
    // not reparent it underneath sub-a.
    await subCRow.dragTo(subARow, { targetPosition: { x: 5, y: Math.max(1, Math.floor(box.height * 0.1)) } });

    await expect.poll(() => folderOrderUnder(page, root)).toEqual([subC, subA, subB]);
    // Reparent, not reorder, would have nested sub-c under sub-a
    // (`${subA}/sub-c`) and removed it from the top-level list entirely -
    // assert that did NOT happen (content-preserving, per CLAUDE.md).
    await expect(page.locator(`#file-tree [data-path="${subA}/sub-c"]`)).toHaveCount(0);
    const subCEntry = await (await page.request.get(`/api/notes`)).json();
    expect(JSON.stringify(subCEntry)).toContain(subC);

    // The custom order survives a full tree reload (persisted per-parent in
    // localStorage, not just an in-memory redraw).
    await page.reload();
    await page.locator(`#file-tree [data-path="${root}"]`).click();
    await expect.poll(() => folderOrderUnder(page, root)).toEqual([subC, subA, subB]);

    // The sort-toggle overrides the custom order back to alphabetical -
    // clicking it once cycles to descending, which for [sub-a, sub-b,
    // sub-c] is [sub-c, sub-b, sub-a] - distinguishable from both the
    // ascending default and the [sub-c, sub-a, sub-b] custom order above.
    await page.locator('#sort-toggle-btn').click();
    await expect(page.locator('#sort-toggle-label')).toHaveText('Z↓');
    await expect.poll(() => folderOrderUnder(page, root)).toEqual([subC, subB, subA]);

    await api.deleteTree(root);
  });

  test('dragging onto the middle of a sibling row still reparents (move), not reorders', async ({ page, api }) => {
    const root = uniqueName('reorder-vs-move');
    const subA = `${root}/sub-a`;
    const subB = `${root}/sub-b`;
    await api.createFolder(subA);
    await api.createFolder(subB);

    await page.goto('/');
    const rootRow = page.locator(`#file-tree [data-path="${root}"]`);
    await rootRow.click();

    const subARow = page.locator(`#file-tree [data-path="${subA}"]`);
    const subBRow = page.locator(`#file-tree [data-path="${subB}"]`);

    // Drop dead-center (the default `dragTo` target point) - the existing
    // move/reparent zone, unaffected by the new reorder-zone logic.
    await subBRow.dragTo(subARow);

    await expect(page.locator(`#file-tree [data-path="${subB}"]`)).toHaveCount(0);
    await expect(page.locator(`#file-tree [data-path="${subA}/sub-b"]`)).toBeVisible();

    await api.deleteTree(root);
  });
});
