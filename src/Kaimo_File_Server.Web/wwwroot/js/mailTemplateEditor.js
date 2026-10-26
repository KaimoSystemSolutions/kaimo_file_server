"use strict";
// Inserts a placeholder at the caret of the mail template textarea and notifies Blazor,
// so the bound value and the live preview follow.
window.mailTemplateEditor = {
    insertAtCursor: function (element, text) {
        var _a, _b;
        if (!element)
            return;
        const start = (_a = element.selectionStart) !== null && _a !== void 0 ? _a : element.value.length;
        const end = (_b = element.selectionEnd) !== null && _b !== void 0 ? _b : start;
        element.value = element.value.slice(0, start) + text + element.value.slice(end);
        element.selectionStart = element.selectionEnd = start + text.length;
        element.focus();
        element.dispatchEvent(new Event('input', { bubbles: true }));
    }
};
