// Inserts a placeholder at the caret of the mail template textarea and notifies Blazor,
// so the bound value and the live preview follow.
window.mailTemplateEditor = {
    insertAtCursor: function (element, text) {
        if (!element) return;
        const start = element.selectionStart ?? element.value.length;
        const end = element.selectionEnd ?? start;
        element.value = element.value.slice(0, start) + text + element.value.slice(end);
        element.selectionStart = element.selectionEnd = start + text.length;
        element.focus();
        element.dispatchEvent(new Event('input', { bubbles: true }));
    }
};
