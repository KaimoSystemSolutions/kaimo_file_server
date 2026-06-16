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

        try
        {
            var culture = new CultureInfo(lang);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Invalid culture code in DB — fall back to default
            var fallback = new CultureInfo("de");
            CultureInfo.CurrentCulture = fallback;
            CultureInfo.CurrentUICulture = fallback;
        }

        await _next(context);
    }
}