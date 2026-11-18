const bindings = new WeakMap();
const active = new Set();

export function bind(input, callback) {
    unbind(null);
    if (!input || bindings.has(input)) return;
    const form = input.closest('form');
    const state = { input, callback, form, composing: false, blockSubmit: false, disposed: false,
        pending: 0, queue: Promise.resolve(), current: input.value, rejected: [], revision: 0 };
    const draft = () => [...state.rejected, state.current].filter(Boolean).join(',');
    const paint = () => { input.value = draft(); };
    const enqueue = action => {
        state.pending++;
        state.queue = state.queue.then(async () => { if (!state.disposed) await action(); })
            .catch(() => { if (!state.disposed) console.error('Không đồng bộ được ô từ khóa.'); })
            .finally(() => { state.pending--; });
    };
    const reportDraft = value => enqueue(() => callback.invokeMethodAsync('DraftFromBrowser', value));
    const commit = () => {
        const value = input.value;
        const revision = ++state.revision;
        state.current = ''; state.rejected = []; paint();
        enqueue(async () => {
            const result = await callback.invokeMethodAsync('CommitFromBrowser', value);
            if (state.disposed) return;
            if (!result.accepted) {
                if (value) state.rejected.push(value);
                paint(); reportDraft(draft());
            } else if (state.revision === revision) {
                state.current = result.draft;
                paint();
                if (state.rejected.length) reportDraft(draft());
            }
        });
    };
    state.inputEvent = () => { state.current = input.value; state.rejected = []; state.revision++; reportDraft(state.current); };
    state.start = () => { state.composing = true; };
    state.end = () => { state.composing = false; };
    state.key = event => {
        if (input.disabled) return;
        if (state.composing || event.isComposing || event.keyCode === 229) {
            if (event.key === 'Enter') { state.blockSubmit = true; setTimeout(() => { state.blockSubmit = false; }, 0); }
            return;
        }
        if (event.key === 'Enter' || event.key === ',') { event.preventDefault(); commit(); }
        else if (event.key === 'Backspace' && input.value === '') {
            event.preventDefault(); enqueue(() => callback.invokeMethodAsync('RemoveLastFromBrowser'));
        }
    };
    state.blur = () => {
        if (input.disabled) return;
        if (state.composing) {
            state.composing = false; state.current = input.value; state.rejected = []; state.revision++;
            reportDraft(state.current);
        } else commit();
    };
    state.submit = event => {
        if (!input.isConnected) { unbind(input); return; }
        if (event.target !== form) return;
        if (state.composing || state.blockSubmit) { event.preventDefault(); event.stopImmediatePropagation(); return; }
        // A click on Save can follow blur before its interop has completed. Flush drafts first.
        if (state.pending > 0) {
            event.preventDefault(); event.stopImmediatePropagation();
            const submitter = event.submitter;
            state.queue.then(() => {
                if (!state.disposed && form.isConnected && !state.composing && !submitter?.disabled)
                    form.requestSubmit(submitter?.isConnected ? submitter : undefined);
            });
        }
    };
    state.click = event => {
        const button = event.target.closest?.('button');
        const row = input.closest('.skill-row');
        if (!button || !row?.contains(button) || state.pending === 0) return;
        // Move/delete can unmount this input. Persist its latest text before the row action.
        event.preventDefault(); event.stopImmediatePropagation();
        state.queue.then(() => { if (!state.disposed && button.isConnected && !button.disabled) button.click(); });
    };
    input.addEventListener('input', state.inputEvent);
    input.addEventListener('keydown', state.key);
    input.addEventListener('blur', state.blur);
    input.addEventListener('compositionstart', state.start);
    input.addEventListener('compositionend', state.end);
    window.addEventListener('submit', state.submit, true);
    window.addEventListener('click', state.click, true);
    bindings.set(input, state); active.add(state);
}

export function sync(input, serverDraft) {
    const state = input && bindings.get(input);
    if (!state || state.pending > 0 || state.composing) return;
    state.current = serverDraft; state.rejected = []; input.value = serverDraft;
}

export function unbind(input) {
    // ElementReference can be null after Blazor removes a row; also release detached handlers.
    for (const state of active) {
        const element = state.input;
        if (element !== input && element.isConnected) continue;
        state.disposed = true;
        element.removeEventListener('input', state.inputEvent);
        element.removeEventListener('keydown', state.key);
        element.removeEventListener('blur', state.blur);
        element.removeEventListener('compositionstart', state.start);
        element.removeEventListener('compositionend', state.end);
        window.removeEventListener('submit', state.submit, true);
        window.removeEventListener('click', state.click, true);
        bindings.delete(element); active.delete(state);
    }
}
