// B24: hộp thoại dùng thẻ <dialog> gốc (showModal): trình duyệt giữ focus bên trong, Escape gửi sự kiện 'cancel'.
// Không tự đóng khi đang lưu: Blazor quyết định qua CancelFromBrowser.
export function open(dialog, callback, focusId) {
    if (!dialog) return;
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        callback.invokeMethodAsync('CancelFromBrowser');
    });
    if (!dialog.open) dialog.showModal();
    const first = (focusId && document.getElementById(focusId)) || dialog.querySelector('[autofocus], input, select, textarea, button');
    first?.focus();
}

export function close(dialog, returnFocusId, fallbackId) {
    if (dialog?.open) dialog.close();
    requestAnimationFrame(() => {
        const opener = returnFocusId && document.getElementById(returnFocusId);
        if (opener && !opener.disabled) { opener.focus(); return; }
        const fallback = fallbackId && document.getElementById(fallbackId);
        fallback?.focus();
    });
}
