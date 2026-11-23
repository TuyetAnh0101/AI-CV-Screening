// Progressive enhancement only: never read JWT, sessionStorage or CV content.
// Blazor owns search state; the backend authorizes every request independently.
window.scappRolePermissions = Object.freeze({
  async copy(value) {
    if (typeof value !== "string" || value.length > 10000) return false;
    if (!navigator.clipboard?.writeText) return false;
    try { await navigator.clipboard.writeText(value); return true; }
    catch (_) { return false; }
  }
});

// / focuses permissions search (except while typing elsewhere).
// Escape clears search using a native input event so the Blazor binding stays in sync.
document.addEventListener("keydown", event => {
  const input = document.getElementById("scapp-permissions-search");
  if (!input || event.altKey || event.ctrlKey || event.metaKey) return;
  const active = document.activeElement;
  const editing = active instanceof HTMLElement &&
    (active.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(active.tagName));
  if (event.key === "/" && !editing) {
    event.preventDefault();
    input.focus();
  } else if (event.key === "Escape" && active === input && input.value) {
    input.value = "";
    input.dispatchEvent(new Event("input", { bubbles: true }));
    event.preventDefault();
  }
});
