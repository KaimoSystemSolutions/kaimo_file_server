// Editable path bars (Components/Shared/PathBar.razor): when the text field opens, its whole
// path is selected so it can be copied or overwritten right away.
document.addEventListener('focusin', event => {
    const target = event.target as Element | null;
    if (target instanceof HTMLInputElement && target.classList.contains('path-bar-input')) target.select();
});
