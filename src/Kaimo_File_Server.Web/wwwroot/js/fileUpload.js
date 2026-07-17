function initFileUpload(elementSelector, dotNetRef) {

    function isOsFileDrag(e) {
        return !!e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files');
    }

    document.addEventListener('dragenter', e => {
        if (!isOsFileDrag(e)) return;

        const el = document.querySelector(elementSelector);
        e.preventDefault();
        if (el?.contains(e.target)) {
            el.classList.add('file-dragged-over');
        }
    });

    document.addEventListener("dragstart", e => {
        e.dataTransfer.setData("text/html", "...")
    })

    document.addEventListener('dragover', e => {
        if (!isOsFileDrag(e)) return;

        const el = document.querySelector(elementSelector);
        e.preventDefault();

        if (el?.contains(e.target)) {
            el.classList.add('file-dragged-over');
        }
    });

    document.addEventListener('dragleave', e => {
        if (!isOsFileDrag(e)) return;

        const el = document.querySelector(elementSelector);
        if (el && !el.contains(e.relatedTarget)) {
            el.classList.remove('file-dragged-over');
        }
    });

    document.addEventListener('drop', async e => {
        if (!isOsFileDrag(e)) return;

        e.preventDefault();
        const el = document.querySelector(elementSelector);
        console.log('drop fired', e.target, 'el:', el, 'contains:', el?.contains(e.target));

        if (el?.contains(e.target)) {
            el.classList.remove('file-dragged-over');
            const input = document.getElementById('global-file-input');
            input.files = e.dataTransfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }
    });
}