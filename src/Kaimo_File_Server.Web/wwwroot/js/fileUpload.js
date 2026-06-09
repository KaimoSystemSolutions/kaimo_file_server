function initFileUpload(elementSelector, dotNetRef) {

    document.addEventListener('dragenter', e => {
        const el = document.querySelector(elementSelector);
        if (el?.contains(e.target)) {
            e.preventDefault();
            el.classList.add('file-dragged-over');
        }
    });

    document.addEventListener('dragover', e => {
        const el = document.querySelector(elementSelector);
        if (el?.contains(e.target)) {
            e.preventDefault();
            el.classList.add('file-dragged-over');
        }
    });

    document.addEventListener('dragleave', e => {
        const el = document.querySelector(elementSelector);
        if (el && !el.contains(e.relatedTarget)) {
            el.classList.remove('file-dragged-over');
        }
    });

    document.addEventListener('drop', async e => {
        const el = document.querySelector(elementSelector);
        if (el?.contains(e.target)) {
            e.preventDefault();
            el.classList.remove('file-dragged-over');
            const input = document.getElementById('global-file-input');
            input.files = e.dataTransfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }
    });
}