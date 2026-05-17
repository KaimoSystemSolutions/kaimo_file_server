using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Zentralisiert die Theme-Logik (localStorage lesen/schreiben, data-theme setzen).
/// Ersetzt die duplizierte Logik in MainLayout und EmptyLayout.
/// </summary>
public class ThemeService
{
    private readonly IJSRuntime _js;
    private string _theme = "dark";

    public ThemeService(IJSRuntime js)
    {
        _js = js;
    }

    public string Current => _theme;

    public bool IsDark => _theme == "dark";

    public async Task InitializeAsync()
    {
        try
        {
            var saved = await _js.InvokeAsync<string?>("localStorage.getItem", "kaimo_theme");
            if (saved is "dark" or "light")
            {
                _theme = saved;
            }
        }
        catch
        {
            // JS Interop evtl. noch nicht verfügbar
        }
    }

    public async Task ToggleAsync()
    {
        _theme = _theme == "dark" ? "light" : "dark";
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", "kaimo_theme", _theme);
            await _js.InvokeVoidAsync("eval", $"document.documentElement.setAttribute('data-theme','{_theme}')");
        }
        catch
        {
            // Fallback: bleibt im aktuellen State
        }
    }
}