// Admin report visual enhancement only. This module never fetches APIs or handles JWT.
// Input contains bounded dates + aggregate non-identifying integers, provided by server-side Blazor.
(() => {
  'use strict';
  const NS = 'http://www.w3.org/2000/svg';
  const labels = { quota: 'Đơn vị quota ròng', fail: 'Lượt chấm lỗi', audit: 'Audit bị từ chối' };
  const vi = new Intl.NumberFormat('vi-VN');
  const svgElement = (name, attrs = {}) => {
    const el = document.createElementNS(NS, name);
    for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, String(v));
    return el;
  };
  function draw(id, metric, rows) {
    const node = document.getElementById(id);
    if (!node || !Array.isArray(rows) || !(metric in labels)) return;
    node.replaceChildren();
    const data = rows.slice(0, 90).map(r => ({
      date: typeof r.date === 'string' ? r.date.slice(0, 10) : '',
      value: Math.max(0, Number.isFinite(Number(r[metric])) ? Number(r[metric]) : 0)
    }));
    if (!data.length) {
      const empty = document.createElement('div');
      empty.className = 'aor-chart-empty';
      empty.textContent = 'Không có điểm dữ liệu trong khoảng đã chọn.';
      node.appendChild(empty);
      return;
    }
    const width = 900, height = 250, left = 58, right = 18, top = 23, bottom = 38;
    const plotW = width - left - right, plotH = height - top - bottom;
    const max = Math.max(1, ...data.map(x => x.value));
    const x = i => left + (data.length === 1 ? plotW / 2 : i * plotW / (data.length - 1));
    const y = v => top + plotH - v * plotH / max;
    const svg = svgElement('svg', { viewBox: `0 0 ${width} ${height}`, role: 'img', 'aria-label': `${labels[metric]} theo ${data.length} ngày` });
    for (let i = 0; i <= 4; i++) {
      const value = Math.round(max * i / 4), py = y(value);
      svg.appendChild(svgElement('line', { x1: left, y1: py, x2: width - right, y2: py, class: 'aor-grid' }));
      const t = svgElement('text', { x: left - 10, y: py + 4, 'text-anchor': 'end' });
      t.textContent = vi.format(value); svg.appendChild(t);
    }
    const points = data.map((d,i) => `${x(i)},${y(d.value)}`);
    const area = `${left},${top+plotH} ${points.join(' ')} ${x(data.length-1)},${top+plotH}`;
    svg.appendChild(svgElement('polygon', { points: area, class: 'aor-area' }));
    svg.appendChild(svgElement('polyline', { points: points.join(' '), class: 'aor-line' }));
    const indices = [...new Set([0, Math.floor((data.length-1)/2), data.length-1])];
    for (const i of indices) {
      const text = svgElement('text', { x: x(i), y: height-12, 'text-anchor': i===0?'start':i===data.length-1?'end':'middle' });
      text.textContent = data[i].date.slice(5).split('-').reverse().join('/'); svg.appendChild(text);
    }
    // Title works with mouse, keyboard, and assistive technologies; no external tooltip dependency.
    for (let i=0; i<data.length; i++) {
      const c = svgElement('circle', { cx:x(i), cy:y(data[i].value), r:4.3,
        tabindex:0, role:'graphics-symbol', 'aria-label':`${data[i].date}: ${vi.format(data[i].value)} ${labels[metric]}` });
      const title = svgElement('title'); title.textContent = `${data[i].date}: ${vi.format(data[i].value)}`;
      c.appendChild(title); svg.appendChild(c);
    }
    node.appendChild(svg);
  }
  // Lightweight keyboard enhancements; the server-side Blazor handlers still own all actions.
  document.addEventListener('keydown', e => {
    const page = document.querySelector('.aor-page');
    if (!page) return;
    const close = page.querySelector('[data-aor-dialog-close]');
    if (e.key === 'Escape' && close && !close.disabled) {
      e.preventDefault(); close.click(); return;
    }
    if (e.ctrlKey && e.key === 'Enter' && !e.repeat) {
      const target = e.target;
      if (target instanceof Element && target.closest('input,textarea,select,[contenteditable]')) return;
      const apply = page.querySelector('[data-aor-apply]');
      if (apply && !apply.disabled) { e.preventDefault(); apply.click(); }
    }
  });
  window.scappAdminReports = Object.freeze({ draw });
})();
