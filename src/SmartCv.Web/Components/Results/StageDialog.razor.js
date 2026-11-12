// Native modal dialogs provide keyboard focus containment and make the page inert.
const openers = new WeakMap();
export function open(dialog, callback) {
    const active = document.activeElement;
    openers.set(dialog, active?.id || null);
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        callback.invokeMethodAsync('CancelFromBrowser');
    });
    dialog.addEventListener('click', event => {
        if (event.target !== dialog) return;
        const bounds = dialog.getBoundingClientRect();
        if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom)
            callback.invokeMethodAsync('CancelFromBrowser');
    });
    dialog.addEventListener('keydown', event => {
        if (event.key !== 'Tab') return;
        const available = [...dialog.querySelectorAll('button, input, select, textarea, [tabindex="0"]')]
            .filter(control => !control.matches(':disabled') && control.getClientRects().length > 0);
        // A radio group occupies one Tab stop: the checked option, or the first option.
        const controls = available.filter(control => {
            if (control.type !== 'radio') return true;
            const group = available.filter(other => other.type === 'radio' && other.name === control.name);
            return control === (group.find(other => other.checked) || group[0]);
        });
        const first = controls[0], last = controls.at(-1);
        if (!first) { event.preventDefault(); return; }
        if ((!event.shiftKey && document.activeElement === last) ||
            (event.shiftKey && document.activeElement === first) || !dialog.contains(document.activeElement)) {
            event.preventDefault();
            (event.shiftKey ? last : first).focus();
        }
    });
    if (!dialog.open) dialog.showModal();
}
export function close(dialog) {
    dialog.close();
    const id = openers.get(dialog);
    requestAnimationFrame(() => {
        const opener = id && document.getElementById(id);
        if (opener && !opener.disabled) opener.focus();
        else document.getElementById('ranking-title')?.focus();
    });
    openers.delete(dialog);
}
