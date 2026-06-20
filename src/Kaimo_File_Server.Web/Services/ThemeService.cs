using Microsoft.JSInterop;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Centralizes the theme logic (read/write localStorage, set data-theme).
/// Replaces the duplicated logic in MainLayout and EmptyLayout.
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
            // JS interop may not be available yet
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
            // Fallback: stay in the current state
        }
    }
}