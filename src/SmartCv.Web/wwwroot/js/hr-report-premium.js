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
