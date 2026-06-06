function initFileUpload(element, dotNetRef) {

    element.addEventListener('dragenter', e => {
        e.preventDefault();
        element.classList.add('file-dragged-over');
    });

    element.addEventListener('dragover', e => {
        e.preventDefault();
    });

    element.addEventListener('dragleave', e => {
        e.preventDefault();

        if (!element.contains(e.relatedTarget)) {
            element.classList.remove('file-dragged-over');
        }
    });

    element.addEventListener('drop', async e => {
        e.preventDefault();
        element.classList.remove('file-dragged-over');

        const input = document.getElementById('global-file-input');
        input.files = e.dataTransfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    });
}