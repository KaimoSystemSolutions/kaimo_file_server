window.filePreview = {
    showBlob: function (bytes, contentType) {
        if (contentType.includes("text")) {
            const text = new TextDecoder().decode(new Uint8Array(bytes));
            const html = `<html><body style="
                background: var(--bg-tertiary);
                color: #e0e0e0;
                font-family: monospace;
                font-size: 14px;
                padding: 1.5rem;
                margin: 0;
                white-space: pre-wrap;
                word-wrap: break-word;
            ">${text.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')}</body></html>`;
            const blob = new Blob([html], { type: 'text/html' });
            return URL.createObjectURL(blob);
        }
        const blob = new Blob([bytes], { type: contentType });
        return URL.createObjectURL(blob);
    },
    revokeBlob: function (url) {
        URL.revokeObjectURL(url);
    }
};