// Only a per-workspace presentation preference; never store user secrets or CV data here.
export function readHidden(workspaceId) {
    try { return localStorage.getItem(`scapp:b56:onboarding:hidden:${workspaceId}`) === "1"; }
    catch { return false; }
}
export function hide(workspaceId) {
    try { localStorage.setItem(`scapp:b56:onboarding:hidden:${workspaceId}`, "1"); return true; }
    catch { return false; }
}
export function show(workspaceId) {
    try { localStorage.removeItem(`scapp:b56:onboarding:hidden:${workspaceId}`); return true; }
    catch { return false; }
}
