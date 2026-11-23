/* SCApp FIX-B56-UI · Progressive enhancement only.
 * Không gọi API; không giữ token, tên hoặc dữ liệu ứng viên.
 * Khi JS bị tắt, bộ lọc Blazor và các nút vẫn hoạt động bình thường.
 */
(() => {
  "use strict";
  if (window.__scappUiRefinement) return;
  window.__scappUiRefinement = true;

  const reduceMotion = () =>
    window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? true;

  function ensureVisible(button) {
    const rail = button.closest(".ranking .fxui-scroll-rail");
    if (!rail || rail.scrollWidth <= rail.clientWidth + 3) return;

    const left = button.getBoundingClientRect().left -
      rail.getBoundingClientRect().left + rail.scrollLeft;
    const target = left - (rail.clientWidth - button.offsetWidth) / 2;
    const max = Math.max(0, rail.scrollWidth - rail.clientWidth);
    rail.scrollTo({
      left: Math.min(max, Math.max(0, target)),
      behavior: reduceMotion() ? "auto" : "smooth"
    });
  }

  document.addEventListener("click", (event) => {
    if (!(event.target instanceof Element)) return;
    const button = event.target.closest(".ranking .fxui-scroll-rail button");
    if (!button) return;
    window.requestAnimationFrame(() => {
      if (button.isConnected) ensureVisible(button);
    });
  });

  // Hỗ trợ điều hướng nút lọc theo hàng bằng phím mũi tên.
  document.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    if (!(event.target instanceof Element)) return;
    const button = event.target.closest(".ranking .fxui-scroll-rail button");
    if (!button) return;
    const rail = button.closest(".fxui-scroll-rail");
    if (!rail) return;
    const buttons = Array.from(rail.querySelectorAll("button:not(:disabled)"));
    const at = buttons.indexOf(button);
    if (at < 0 || buttons.length < 2) return;
    event.preventDefault();
    const direction = event.key === "ArrowRight" ? 1 : -1;
    const next = buttons[(at + direction + buttons.length) % buttons.length];
    next.focus();
    ensureVisible(next);
  });
})();
