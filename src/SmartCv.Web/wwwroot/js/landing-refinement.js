// Hiệu ứng giao diện cho Landing Page (ES module, nạp từ Pages/Landing.razor).
// Chỉ đổi thuộc tính data-*, aria-current và biến CSS; không gọi API, không lưu trữ, không đụng nội dung do Blazor dựng.

const instances = new WeakMap();

export function init(root) {
    if (!root || !root.isConnected) return { dispose() { } };
    instances.get(root)?.dispose();   // idempotent: không đăng ký listener hai lần

    const aborter = new AbortController();
    const { signal } = aborter;
    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const shell = root.closest('.public-shell') ?? document;
    const header = shell.querySelector('.public-header');
    const toTop = root.querySelector('[data-lp-top]');
    const steps = [...root.querySelectorAll('[data-lp-step]')];
    const links = header ? [...header.querySelectorAll('.public-nav a[href*="#"]')] : [];
    const targets = links
        .map(link => ({ link, section: document.getElementById(link.getAttribute('href').split('#')[1]) }))
        .filter(item => item.section);

    let frame = 0;
    let revealer = null;

    function update() {
        frame = 0;
        const y = window.scrollY;
        const offset = (header?.offsetHeight ?? 0) + 120;

        header?.setAttribute('data-scrolled', String(y > 8));
        toTop?.setAttribute('data-visible', String(y > window.innerHeight));

        // Mục điều hướng đang xem: section chứa vạch ngay dưới header.
        for (const { link, section } of targets) {
            const box = section.getBoundingClientRect();
            if (box.top <= offset && box.bottom > offset) link.setAttribute('aria-current', 'true');
            else link.removeAttribute('aria-current');
        }

        // Quy trình: tô dần các bước đã đi qua. Chỉ là nhấn mạnh, chữ luôn đọc được.
        const horizontal = steps.length > 1 && steps[0].offsetTop === steps[1].offsetTop;
        const line = window.innerHeight * 0.75;
        steps.forEach((step, index) => {
            const top = step.getBoundingClientRect().top + (horizontal ? index * 55 : 0);
            step.setAttribute('data-active', String(top < line));
        });
    }

    function schedule() {
        if (!frame) frame = requestAnimationFrame(update);
    }

    window.addEventListener('scroll', schedule, { passive: true, signal });
    window.addEventListener('resize', schedule, { passive: true, signal });

    if (!reduceMotion && 'IntersectionObserver' in window) {
        // Xuất hiện nhẹ khi cuộn: chỉ ẩn trước những khối đang nằm dưới màn hình.
        revealer = new IntersectionObserver(entries => {
            for (const entry of entries) {
                if (!entry.isIntersecting && entry.boundingClientRect.top > 0) continue;
                entry.target.setAttribute('data-reveal-state', 'shown');
                revealer.unobserve(entry.target);
            }
        }, { rootMargin: '0px 0px -8% 0px' });

        for (const item of root.querySelectorAll('[data-reveal]')) {
            if (item.getBoundingClientRect().top < window.innerHeight) continue;
            item.setAttribute('data-reveal-state', 'pending');
            revealer.observe(item);
        }

    }

    // Không để phím Home/End/mũi tên cuộn trang khi đang chọn tab (Blazor xử lý việc đổi tab).
    root.addEventListener('keydown', event => {
        if (event.target.matches?.('[role="tab"]') && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key))
            event.preventDefault();
    }, { signal });

    toTop?.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        window.scrollTo({ top: 0, behavior: reduceMotion ? 'auto' : 'smooth' });
        document.getElementById('main-content')?.focus({ preventScroll: true });
    }, { signal });

    // Mở thẳng /#faq: nội dung chỉ có sau khi circuit kết nối nên trình duyệt chưa tự cuộn được.
    const anchor = window.location.hash.slice(1);
    if (anchor) document.getElementById(anchor)?.scrollIntoView();

    update();

    const instance = {
        dispose() {
            aborter.abort();
            revealer?.disconnect();
            if (frame) cancelAnimationFrame(frame);
            frame = 0;
            root.querySelectorAll('[data-reveal-state]').forEach(item => item.removeAttribute('data-reveal-state'));
            header?.removeAttribute('data-scrolled');
            links.forEach(link => link.removeAttribute('aria-current'));
            instances.delete(root);
        }
    };
    instances.set(root, instance);
    return instance;
}
