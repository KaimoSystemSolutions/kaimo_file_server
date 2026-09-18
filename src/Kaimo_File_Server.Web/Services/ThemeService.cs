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
    private string _accent = "green";

    /// <summary>
    /// Selectable accent colors. "green" is the default (no CSS override block);
    /// the others have per-theme token blocks in app.css keyed on data-accent.
    /// </summary>
    public static readonly string[] Accents =
        { "green", "magenta", "orange", "red", "blue", "yellow", "turquoise" };

    public ThemeService(IJSRuntime js)
    {
        _js = js;
    }

    public string Current => _theme;

    public bool IsDark => _theme == "dark";

    public string Accent => _accent;

    public async Task InitializeAsync()
    {
        try
        {
            var saved = await _js.InvokeAsync<string?>("localStorage.getItem", "kaimo_theme");
            if (saved is "dark" or "light")
            {
                _theme = saved;
            }

            var accent = await _js.InvokeAsync<string?>("localStorage.getItem", "kaimo_accent");
            if (accent is not null && Array.IndexOf(Accents, accent) >= 0)
            {
                _accent = accent;
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

    public async Task SetAccentAsync(string accent)
    {
        if (Array.IndexOf(Accents, accent) < 0)
            return;

        _accent = accent;
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", "kaimo_accent", _accent);
            await _js.InvokeVoidAsync("eval", $"document.documentElement.setAttribute('data-accent','{_accent}')");
        }
        catch
        {
            // Fallback: stay in the current state
        }
    }
}