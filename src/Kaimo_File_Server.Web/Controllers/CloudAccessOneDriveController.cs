using System.Text.Encodings.Web;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Middleware;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Web.Controllers;

[ApiController]
[Route("api/cloud-access/onedrive")]
public sealed class CloudAccessOneDriveController(
    ICloudAccessRepository repository,
    IStorageConnectionRepository connections,
    ICredentialVault credentialVault,
    ICloudAuthorizationTicketStore tickets,
    IOneDriveDeviceAuthorizationService deviceAuthorization,
    OneDriveStorageConnectionFactory oneDriveConnections,
    IStorageConnectionProviderCatalog storageProviders,
    CloudAccessDownloadTicketStore downloadTickets,
    IUserContextFactory userContextFactory,
    CloudAccessAuthorizationService authorization,
    ILogger<CloudAccessOneDriveController> logger) : ControllerBase
{
    [HttpGet("~/api/cloud-access/download")]
    public async Task<IActionResult> Download(string ticket)
    {
        if (!downloadTickets.TryConsume(ticket, out var download) || download is null)
            return BadRequest("The download link is invalid or has expired.");
        var share = await repository.GetShareAsync(download.ShareId);
        var actor = await userContextFactory.CreateByUserIdAsync(download.UserId);
        if (share is null || actor is null || !await authorization.CanAccessAsync(actor, share))
            return Forbid();
        var record = await connections.GetAsync(share.ConnectionId);
        if (record?.State != StorageConnectionState.Ready)
            return NotFound();
        if (!ShareRelativePath.TryNormalizeStrict(download.RelativePath, out var relative, allowRoot: false))
            return BadRequest("The download path is invalid.");

        await using var session = await storageProviders.GetRequired(record.ProviderId)
            .OpenSessionAsync(record, HttpContext.RequestAborted);
        var remoteFiles = session.RemoteFiles;
        if (remoteFiles is null) return NotFound();
        Stream content;
        try
        {
            content = await remoteFiles.OpenReadAsync(
                ShareRelativePath.Combine(share.RemoteRootPath, relative), HttpContext.RequestAborted);
        }
        catch (RemoteStorageAccessDeniedException)
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: R("Web_ExternalStorage_RemoteReadDenied"));
        }
        await using (content)
        {
            var contentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = download.FileName
            };
            Response.Headers.ContentDisposition = contentDisposition.ToString();
            Response.ContentType = "application/octet-stream";
            Response.Headers.CacheControl = "no-store";
            await content.CopyToAsync(Response.Body, HttpContext.RequestAborted);
        }
        return new EmptyResult();
    }

    // POST, not GET: the single-use ticket is a secret and travels in the form
    // body so it never lands in the URL (browser history, proxy/access logs).
    // The web session cookie is intentionally not honored on controllers, so the
    // unguessable ticket in the body is itself the anti-CSRF proof; antiforgery
    // token validation (which needs the session) is therefore not applicable.
    [HttpPost("connect")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Connect([FromForm] Guid connectionId, [FromForm] string ticket)
    {
        var connection = await connections.GetAsync(connectionId);
        if (connection is null || !string.Equals(connection.ProviderId, "onedrive", StringComparison.OrdinalIgnoreCase))
            return NotFound("Cloud Access connection not found.");
        // The single-use ticket is the authorization proof: it is issued only after
        // the ManageConnections check and is bound to this connection. The web
        // session cookie is honored on the Blazor hub only (not on controllers), so the
        // session is intentionally not required here (see the download endpoint).
        if (!await tickets.IsValidAsync(
                ticket, connectionId, string.Empty, "onedrive-access"))
            return BadRequest("The Cloud Access authorization request is invalid or has expired.");
        try
        {
            var authorization = await deviceAuthorization.StartAsync(connectionId, string.Empty, ticket);
            return Content(RenderPage(authorization, SecurityHeadersMiddleware.GetNonce(HttpContext)), "text/html; charset=utf-8");
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            // Log the real cause server-side; return only a generic message so
            // internal exception text never leaks into the redirect URL/history.
            logger.LogWarning(exception, "Unable to start OneDrive authorization for Cloud Access connection {ConnectionId}", connectionId);
            return Redirect(ConnectionsPage(error: R("Web_CloudSync_Device_Failed")));
        }
    }

    // POST with the session secret in the body (see Connect). The browser polls
    // this endpoint, so it must not be a cacheable, prefetchable GET.
    [HttpPost("device-status")]
    [IgnoreAntiforgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> DeviceStatus([FromForm] string session)
    {
        var result = await deviceAuthorization.PollAsync(session);
        if (result.State == OneDriveDevicePollState.Pending)
            return Ok(new { state = "pending", retryAfterSeconds = result.RetryAfterSeconds });
        if (result.State == OneDriveDevicePollState.Failed)
            return Ok(new { state = "failed", message = result.ErrorMessage });
        var record = await connections.GetAsync(result.ShareId);
        if (record is null)
            return Ok(new { state = "failed", message = R("Web_CloudAccess_ConnectionMissing") });
        if (result.AuthorizationTicket is null || result.RefreshToken is null || result.Scope is null
            || !await tickets.TryConsumeAsync(
                result.AuthorizationTicket, result.ShareId, string.Empty, "onedrive-access"))
            return Ok(new { state = "failed", message = R("Web_CloudSync_Device_Expired") });

        var credentials = new Dictionary<string, string>
        {
            ["refreshToken"] = result.RefreshToken,
            ["scope"] = result.Scope
        };
        try
        {
            await using var connection = oneDriveConnections.CreatePending(credentials);
            var account = await connection.GetAccountInfoAsync();
            await connections.UpdateRuntimeAsync(
                record.Id,
                credentialVault.ProtectConnectionCredentials(record, credentials),
                account?.DisplayName,
                account?.Email,
                StorageConnectionState.Ready,
                null);
            return Ok(new { state = "complete", redirect = ConnectionsPage(connected: record.Id) });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "OneDrive verification failed for Cloud Access connection {ConnectionId}", record.Id);
            await connections.UpdateRuntimeAsync(
                record.Id,
                credentialVault.ProtectConnectionCredentials(record, credentials),
                null, null,
                StorageConnectionState.Degraded,
                R("Web_CloudSync_Device_Failed"));
            return Ok(new { state = "failed", message = R("Web_CloudSync_Device_Failed") });
        }
    }

    private static string RenderPage(OneDriveDeviceAuthorization authorization, string cspNonce)
    {
        var html = HtmlEncoder.Default;
        var openMicrosoft = html.Encode(R("Web_CloudSync_Device_OpenMicrosoft"));
        var waiting = html.Encode(R("Web_CloudSync_Device_Waiting"));
        var copy = html.Encode(R("Web_CloudSync_Device_CopyCode"));
        var copiedJs = JsonSerializer.Serialize(R("Web_CloudSync_Device_CodeCopied"));
        var copyJs = JsonSerializer.Serialize(R("Web_CloudSync_Device_CopyCode"));
        var failed = JsonSerializer.Serialize(R("Web_CloudSync_Device_Failed"));
        var statusUrl = JsonSerializer.Serialize("/api/cloud-access/onedrive/device-status");
        var sessionId = JsonSerializer.Serialize(authorization.SessionId);
        var body = $$$"""
            <div class="auth-code-row"><code class="auth-code" id="code">{{{html.Encode(authorization.UserCode)}}}</code>
            <button class="btn-secondary auth-copy" id="copy" type="button"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg><span>{{{copy}}}</span></button></div>
            <div class="auth-actions"><a class="btn-primary" href="{{{html.Encode(authorization.VerificationUri)}}}" target="_blank" rel="noopener noreferrer">{{{openMicrosoft}}}</a>
            <p id="status" role="status">{{{waiting}}}</p></div>
            """;
        var script = $$$"""
            const u={{{statusUrl}}},session={{{sessionId}}},s=document.getElementById('status'),f={{{failed}}};async function p(){try{const body=new URLSearchParams({session:session});const r=await fetch(u,{method:'POST',cache:'no-store',credentials:'same-origin',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:body}),j=await r.json();if(j.state==='complete'){location.replace(j.redirect);return}if(j.state==='failed'){s.textContent=j.message||f;s.className='error-banner';return}setTimeout(p,Math.max(1,j.retryAfterSeconds||{{{authorization.PollIntervalSeconds}}})*1000)}catch(e){s.textContent=f;s.className='error-banner'}}setTimeout(p,{{{authorization.PollIntervalSeconds}}}*1000)
            const c=document.getElementById('code'),b=document.getElementById('copy'),l=b.querySelector('span');b.addEventListener('click',async()=>{try{await navigator.clipboard.writeText(c.textContent.trim())}catch(e){getSelection().selectAllChildren(c);return}l.textContent={{{copiedJs}}};setTimeout(()=>l.textContent={{{copyJs}}},2000)})
            """;
        return CloudAuthorizationPage.Render(
            R("Web_CloudSync_Device_Title"),
            R("Web_CloudSync_Device_Instructions"),
            """<svg viewBox="0 0 24 24"><path d="M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z"/></svg>""",
            "--provider-onedrive",
            body, script, cspNonce);
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    /// <summary>Builds a return URL to the External Storage → Connections tab.</summary>
    private static string ConnectionsPage(string? error = null, Guid? connected = null)
    {
        var url = CloudAuthorizationPage.ConnectionsUrl;
        if (connected is Guid id)
            url += $"&connected={id}";
        if (!string.IsNullOrEmpty(error))
            url += $"&error={Uri.EscapeDataString(error)}";
        return url;
    }
}
