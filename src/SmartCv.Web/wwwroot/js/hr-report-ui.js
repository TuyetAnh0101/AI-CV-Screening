// Progressive enhancement only. All sensitive data/API calls stay in Blazor server.
// No tokens, CV text, candidate data or personal names are persisted in JS.
const active = new Map();
export function setup(id) {
  const root = document.getElementById(id);
  if (!root || active.has(id)) return;
  const filter = root.querySelector('[data-hr-quickfind]');
  const keydown = (event) => {
    const target = event.target;
    const editing = target instanceof HTMLElement && (target.closest('input,textarea,select,[contenteditable]') !== null);
    if (event.key === '/' && !editing && !event.ctrlKey && !event.metaKey) {
      event.preventDefault(); filter?.focus();
    } else if (event.key === 'Escape' && root.contains(document.activeElement)) {
      document.activeElement?.blur?.();
    }
  };
  const pointer = () => {
    // CSS-compatible focus/hover enhancement. Scope only the report panel.
    root.classList.add('hrr-interactive');
  };
  document.addEventListener('keydown', keydown);
  root.addEventListener('pointerdown', pointer, { passive: true });
  active.set(id, () => {
    document.removeEventListener('keydown', keydown);
    root.removeEventListener('pointerdown', pointer);
  });
}
export function dispose(id) { active.get(id)?.(); active.delete(id); }
