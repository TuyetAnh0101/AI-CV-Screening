// B38: hộp "Chữ đã đọc". Chỉ dùng cho component ExtractedTextDialog, tải bằng import() khi hộp mở.
// Cần JS vì Blazor không tự gọi được dialog.showModal(): đó là cách duy nhất có lớp phủ thật (phần còn lại của trang không
// bấm / không Tab tới được), Escape đóng hộp và hộp nằm trên mọi thành phần khác. Không đọc, không gửi nội dung CV đi đâu;
// copyText chỉ chạy khi người dùng bấm "Sao chép chữ".
export function open(dialog, dotnet) {
    if (!dialog) return;
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        dotnet.invokeMethodAsync('CloseFromBrowser');
    });
    dialog.addEventListener('click', event => {
        if (event.target !== dialog) return;   // chỉ khi bấm vào nền mờ bên ngoài khung
        const bounds = dialog.getBoundingClientRect();
        if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom)
            dotnet.invokeMethodAsync('CloseFromBrowser');
    });
    dialog.addEventListener('keydown', event => {
        if (event.key !== 'Tab') return;
        const controls = [...dialog.querySelectorAll('button, a[href], input, select, textarea, [tabindex="0"]')]
            .filter(control => !control.matches(':disabled') && control.getClientRects().length > 0);
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

export function close(dialog, focusIds) {
    if (dialog && dialog.open) dialog.close();
    requestAnimationFrame(() => {
        for (const id of focusIds || []) {
            const target = id && document.getElementById(id);
            if (target && !target.disabled && target.getClientRects().length > 0) {
                target.focus();
                return;
            }
        }
    });
}

export async function copyText(element) {
    try {
        await navigator.clipboard.writeText(element ? element.textContent : '');
        return true;
    } catch {
        return false;
    }
}
