using System.Globalization;
using Kaimo_File_Server.Infrastructure.Configuration;

namespace Kaimo_File_Server.Web.Middleware;

/// <summary>
/// Reads "app.language" from IConfigRepository and sets
/// CultureInfo.CurrentCulture / CurrentUICulture for every request.
///
/// This makes Resources.resx pick the correct language globally.
///
/// Register in Program.cs:
///   app.UseRequestLocalization();         // built-in (optional)
///   app.UseMiddleware&lt;ConfigLocalizationMiddleware&gt;();
/// </summary>
public class ConfigLocalizationMiddleware
{
    private readonly RequestDelegate _next;

    public ConfigLocalizationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IConfigRepository config)
    {
        var lang = await config.GetStringAsync("app.language", "de");
        var culture = ResolveCulture(lang);

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        await _next(context);
    }

    /// <summary>
    /// Sets the AppDomain-wide default culture. Threads that never pass through
    /// the request pipeline — Blazor interactive circuit continuations resuming
    /// on fresh thread-pool threads, background jobs — inherit this instead of
    /// the OS default, so resources resolve in the configured language rather
    /// than intermittently falling back to English.
    ///
    /// Called once at startup and again from SettingsViewModel when the
    /// language changes.
    /// </summary>
    public static void ApplyDefaultCulture(string lang)
    {
        var culture = ResolveCulture(lang);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private static CultureInfo ResolveCulture(string lang)
    {
        try
        {
            return new CultureInfo(lang);
        }
        catch (CultureNotFoundException)
        {
            // Invalid culture code in DB — fall back to default
            return new CultureInfo("de");
        }
    }
}