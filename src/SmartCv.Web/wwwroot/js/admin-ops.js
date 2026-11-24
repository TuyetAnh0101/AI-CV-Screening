/* SCApp Admin Operations: accessible progressive enhancement only.
   No API calls, credentials, token storage, or HTML injection. */
(() => {
  "use strict";
  const chartState = new WeakMap();
  const historyState = new WeakMap();
  const clamp = (x, min, max) => Math.max(min, Math.min(max, x));
  const num = (x) => Number.isFinite(+x) ? Math.max(0, Number(x)) : 0;
  const smallDate = (s) => {
    if (!s) return "";
    const parts = String(s).slice(0, 10).split("-");
    return parts.length === 3 ? `${parts[2]}/${parts[1]}` : String(s);
  };

  function draw(canvas, points) {
    if (!(canvas instanceof HTMLCanvasElement) || !canvas.isConnected) return;
    const width = Math.max(260, canvas.getBoundingClientRect().width);
    const height = Math.max(180, canvas.getBoundingClientRect().height);
    const scale = Math.min(devicePixelRatio || 1, 2);
    canvas.width = Math.round(width * scale);
    canvas.height = Math.round(height * scale);
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    ctx.scale(scale, scale);
    ctx.clearRect(0, 0, width, height);
    const l = 40, r = 15, t = 17, b = 36;
    const pw = width - l - r, ph = height - t - b;
    const totals = points.map(p => num(p.screeningUnits) + num(p.promptTestUnits) + num(p.previewUnits));
    const max = Math.max(1, ...totals);
    const roundMax = Math.ceil(max / 4) * 4 || 4;
    ctx.font = "12px Inter, system-ui, sans-serif";
    for (let line = 0; line <= 4; line++) {
      const y = t + ph * line / 4;
      ctx.strokeStyle = "#e6efee";
      ctx.beginPath(); ctx.moveTo(l, y); ctx.lineTo(width - r, y); ctx.stroke();
      ctx.fillStyle = "#738a90";
      ctx.textAlign = "right";
      ctx.fillText(String(Math.round(roundMax * (4 - line) / 4)), l - 9, y + 4);
    }
    const slot = pw / Math.max(1, points.length);
    const bw = clamp(slot * .62, 2, 33);
    points.forEach((p, i) => {
      const x = l + (i + .5) * slot - bw / 2;
      let y = t + ph;
      const segments = [
        [num(p.screeningUnits), "#eb866d"],
        [num(p.promptTestUnits), "#8f77ce"],
        [num(p.previewUnits), "#59a8d4"]
      ];
      for (const [v, color] of segments) {
        const h = (v / roundMax) * ph;
        if (h > 0) { y -= h; ctx.fillStyle = color; ctx.fillRect(x, y, bw, h); }
      }
    });
    const stride = Math.max(1, Math.ceil(points.length / 7));
    points.forEach((p, i) => {
      if (i % stride && i !== points.length - 1) return;
      ctx.fillStyle = "#7a8d96";
      ctx.font = "11px Inter, system-ui, sans-serif";
      ctx.textAlign = "center";
      ctx.fillText(smallDate(p.date), l + (i + .5) * slot, t + ph + 21);
    });
  }

  // Distinct chart for persisted evaluation outcomes, not AI quota or provider tokens.
  function drawHistory(canvas, info) {
    if (!(canvas instanceof HTMLCanvasElement) || !canvas.isConnected) return;
    const points = info.points;
    const width = Math.max(260, canvas.getBoundingClientRect().width);
    const height = Math.max(180, canvas.getBoundingClientRect().height);
    const scale = Math.min(devicePixelRatio || 1, 2);
    canvas.width = Math.round(width * scale);
    canvas.height = Math.round(height * scale);
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    ctx.scale(scale, scale);
    ctx.clearRect(0, 0, width, height);
    const left = 45, right = 16, top = 18, bottom = 38;
    const plotW = width - left - right;
    const plotH = height - top - bottom;
    const maxCount = Math.max(1, ...points.map(p => p.combined + p.keywordOnly + p.failed + p.other));
    const tick = Math.max(1, Math.ceil(maxCount / 4));
    const upper = tick * 4;
    ctx.font = "12px Inter, system-ui, sans-serif";
    for (let k = 0; k < 5; k++) {
      const y = top + plotH * (k / 4);
      ctx.beginPath(); ctx.strokeStyle = "#e7eeee";
      ctx.moveTo(left, y); ctx.lineTo(width - right, y); ctx.stroke();
      ctx.fillStyle = "#748b91"; ctx.textAlign = "right";
      ctx.fillText(String((4 - k) * tick), left - 9, y + 4);
    }
    if (!points.length) return;
    const slot = plotW / points.length;
    const bw = clamp(slot * 0.64, 2, 40);
    points.forEach((p, i) => {
      const x = left + (i + 0.5) * slot - bw / 2;
      let y = top + plotH;
      const segments = [
        [p.combined, "#219884"], [p.keywordOnly, "#9377d0"],
        [p.failed, "#e57260"], [p.other, "#9eafb2"]
      ];
      for (const [v, color] of segments) {
        const h = Math.max(0, v) / upper * plotH;
        if (h > 0) { y -= h; ctx.fillStyle = color; ctx.fillRect(x, y, bw, h); }
      }
    });
    const stride = Math.max(1, Math.ceil(points.length / 7));
    points.forEach((p, i) => {
      if (i % stride && i !== points.length - 1) return;
      const date = smallDate(p.bucket);
      const text = info.bucket === "month" ? String(p.bucket).slice(5, 7) + "/" + String(p.bucket).slice(2, 4) : date;
      ctx.fillStyle = "#778c93"; ctx.font = "11px Inter, system-ui, sans-serif"; ctx.textAlign = "center";
      ctx.fillText(text, left + (i + .5) * slot, top + plotH + 22);
    });
  }

  function historyChart(canvas, items, bucket) {
    if (!(canvas instanceof HTMLCanvasElement) || !Array.isArray(items)) return;
    const safe = items.map(p => ({
      bucket: p.bucket ?? p.Bucket,
      combined: num(p.combined ?? p.Combined),
      keywordOnly: num(p.keywordOnly ?? p.KeywordOnly),
      failed: num(p.failed ?? p.Failed),
      other: num(p.other ?? p.Other)
    }));
    const info = { points: safe, bucket: bucket === "month" ? "month" : "day" };
    historyState.set(canvas, info);
    drawHistory(canvas, info);
    observer?.observe(canvas);
    // Screen readers can use the adjacent accessible table; pointer users see exact bucket totals.
    canvas.onpointermove = evt => {
      const rect = canvas.getBoundingClientRect();
      const plotW = Math.max(1, rect.width - 45 - 16);
      const index = Math.floor(((evt.clientX - rect.left) - 45) / plotW * safe.length);
      const p = safe[index];
      if (p) {
        const date = info.bucket === "month" ? String(p.bucket).slice(0, 7) : String(p.bucket).slice(0, 10);
        canvas.title = `${date}: Combined ${p.combined}; KeywordOnly ${p.keywordOnly}; Failed ${p.failed}; Khác ${p.other}`;
      }
    };
  }

  const observer = typeof ResizeObserver !== "undefined"
    ? new ResizeObserver(entries => {
        for (const entry of entries) {
          const canvas = entry.target;
          if (!canvas.isConnected) { observer.unobserve(canvas); continue; }
          const history = historyState.get(canvas);
          if (history) drawHistory(canvas, history);
          else {
            const points = chartState.get(canvas);
            if (points) draw(canvas, points);
          }
        }
      }) : null;

  function chart(canvas, points) {
    if (!(canvas instanceof HTMLCanvasElement) || !Array.isArray(points)) return;
    const safe = points.map(p => ({
      date: p.date || p.Date,
      screeningUnits: num(p.screeningUnits ?? p.ScreeningUnits),
      promptTestUnits: num(p.promptTestUnits ?? p.PromptTestUnits),
      previewUnits: num(p.previewUnits ?? p.PreviewUnits)
    }));
    chartState.set(canvas, safe);
    draw(canvas, safe);
    observer?.observe(canvas);
  }

  let toastTimer;
  function toast(message) {
    document.querySelectorAll(".op-toast").forEach(el => el.remove());
    const el = document.createElement("div");
    el.className = "op-toast";
    el.setAttribute("role", "status");
    el.textContent = String(message).slice(0, 240);
    document.body.appendChild(el);
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.remove(), 4200);
  }

  async function copy(value) {
    try {
      if (!navigator.clipboard?.writeText) throw new Error("Clipboard unavailable");
      await navigator.clipboard.writeText(String(value));
      toast("Đã sao chép đường dẫn cấu hình.");
    } catch {
      toast("Trình duyệt không cho phép sao chép. Hãy chọn và sao chép thủ công.");
    }
  }

  let beforeModalOverflow = "";
  let beforeModalFocus = null;
  function lockModal(locked) {
    if (locked) {
      beforeModalOverflow = document.body.style.overflow;
      beforeModalFocus = document.activeElement;
      document.body.style.overflow = "hidden";
      setTimeout(() => document.querySelector(".op-modal input, .op-modal select, .op-modal button")?.focus(), 0);
    } else {
      document.body.style.overflow = beforeModalOverflow;
      beforeModalFocus?.focus?.();
      beforeModalFocus = null;
    }
  }

  document.addEventListener("keydown", e => {
    const modal = document.querySelector(".op-modal");
    if (e.key === "Tab" && modal) {
      const controls = [...modal.querySelectorAll("button:not(:disabled),input:not(:disabled),select:not(:disabled),a[href]")]
        .filter(el => el.getClientRects().length > 0);
      if (controls.length) {
        const first = controls[0], last = controls[controls.length - 1];
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      }
    }
    if (e.key === "Escape") document.querySelector(".op-modal [data-op-close]")?.click();
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
      const search = document.getElementById("op-workspace-search");
      if (search) { e.preventDefault(); search.focus(); search.select(); }
    }
  });
  window.addEventListener("pagehide", () => { document.body.style.overflow = beforeModalOverflow; });

  window.scAdminOps = Object.freeze({ chart, historyChart, copy, toast, lockModal });
})();
