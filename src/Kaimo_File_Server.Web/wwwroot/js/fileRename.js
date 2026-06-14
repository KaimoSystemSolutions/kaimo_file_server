function selectFileName(selector) {
    const input = document.querySelector(selector);
    
    if (!input) return;
    const val = input.value;
    const lastDot = val.lastIndexOf('.');
    const end = lastDot > 0 ? lastDot : val.length;
    input.focus();
    input.setSelectionRange(0, end);
    
}