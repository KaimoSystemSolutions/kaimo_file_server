using System.Text.Encodings.Web;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kaimo_File_Server.Web.Controllers;

/// <summary>
/// Hosts the redirect-free OneDrive device authorization page. The browser only
/// displays Microsoft's user code; every token request originates from this
/// server, and no inbound internet endpoint or client secret is required.
/// </summary>
[ApiController]
[Route("api/onedrive")]
public sealed class OneDriveOAuthController : ControllerBase
{
    private readonly IShareRepository _shareRepository;
    private readonly ICloudAuthorizationTicketStore _authorizationTickets;
    private readonly IOneDriveDeviceAuthorizationService _deviceAuthorization;

    /// <summary>Creates the endpoint using neutral persistence and authorization services.</summary>
    public OneDriveOAuthController(
        IShareRepository shareRepository,
        ICloudAuthorizationTicketStore authorizationTickets,
        IOneDriveDeviceAuthorizationService deviceAuthorization)
    {
        _shareRepository = shareRepository;
        _authorizationTickets = authorizationTickets;
        _deviceAuthorization = deviceAuthorization;
    }

    /// <summary>
    /// Validates the one-time cloud ticket and folder conflict state, starts a
    /// Microsoft device session, and renders the local authorization page.
    /// </summary>
    [HttpGet("connect")]
    public async Task<IActionResult> Connect(Guid shareId, string? path, string ticket)
    {
        var normalizedPath = CloudSyncPaths.Normalize(path);
        if (!_authorizationTickets.IsValid(ticket, shareId, normalizedPath, "onedrive"))
            return BadRequest("The cloud authorization request is invalid or has expired.");

        var share = await _shareRepository.GetByIdAsync(shareId);
        if (share is null)
            return NotFound("Share not found.");

        var conflict = CloudSyncPaths.FindConflict(share.CloudSettings, normalizedPath);
        if (conflict is not null)
        {
            return BadRequest(conflict == normalizedPath
                ? "This folder is already synced."
                : $"This conflicts with the already-synced folder '{conflict}'.");
        }

        try
        {
            var authorization = await _deviceAuthorization.StartAsync(
                shareId,
                normalizedPath,
                ticket);
            return Content(RenderAuthorizationPage(authorization), "text/html; charset=utf-8");
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            return Redirect($"/sync?syncError={Uri.EscapeDataString(exception.Message)}");
        }
    }

    /// <summary>
    /// Polls the server-side device session. On completion it consumes the
    /// original ticket, rechecks conflicts, and persists the refresh token.
    /// </summary>
    [HttpGet("device-status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> DeviceStatus(string session)
    {
        var result = await _deviceAuthorization.PollAsync(session);
        if (result.State == OneDriveDevicePollState.Pending)
        {
            return Ok(new
            {
                state = "pending",
                retryAfterSeconds = result.RetryAfterSeconds
            });
        }

        if (result.State == OneDriveDevicePollState.Failed)
            return Ok(new { state = "failed", message = result.ErrorMessage });

        if (result.AuthorizationTicket is null
            || result.LocalPath is null
            || result.RefreshToken is null
            || result.Scope is null
            || !_authorizationTickets.TryConsume(
                result.AuthorizationTicket,
                result.ShareId,
                result.LocalPath,
                "onedrive"))
        {
            return Ok(new
            {
                state = "failed",
                message = Text(
                    "Web_CloudSync_Device_Expired",
                    "The cloud authorization request has expired. Please start again.")
            });
        }

        var share = await _shareRepository.GetByIdAsync(result.ShareId);
        if (share is null)
            return Ok(new { state = "failed", message = "Share not found." });

        // The share may have changed while the user was signing in at Microsoft.
        var conflict = CloudSyncPaths.FindConflict(share.CloudSettings, result.LocalPath);
        if (conflict is not null)
        {
            return Ok(new
            {
                state = "failed",
                message = Text(
                    "Web_CloudSync_Error_Conflict",
                    "This folder overlaps an existing cloud sync.")
            });
        }

        share.CloudSettings.Folders[result.LocalPath] = new SyncedFolder(
            "onedrive",
            new Dictionary<string, string>
            {
                ["connectionId"] = Guid.NewGuid().ToString("N"),
                ["refreshToken"] = result.RefreshToken,
                ["scope"] = result.Scope
            })
        {
            RequiresRemoteFolderSelection = true
        };
        await _shareRepository.UpdateAsync(share);

        var redirect = $"/sync?connectedShare={share.Id}" +
                       $"&connectedPath={Uri.EscapeDataString(result.LocalPath)}&remoteFolderRequired=true";
        return Ok(new { state = "complete", redirect });
    }

    /// <summary>
    /// Builds a self-contained, encoded status page. Only the user code and an
    /// opaque session id enter the browser; OAuth tokens stay on the server.
    /// </summary>
    private static string RenderAuthorizationPage(OneDriveDeviceAuthorization authorization)
    {
        var html = HtmlEncoder.Default;
        var statusUrl = "/api/onedrive/device-status?session=" +
                        Uri.EscapeDataString(authorization.SessionId);
        var statusUrlJson = JsonSerializer.Serialize(statusUrl);
        var fallbackErrorJson = JsonSerializer.Serialize(Text(
            "Web_CloudSync_Device_Failed",
            "Microsoft authorization failed. Please return to Cloud Sync and try again."));
        var title = html.Encode(Text("Web_CloudSync_Device_Title", "Connect Microsoft OneDrive"));
        var instructions = html.Encode(Text(
            "Web_CloudSync_Device_Instructions",
            "Open the Microsoft sign-in page, enter this code, and approve access to your OneDrive."));
        var openMicrosoft = html.Encode(Text(
            "Web_CloudSync_Device_OpenMicrosoft",
            "Open Microsoft sign-in"));
        var waiting = html.Encode(Text(
            "Web_CloudSync_Device_Waiting",
            "Waiting for Microsoft authorization…"));
        var cancel = html.Encode(Text("Web_Button_Cancel", "Cancel"));

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <title>{{title}} - Kaimo Files</title>
                <style>
                    :root { color-scheme: light dark; font-family: Inter, system-ui, sans-serif; }
                    body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #101522; color: #f5f7fb; }
                    main { width: min(520px, calc(100% - 40px)); padding: 36px; border: 1px solid #34405a; border-radius: 18px; background: #192132; box-shadow: 0 24px 64px #0006; text-align: center; }
                    h1 { margin: 0 0 12px; font-size: 1.6rem; }
                    p { color: #bac5d9; line-height: 1.55; }
                    code { display: block; margin: 26px auto; padding: 16px; border-radius: 10px; background: #0d1320; color: #8fc8ff; font: 700 1.9rem/1.2 ui-monospace, monospace; letter-spacing: .12em; user-select: all; }
                    a.button { display: inline-block; padding: 11px 18px; border-radius: 9px; background: #2878d0; color: white; text-decoration: none; font-weight: 650; }
                    a.cancel { display: block; margin-top: 20px; color: #aab6ca; }
                    #status { margin-top: 24px; font-size: .92rem; }
                    #status.error { color: #ff9c9c; }
                </style>
            </head>
            <body>
                <main>
                    <h1>{{title}}</h1>
                    <p>{{instructions}}</p>
                    <code>{{html.Encode(authorization.UserCode)}}</code>
                    <a class="button" href="{{html.Encode(authorization.VerificationUri)}}" target="_blank" rel="noopener noreferrer">{{openMicrosoft}}</a>
                    <p id="status">{{waiting}}</p>
                    <a class="cancel" href="/sync">{{cancel}}</a>
                </main>
                <script>
                    const statusUrl = {{statusUrlJson}};
                    const fallbackError = {{fallbackErrorJson}};
                    const statusElement = document.getElementById('status');
                    async function poll() {
                        try {
                            const response = await fetch(statusUrl, { cache: 'no-store', credentials: 'same-origin' });
                            if (!response.ok) throw new Error(fallbackError);
                            const result = await response.json();
                            if (result.state === 'complete') {
                                window.location.replace(result.redirect);
                                return;
                            }
                            if (result.state === 'failed') {
                                statusElement.textContent = result.message || fallbackError;
                                statusElement.classList.add('error');
                                return;
                            }
                            window.setTimeout(poll, Math.max(1, result.retryAfterSeconds || {{authorization.PollIntervalSeconds}}) * 1000);
                        } catch (error) {
                            statusElement.textContent = error.message || fallbackError;
                            statusElement.classList.add('error');
                        }
                    }
                    window.setTimeout(poll, {{authorization.PollIntervalSeconds}} * 1000);
                </script>
            </body>
            </html>
            """;
    }

    /// <summary>Resolves localized UI text with a stable English fallback.</summary>
    private static string Text(string key, string fallback)
        => Resources.ResourceManager.GetString(key) ?? fallback;
}
