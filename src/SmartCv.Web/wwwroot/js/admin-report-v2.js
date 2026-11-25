// Progressive enhancement. No network calls, JWT, PII, localStorage, or report payloads.
const registrations = new Map();
export function attach(id) {
  if (registrations.has(id)) return;
  const root = document.getElementById(id);
  if (!root) return;
  const onKey = event => {
    if (!root.isConnected || event.defaultPrevented || event.repeat) return;
    const target = event.target;
    const typing = target instanceof HTMLElement &&
      Boolean(target.closest('input,textarea,select,[contenteditable="true"]'));
    // Alt+Shift+P previews current form selection using Blazor's actual button handler.
    if (event.altKey && event.shiftKey && event.code === 'KeyP' && !typing) {
      const button = root.querySelector('[data-ar2-preview]');
      if (button && !button.disabled) { event.preventDefault(); button.click(); }
    }
  };
  document.addEventListener('keydown', onKey);
  let observer;
  if ('IntersectionObserver' in window && !window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    observer = new IntersectionObserver(entries => {
      for (const entry of entries) if (entry.isIntersecting) {
        entry.target.classList.add('ar2-reveal');
        observer.unobserve(entry.target);
      }
    }, { threshold: .1 });
    root.querySelectorAll('.ar2-grid,.ar2-actions,.ar2-preview').forEach(node => observer.observe(node));
  }
  registrations.set(id, () => { document.removeEventListener('keydown', onKey); observer?.disconnect(); });
}
export function detach(id) { registrations.get(id)?.(); registrations.delete(id); }
