// Keep the existing Blazor row actions visible outside a horizontally scrolling table.
(() => {
    let scheduled = false;

    function positionMenus() {
        scheduled = false;
        for (const menu of document.querySelectorAll('.cv-table-wrap .menu-pop')) {
            const button = menu.closest('.row-menu')?.querySelector('.menu-btn');
            if (!button) continue;

            const anchor = button.getBoundingClientRect();
            const table = button.closest('.cv-table-wrap').getBoundingClientRect();
            const gutter = 8;
            menu.hidden = anchor.bottom <= 0 || anchor.top >= innerHeight
                || anchor.right <= table.left || anchor.left >= table.right;
            if (menu.hidden) continue;

            Object.assign(menu.style, {
                position: 'fixed', right: 'auto', bottom: 'auto', zIndex: '1030',
                maxWidth: `${innerWidth - gutter * 2}px`,
                maxHeight: `${innerHeight - gutter * 2}px`, overflowY: 'auto'
            });

            const size = menu.getBoundingClientRect();
            const left = Math.max(gutter, Math.min(anchor.right - size.width, innerWidth - size.width - gutter));
            let top = anchor.bottom + 4;
            if (top + size.height > innerHeight - gutter) top = anchor.top - size.height - 4;
            top = Math.max(gutter, Math.min(top, innerHeight - size.height - gutter));
            menu.style.left = `${left}px`;
            menu.style.top = `${top}px`;
        }
    }

    function schedule() {
        if (!scheduled) {
            scheduled = true;
            requestAnimationFrame(positionMenus);
        }
    }

    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
    window.addEventListener('resize', schedule);
    document.addEventListener('scroll', schedule, true);
    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        const button = document.querySelector('.cv-table-wrap .menu-btn[aria-expanded="true"]');
        if (button) {
            button.click();
            button.focus();
        }
    });
    schedule();
})();
