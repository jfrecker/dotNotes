// Light/dark theme switcher (docs/03-FEATURE-SPEC.md: "Light/dark theme
// toggle in the top nav"). Unlike the view-mode toggle (js/app.js,
// sessionStorage), a theme choice is meant to stick around across browser
// restarts, so this uses localStorage.
//
// This file is loaded from <head>, before the rest of the page, and applies
// the theme immediately (see the bottom of this IIFE) so the page never
// paints in the "wrong" theme before flipping over - a build-step-free
// stand-in for what a framework would call "no flash of unstyled content".
//
// The actual color values live in css/app.css as CSS custom properties,
// switched via a `data-theme="dark"` attribute on <html> - see that file's
// "--- theme (light/dark) ---" section for why (the precompiled Tailwind
// build has no dark: variants to hook into).
const Theme = (() => {
  const STORAGE_KEY = 'dotnotes-theme';

  function readStored() {
    try {
      return localStorage.getItem(STORAGE_KEY);
    } catch {
      // Private-browsing/storage-disabled edge case - fall back to the
      // system preference every time rather than throwing.
      return null;
    }
  }

  function systemPreference() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
      ? 'dark'
      : 'light';
  }

  /** The theme actually in effect: the user's explicit choice, or the OS preference if they haven't made one yet. */
  function current() {
    return readStored() || systemPreference();
  }

  function apply(theme) {
    document.documentElement.setAttribute('data-theme', theme);
  }

  /** Persists `theme` ('light' | 'dark') as the user's explicit choice and applies it. */
  function set(theme) {
    try {
      localStorage.setItem(STORAGE_KEY, theme);
    } catch {
      // Ignore - theme still applies for this page load, it just won't persist.
    }
    apply(theme);
  }

  /** Flips between light and dark, persists the result, and returns the new theme. */
  function toggle() {
    const next = current() === 'dark' ? 'light' : 'dark';
    set(next);
    return next;
  }

  // Apply immediately at load (see this file's top-of-file comment) rather
  // than waiting for DOMContentLoaded.
  apply(current());

  return { current, set, toggle };
})();
