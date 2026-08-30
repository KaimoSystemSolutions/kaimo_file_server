window.filePreview = {
    // Builds the preview. Returns { displayUrl, downloadUrl }:
    //   downloadUrl - blob of the ORIGINAL bytes, always usable for "Herunterladen".
    //   displayUrl  - blob to render inline (text/docx get a generated HTML doc),
    //                 or null when the file can't be shown inline.
    preparePreview: async function (bytes, contentType, kind) {
        const arr = new Uint8Array(bytes);
        const downloadUrl = URL.createObjectURL(
            new Blob([arr], { type: contentType || 'application/octet-stream' }));

        let displayUrl = null;
        try {
            switch (kind) {
                case 'Text':
                    displayUrl = buildTextDocUrl(arr);
                    break;
                case 'Docx':
                    displayUrl = await buildDocxDocUrl(arr);
                    break;
                case 'Image':
                case 'Video':
                case 'Audio':
                case 'Pdf':
                    displayUrl = downloadUrl;
                    break;
                default:
                    displayUrl = null; // Unsupported -> fallback panel
            }
        } catch (e) {
            console.error('Preview rendering failed', e);
            displayUrl = null;
        }

        return { displayUrl: displayUrl, downloadUrl: downloadUrl };
    },

    revokeBlob: function (url) {
        if (url) URL.revokeObjectURL(url);
    },

    // Triggers a browser download of raw bytes without keeping a blob URL around.
    downloadBytes: function (bytes, contentType, fileName) {
        const arr = new Uint8Array(bytes);
        const url = URL.createObjectURL(
            new Blob([arr], { type: contentType || 'application/octet-stream' }));
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName || 'download';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        // Give the browser a tick to start the download before revoking.
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },

    // Streams a download straight from a server URL (the response carries
    // Content-Disposition: attachment), without loading the file into memory first.
    downloadFromUrl: function (url, fileName) {
        const a = document.createElement('a');
        a.href = url;
        if (fileName) a.download = fileName;
        a.rel = 'noopener';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }
};

function escapeHtml(text) {
    return text
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
}

// Wraps arbitrary inner HTML in a themed, self-contained document for the iframe.
function wrapHtmlDocument(innerHtml, monospace) {
    const fontStack = monospace
        ? "font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 13px; white-space: pre-wrap; word-wrap: break-word;"
        : "font-family: 'Plus Jakarta Sans', system-ui, sans-serif; font-size: 15px; line-height: 1.6;";

    return `<!DOCTYPE html><html><head><meta charset="utf-8">
        <style>
            html, body { margin: 0; }
            body {
                background: #1e1e1e;
                color: #e0e0e0;
                padding: 1.5rem;
                ${fontStack}
            }
            a { color: #6ea8fe; }
            table { border-collapse: collapse; }
            td, th { border: 1px solid #444; padding: 4px 8px; }
            img { max-width: 100%; height: auto; }
            h1, h2, h3 { color: #fff; }
        </style></head><body>${innerHtml}</body></html>`;
}

function buildTextDocUrl(arr) {
    const text = new TextDecoder('utf-8', { fatal: false }).decode(arr);
    const html = wrapHtmlDocument(escapeHtml(text), true);
    return URL.createObjectURL(new Blob([html], { type: 'text/html' }));
}

async function buildDocxDocUrl(arr) {
    if (typeof mammoth === 'undefined') {
        throw new Error('mammoth.js not loaded');
    }
    // mammoth needs a plain ArrayBuffer; slice to drop any offset/extra capacity.
    const buffer = arr.buffer.slice(arr.byteOffset, arr.byteOffset + arr.byteLength);
    const result = await mammoth.convertToHtml({ arrayBuffer: buffer });
    const html = wrapHtmlDocument(result.value || '<em>Leeres Dokument</em>', false);
    return URL.createObjectURL(new Blob([html], { type: 'text/html' }));
}
