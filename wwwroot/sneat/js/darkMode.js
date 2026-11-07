/**
 * darkMode.js
 * Handles dark/light mode toggle and persists preference in localStorage.
 * Runs inline in <head> to prevent flash of wrong theme.
 */
'use strict';

(function () {
  const STORAGE_KEY = 'smartschool-theme';
  const htmlEl = document.documentElement;
  const LIGHT = 'light-style';
  const DARK  = 'dark-style';

  function getPreferred() {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved === LIGHT || saved === DARK) return saved;
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? DARK : LIGHT;
  }

  function applyTheme(theme) {
    htmlEl.classList.remove(LIGHT, DARK);
    htmlEl.classList.add(theme);
    localStorage.setItem(STORAGE_KEY, theme);
    updateIcons(theme);
  }

  function updateIcons(theme) {
    document.querySelectorAll('.theme-icon-sun').forEach(el =>
      el.classList.toggle('d-none', theme !== DARK));
    document.querySelectorAll('.theme-icon-moon').forEach(el =>
      el.classList.toggle('d-none', theme === DARK));
  }

  // Apply immediately (before paint) to avoid flash
  applyTheme(getPreferred());

  document.addEventListener('DOMContentLoaded', function () {
    // Re-sync icons once DOM exists
    updateIcons(getPreferred());

    document.querySelectorAll('.theme-toggle-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        const next = htmlEl.classList.contains(DARK) ? LIGHT : DARK;
        applyTheme(next);
      });
    });
  });
})();

