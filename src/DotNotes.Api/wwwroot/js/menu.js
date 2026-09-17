// A small, generic themed popup menu shared by three call sites in js/app.js:
// the "+New" dropdown, the file-tree right-click/keyboard context menu
// ("Rename" / "Move to..."), and the "Move to..." folder picker itself.
// One implementation rather than three bespoke dropdowns, since all three
// are the same shape - a positioned list of actionable items, some possibly
// disabled, closed by Esc/outside-click/an item activating, with arrow-key
// navigation between items (docs/03-FEATURE-SPEC.md's "+New menu" and
// "Sidebar drag-and-drop ... keyboard-accessible ... fallback" lines both
// require this).
const Menu = (() => {
  let active = null; // { el, returnFocusEl, onClose }

  function close() {
    if (!active) {
      return;
    }
    const { el, returnFocusEl, onClose } = active;
    el.remove();
    document.removeEventListener('mousedown', handleOutsideMouseDown, true);
    document.removeEventListener('keydown', handleKeydown, true);
    active = null;
    onClose?.();
    // Returning focus to whatever opened the menu (a button, a tree row)
    // keeps keyboard flow sane - without this, focus would silently drop
    // to <body> when Esc/outside-click closes the menu.
    returnFocusEl?.focus();
  }

  function menuItems() {
    return active ? [...active.el.querySelectorAll('[role="menuitem"]:not([aria-disabled="true"])')] : [];
  }

  function handleOutsideMouseDown(event) {
    if (active && !active.el.contains(event.target)) {
      close();
    }
  }

  function handleKeydown(event) {
    if (!active) {
      return;
    }
    const items = menuItems();
    const currentIndex = items.indexOf(document.activeElement);

    if (event.key === 'Escape') {
      event.preventDefault();
      close();
    } else if (event.key === 'ArrowDown') {
      event.preventDefault();
      items[(currentIndex + 1 + items.length) % items.length]?.focus();
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      items[(currentIndex - 1 + items.length) % items.length]?.focus();
    }
    // Enter/Space activation needs no special handling - items are real
    // <button>s, which already fire `click` for both keys natively.
  }

  /**
   * Opens a menu at viewport coordinates `(x, y)`, closing any menu already
   * open first (there is only ever one at a time).
   *
   * @param {{
   *   x: number, y: number,
   *   align?: 'left'|'right' - 'right' anchors the menu's *right* edge to
   *     x instead of its left edge (for a button near the right of its
   *     container, e.g. "+New"),
   *   id？: string - element id, for stable e2e-test hooks,
   *   ariaLabel: string,
   *   returnFocusEl?: HTMLElement - focused again once the menu closes,
   *   onClose？: () => void - e.g. to reset a trigger button's aria-expanded,
   *   items: Array<{ id?: string, label: string, icon?: string (inline SVG
   *     markup), disabled?: boolean, onSelect?: () => void } | { separator: true }>
   * }} options
   */
  function open({ x, y, align = 'left', id, ariaLabel, returnFocusEl, onClose, items }) {
    close();

    const el = document.createElement('div');
    el.className = 'app-menu';
    if (id) {
      el.id = id;
    }
    el.setAttribute('role', 'menu');
    el.setAttribute('aria-label', ariaLabel || 'Menu');
    el.style.top = `${y}px`;
    if (align === 'right') {
      el.style.right = `${Math.max(4, window.innerWidth - x)}px`;
    } else {
      el.style.left = `${x}px`;
    }

    for (const item of items) {
      if (item.separator) {
        const sep = document.createElement('div');
        sep.className = 'app-menu-separator';
        el.appendChild(sep);
        continue;
      }

      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'app-menu-item';
      btn.setAttribute('role', 'menuitem');
      if (item.id) {
        btn.id = item.id;
      }
      if (item.disabled) {
        btn.disabled = true;
        btn.setAttribute('aria-disabled', 'true');
      }
      if (item.icon) {
        const iconEl = document.createElement('span');
        iconEl.className = 'app-menu-item-icon';
        // `item.icon` is always a small inline SVG string authored in this
        // codebase (see js/app.js's ICONS), never user/vault-supplied
        // content, so this is not an XSS vector.
        iconEl.innerHTML = item.icon;
        btn.appendChild(iconEl);
      }
      const labelEl = document.createElement('span');
      labelEl.className = 'app-menu-item-label';
      labelEl.textContent = item.label;
      btn.appendChild(labelEl);

      if (!item.disabled && item.onSelect) {
        btn.addEventListener('click', () => {
          close();
          item.onSelect();
        });
      }
      el.appendChild(btn);
    }

    document.body.appendChild(el);

    // Simple viewport clamp so a menu opened near an edge doesn't render
    // partly off-screen. Both directions matter for `align: 'right'`, not
    // just overflowing the right edge - anchoring a menu's *right* edge to
    // a button that itself sits near the *left* of the page (true here:
    // "+New" lives at the top of the sidebar, close to the window's own
    // left edge, not the far right of a wide toolbar) can easily push a
    // wide-enough menu's left edge past x=0.
    const rect = el.getBoundingClientRect();
    if (rect.right > window.innerWidth) {
      el.style.left = '';
      el.style.right = '4px';
    } else if (rect.left < 0) {
      el.style.right = '';
      el.style.left = '4px';
    }
    if (rect.bottom > window.innerHeight) {
      el.style.top = `${Math.max(4, window.innerHeight - rect.height - 4)}px`;
    }

    active = { el, returnFocusEl, onClose };
    // `mousedown` (not `click`) so this doesn't immediately close a menu
    // that a `click` handler is *itself* in the middle of opening (a click
    // is mousedown+mouseup; using mousedown here still lets a genuine
    // outside click close the menu before its own click ever fires).
    document.addEventListener('mousedown', handleOutsideMouseDown, true);
    document.addEventListener('keydown', handleKeydown, true);

    menuItems()[0]?.focus();
  }

  function isOpen() {
    return active !== null;
  }

  return { open, close, isOpen };
})();
