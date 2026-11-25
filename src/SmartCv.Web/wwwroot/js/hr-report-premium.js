// Enhancement only: no auth token, candidate name, report rows or personal data stored in JS.
const attached = new Map();
export function attach(id) {
  if (attached.has(id)) return;
  const node = document.getElementById(id);
  if (!node) return;
  const shortcut = event => {
    if (!node.isConnected) return;
    const t = event.target;
    const editing = t instanceof HTMLElement && t.closest('input,textarea,select,[contenteditable]');
    if (event.ctrlKey && event.key === 'Enter' && !editing) {
      const action = node.querySelector('[data-hrr-apply]');
      if (action && !action.disabled) { event.preventDefault(); action.click(); }
    }
    if (event.key === 'Escape') {
      const close = node.querySelector('[data-hrr-dialog-close]');
      if (close) { event.preventDefault(); close.click(); }
    }
    // B57/E12: aria-modal dialog keeps Tab / Shift+Tab inside it (background stays unreachable by keyboard).
    const dialog = node.querySelector('[data-hrr-dialog]');
    if (event.key === 'Tab' && dialog) {
      const items = [...dialog.querySelectorAll('button:not([disabled]),a[href],input:not([disabled]),select:not([disabled]),[tabindex]:not([tabindex="-1"])')];
      if (items.length === 0) { event.preventDefault(); dialog.focus(); return; }
      const first = items[0], last = items[items.length - 1];
      if (!dialog.contains(document.activeElement) || document.activeElement === dialog) { event.preventDefault(); (event.shiftKey ? last : first).focus(); }
      else if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }
  };
  document.addEventListener('keydown', shortcut);
  const observers = [];
  if ('IntersectionObserver' in window && !matchMedia('(prefers-reduced-motion: reduce)').matches) {
    const observer = new IntersectionObserver(items => {
      for (const item of items) if (item.isIntersecting) { item.target.classList.add('hrr-visible'); observer.unobserve(item.target); }
    }, { threshold: 0.1 });
    node.querySelectorAll('.hrr-stat,.hrr-panel').forEach(el => observer.observe(el));
    observers.push(observer);
  }
  attached.set(id, () => { document.removeEventListener('keydown', shortcut); observers.forEach(o => o.disconnect()); });
}
export function detach(id) { attached.get(id)?.(); attached.delete(id); }
