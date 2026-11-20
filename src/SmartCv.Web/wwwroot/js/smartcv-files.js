// Binary content only. No credentials, API URLs, browser storage, or console output.
const active = new Map();
let revision = 0;
export function closeAll() { ++revision; for (const close of [...active.values()]) close(); }
window.addEventListener('pagehide', closeAll);

export async function open(streamRef, contentType, fileName, preview) {
    const started = revision;
    const buffer = await streamRef.arrayBuffer();
    if (started !== revision) throw new Error('Transfer cancelled.');
    if (buffer.byteLength > 64 * 1024 * 1024) throw new Error('File is too large.');
    const type = contentType.toLowerCase();
    const canPreview = ['application/pdf', 'image/png', 'image/jpeg'].includes(type);
    const url = URL.createObjectURL(new Blob([buffer], { type: contentType }));
    if (!preview || !canPreview) {
        const a = document.createElement('a');
        a.href = url; a.download = fileName || 'tai-xuong';
        document.body.append(a);
        try { a.click(); }
        finally {
            a.remove();
            const close = () => { URL.revokeObjectURL(url); active.delete(url); };
            active.set(url, close); setTimeout(close, 5000);
        }
        return false;
    }
    const previous = document.activeElement;
    const dialog = document.createElement('dialog');
    dialog.className = 'sc-file-preview'; dialog.setAttribute('aria-label', 'Xem file CV');
    const header = document.createElement('header');
    const name = document.createElement('strong'); name.textContent = fileName || 'File CV';
    const download = document.createElement('a'); download.href = url; download.download = fileName || 'tai-xuong'; download.textContent = 'Tải về';
    const closeButton = document.createElement('button'); closeButton.type = 'button'; closeButton.textContent = 'Đóng';
    const body = document.createElement(type === 'application/pdf' ? 'iframe' : 'img');
    body.src = url;
    if (body.tagName === 'IFRAME') body.title = 'Bản xem trước PDF'; else body.alt = 'Bản xem trước CV';
    header.append(name, download, closeButton); dialog.append(header, body); document.body.append(dialog);
    let closed = false;
    const close = () => {
        if (closed) return; closed = true;
        dialog.close(); dialog.remove(); URL.revokeObjectURL(url); active.delete(url);
        if (previous?.isConnected) previous.focus();
    };
    active.set(url, close); closeButton.addEventListener('click', close); dialog.addEventListener('close', close);
    try { dialog.showModal(); closeButton.focus(); }
    catch { close(); throw new Error('Preview is unavailable.'); }
    return true;
}
