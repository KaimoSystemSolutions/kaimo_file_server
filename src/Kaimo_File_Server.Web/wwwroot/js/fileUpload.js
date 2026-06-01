function initFileUpload(element, dotNetRef) {

    element.addEventListener('dragenter', e => {
        e.preventDefault();
        element.classList.add('file-dragged-over');
    });

    element.addEventListener('dragover', e => {
        e.preventDefault(); // Required to allow dropping
    });

    element.addEventListener('dragleave', e => {
        e.preventDefault();

        // Check if the cursor is actually leaving the element
        // .contains(null) is false, so leaving the window entirely is also handled safely
        if (!element.contains(e.relatedTarget)) {
            element.classList.remove('file-dragged-over');
        }
    });

    element.addEventListener('drop', async e => {
        e.preventDefault();
        element.classList.remove('file-dragged-over');

        const input = element.querySelector('input[type=file]');
        input.files = e.dataTransfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    });
}