/* SCApp role-based navigation enhancements: progressive, no authorization decisions or sensitive data. */
(() => {
  'use strict';
  const normalize = (text) => (text || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLocaleLowerCase('vi').trim();
  const controls = () => Array.from(document.querySelectorAll('[data-rx-nav-search]'));
  const activeSearch = () => controls().find(e => e.offsetParent !== null) || controls()[0];

  function refresh(searchInput) {
    const root = searchInput?.closest('.app-shell');
    const nav = root?.querySelector('[data-rx-nav-target]');
    if (!nav) return;
    const query = normalize(searchInput.value).slice(0, 80);
    let group = null;
    let visibleInGroup = 0;
    let totalVisible = 0;
    function conclude() {
      if (group) group.dataset.rxNavHidden = visibleInGroup === 0 ? 'true' : 'false';
    }
    Array.from(nav.children).forEach(el => {
      if (el.classList.contains('nav-group')) {
        conclude(); group = el; visibleInGroup = 0;
      } else if (el.classList.contains('nav-item')) {
        const shown = !query || normalize(el.textContent).includes(query);
        el.dataset.rxNavHidden = shown ? 'false' : 'true';
        if (shown) { visibleInGroup += 1; totalVisible += 1; }
      }
    });
    conclude();
    const empty = root.querySelector('[data-rx-nav-empty]');
    if (empty) empty.hidden = totalVisible !== 0;
  }

  document.addEventListener('input', event => {
    const field = event.target.closest?.('[data-rx-nav-search]');
    if (field) refresh(field);
  });

  document.addEventListener('keydown', event => {
    const active = document.activeElement;
    const editable = active && (active.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(active.tagName));
    const target = activeSearch();
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k' && target) {
      event.preventDefault(); target.focus(); target.select(); return;
    }
    if (event.key === 'Escape' && target && active === target) {
      target.value = ''; refresh(target); target.blur();
    }
    // Do not register global navigation shortcuts while the user is typing in any editable field.
    if (editable) return;
  });

  document.addEventListener('click', async event => {
    const button = event.target.closest?.('[data-rx-copy]');
    if (!button) return;
    const scope = button.closest('[data-rx-reports]');
    const status = scope?.querySelector('[data-rx-copy-status]');
    const text = button.dataset.rxCopy || '';
    if (!text) return;
    try {
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable');
      await navigator.clipboard.writeText(text);
      if (status) status.textContent = 'Đã sao chép phạm vi báo cáo (không chứa dữ liệu cá nhân).';
    } catch {
      if (status) status.textContent = 'Trình duyệt không cho phép sao chép tự động.';
    }
  });

  // Blazor enhanced navigation can replace the menu DOM. Refresh purely visual filtering on route changes.
  document.addEventListener('enhancedload', () => controls().forEach(refresh));
})();
