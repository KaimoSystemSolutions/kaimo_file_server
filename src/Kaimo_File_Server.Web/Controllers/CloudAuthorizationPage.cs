using System.Globalization;
using System.Text.Encodings.Web;
using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Controllers;

/// <summary>
/// Shared shell for the standalone cloud-provider authorization pages (OneDrive device
/// code, Dropbox code paste). These pages are served by controllers, not Blazor, so the
/// shell pulls in the global app.css tokens and themeInit.js to follow the user's stored
/// theme and accent; each provider only supplies its content and its script.
/// </summary>
internal static class CloudAuthorizationPage
{
    /// <summary>External Storage → Connections tab, where every authorization flow starts and ends.</summary>
    public const string ConnectionsUrl = "/external-storage?tab=connections";

    /// <summary>Renders the page.</summary>
    /// <param name="title">Plain-text title (encoded here).</param>
    /// <param name="instructions">Plain-text instructions (encoded here).</param>
    /// <param name="iconSvg">Trusted inline SVG of the provider icon (24×24, stroke-based).</param>
    /// <param name="iconColorVar">Provider identification token, e.g. <c>--provider-onedrive</c>.</param>
    /// <param name="bodyHtml">Trusted, already-encoded provider content placed above the cancel button.</param>
    /// <param name="script">Trusted provider script; runs under the CSP nonce.</param>
    public static string Render(
        string title, string instructions, string iconSvg, string iconColorVar,
        string bodyHtml, string script, string cspNonce)
    {
        var html = HtmlEncoder.Default;
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var encodedTitle = html.Encode(title);
        var cancel = html.Encode(Resources.ResourceManager.GetString("Web_Button_Cancel") ?? "Cancel");
        return $$$"""
            <!doctype html><html lang="{{{language}}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{{encodedTitle}}} - Kaimo Files</title><link rel="icon" type="image/svg+xml" href="/svg/favicon.svg">
            <link rel="stylesheet" href="/css/app.css?v=2"><script src="/js/themeInit.js"></script><style>
            body{display:grid;place-items:center;padding:var(--space-4)}
            .auth-card{width:min(480px,100%);padding:var(--space-6);border:1px solid var(--border);border-radius:var(--radius-modal);background:var(--bg-secondary);box-shadow:var(--shadow-lg);text-align:center}
            .auth-icon{display:inline-flex;align-items:center;justify-content:center;width:44px;height:44px;margin-bottom:var(--space-4);border-radius:var(--radius-surface);background:var(--bg-tertiary);color:var({{{iconColorVar}}})}
            .auth-icon svg{width:24px;height:24px}
            .auth-card svg{fill:none;stroke:currentColor;stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round}
            h1{margin-bottom:var(--space-2);color:var(--text-primary);font-size:20px;font-weight:700;letter-spacing:-.02em}
            .auth-text{color:var(--text-secondary);font-size:13px;line-height:1.55}
            .auth-code-row{display:flex;gap:var(--space-2);margin:var(--space-5) 0}
            .auth-code{flex:1;min-width:0;padding:var(--space-3);border:1px solid var(--border);border-radius:var(--radius-control);background:var(--bg-primary);color:var(--text-primary);font:700 26px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:.12em;user-select:all;overflow-wrap:anywhere}
            .auth-copy{display:inline-flex;align-items:center;gap:var(--space-2)}
            .auth-copy svg{width:16px;height:16px;stroke-width:2}
            .auth-form{margin-top:var(--space-5);text-align:left}
            .auth-form input{width:100%;font-family:ui-monospace,SFMono-Regular,Consolas,monospace}
            .auth-actions{display:flex;flex-direction:column;gap:var(--space-2);margin-top:var(--space-5)}
            .auth-actions a,.auth-cancel{display:block;text-decoration:none;text-align:center}
            .auth-cancel{margin-top:var(--space-2)}
            #status{margin:var(--space-2) 0;color:var(--text-tertiary);font-size:12.5px}
            #status:empty{display:none}
            #status.error-banner{color:var(--danger);font-size:13px;text-align:left}
            </style></head><body><main class="auth-card">
            <div class="auth-icon" aria-hidden="true">{{{iconSvg}}}</div>
            <h1>{{{encodedTitle}}}</h1><p class="auth-text">{{{html.Encode(instructions)}}}</p>
            {{{bodyHtml}}}
            <a class="btn-secondary auth-cancel" href="{{{ConnectionsUrl}}}">{{{cancel}}}</a>
            </main><script nonce="{{{cspNonce}}}">
            {{{script}}}
            </script></body></html>
            """;
    }
}
