export function open(dialog, callback) {
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        callback.invokeMethodAsync('CancelFromBrowser');
    });
    // Native modal dialogs keep keyboard focus inside, including inputs and paging controls.
    dialog.showModal();
    dialog.querySelector('#attach-close')?.focus();
}
export function close(dialog, returnFocusId) {
    dialog?.close();
    requestAnimationFrame(() => {
        const opener = returnFocusId && document.getElementById(returnFocusId);
        if (opener && !opener.disabled) opener.focus();
        else document.getElementById('cvs-title')?.focus();
    });
}
