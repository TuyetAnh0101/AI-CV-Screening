// B55: called from interactive Blazor before ANY exchange request. Fragment must never be logged, stored or re-used.
export function readAndClearFragment() {
    const fragment = window.location.hash.startsWith('#') ? window.location.hash.substring(1) : '';
    window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search);
    return fragment;
}
