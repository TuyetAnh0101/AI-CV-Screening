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

// Blazor Server can deliver this call before the render batch that re-enables the opener (e.g. "Xuất kết quả" is
// disabled while its dialog is open). Wait up to ~20 frames for the opener to become focusable before the fallback,
// so keyboard users are not dropped on the heading by a timing race.
export function close(dialog, returnFocusId, fallbackId) {
    if (dialog?.open) dialog.close();
    let frames = 0;
    const restore = () => {
        const opener = returnFocusId && document.getElementById(returnFocusId);
        if (opener && !opener.disabled) { opener.focus(); return; }
        if (opener && ++frames < 20) { requestAnimationFrame(restore); return; }
        const fallback = fallbackId && document.getElementById(fallbackId);
        fallback?.focus();
    };
    requestAnimationFrame(restore);
}
