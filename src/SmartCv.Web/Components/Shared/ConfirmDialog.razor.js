const openers = new WeakMap();
export function open(dialog, callback, returnFocusId) {
    openers.set(dialog, returnFocusId || document.activeElement?.id);
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        callback.invokeMethodAsync('CancelFromBrowser');
    });
    dialog.addEventListener('keydown', event => {
        if (event.key !== 'Tab') return;
        const controls = [...dialog.querySelectorAll('button:not(:disabled)')];
        const first = controls[0], last = controls.at(-1);
        if (!first) { event.preventDefault(); return; }
        if ((event.shiftKey && document.activeElement === first) || (!event.shiftKey && document.activeElement === last)) {
            event.preventDefault();
            (event.shiftKey ? last : first).focus();
        }
    });
    dialog.showModal();
}
export function close(dialog, returnFocusId) {
    // Blazor may already have removed the element when disposing the component.
    dialog?.close();
    const id = returnFocusId || (dialog && openers.get(dialog));
    requestAnimationFrame(() => {
        const opener = id && document.getElementById(id);
        if (opener && !opener.disabled) opener.focus();
        else document.getElementById('jobs-title')?.focus();
    });
    if (dialog) openers.delete(dialog);
}
