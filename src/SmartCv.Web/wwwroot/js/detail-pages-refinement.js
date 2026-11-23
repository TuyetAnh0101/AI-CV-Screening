/* SCApp Detail Pages R1 — optional enhancement to native <details>.
 * Native Enter/Space and pointer interactions remain browser-owned.
 * No access to Blazor objects, business data, or authentication state. */
(() => {
  'use strict';

  document.addEventListener('keydown', (event) => {
    if (event.altKey || event.ctrlKey || event.metaKey || event.repeat) return;
    const target = event.target;
    if (!(target instanceof Element)) return;

    const details = target.closest('details[data-scapp-detail]');
    if (!details) return;
    const summary = details.querySelector(':scope > summary');
    if (!summary) return;

    // Escape closes a disclosure even if the focus is inside its body.
    if (event.key === 'Escape' && details.open) {
      details.open = false;
      summary.focus();
      event.preventDefault();
      return;
    }

    if (target !== summary && !summary.contains(target)) return;
    if (event.key === 'ArrowRight' && !details.open) {
      details.open = true;
      event.preventDefault();
    } else if (event.key === 'ArrowLeft' && details.open) {
      details.open = false;
      event.preventDefault();
    }
  });
})();
