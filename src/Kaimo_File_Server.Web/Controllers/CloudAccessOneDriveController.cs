using System.Text.Encodings.Web;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using System.Globalization;

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
    IHttpClientFactory httpClientFactory,
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
        if (record?.State != StorageConnectionState.Ready
            || string.IsNullOrWhiteSpace(record.EncryptedCredentialPayload))
            return NotFound();
        if (!ShareRelativePath.TryNormalizeStrict(download.RelativePath, out var relative, allowRoot: false))
            return BadRequest("The download path is invalid.");

        await using var connection = oneDriveConnections.Create(record);
        var contentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = download.FileName
        };
        Response.Headers.ContentDisposition = contentDisposition.ToString();
        Response.ContentType = "application/octet-stream";
        Response.Headers.CacheControl = "no-store";
        await connection.DownloadAsync(
            ShareRelativePath.Combine(share.RemoteRootPath, relative),
            Response.Body,
            HttpContext.RequestAborted);
        return new EmptyResult();
    }

    [HttpGet("connect")]
    public async Task<IActionResult> Connect(Guid connectionId, string ticket)
    {
        var connection = await connections.GetAsync(connectionId);
        if (connection is null || !string.Equals(connection.ProviderId, "onedrive", StringComparison.OrdinalIgnoreCase))
            return NotFound("Cloud Access connection not found.");
        var actorId = await GetActorIdAsync();
        if (actorId is null)
            return Unauthorized();
        if (!await tickets.IsValidAsync(
                ticket, connectionId, string.Empty, "onedrive-access", actorId, connection.DepartmentId))
            return BadRequest("The Cloud Access authorization request is invalid or has expired.");
        try
        {
            var authorization = await deviceAuthorization.StartAsync(connectionId, string.Empty, ticket);
            return Content(RenderPage(authorization), "text/html; charset=utf-8");
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            logger.LogWarning(exception, "Unable to start OneDrive authorization for Cloud Access connection {ConnectionId}", connectionId);
            return Redirect($"/cloud-access?error={Uri.EscapeDataString(exception.Message)}");
        }
    }

    [HttpGet("device-status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> DeviceStatus(string session)
    {
        var result = await deviceAuthorization.PollAsync(session);
        if (result.State == OneDriveDevicePollState.Pending)
            return Ok(new { state = "pending", retryAfterSeconds = result.RetryAfterSeconds });
        if (result.State == OneDriveDevicePollState.Failed)
            return Ok(new { state = "failed", message = result.ErrorMessage });
        var record = await connections.GetAsync(result.ShareId);
        var actorId = await GetActorIdAsync();
        if (record is null)
            return Ok(new { state = "failed", message = R("Web_CloudAccess_ConnectionMissing") });
        if (result.AuthorizationTicket is null || result.RefreshToken is null || result.Scope is null
            || actorId is null
            || !await tickets.TryConsumeAsync(
                result.AuthorizationTicket, result.ShareId, string.Empty, "onedrive-access",
                actorId, record.DepartmentId))
            return Ok(new { state = "failed", message = R("Web_CloudSync_Device_Expired") });

        var credentials = new Dictionary<string, string>
        {
            ["refreshToken"] = result.RefreshToken,
            ["scope"] = result.Scope
        };
        try
        {
            await using var connection = new OneDriveConnection(
                credentials, httpClientFactory.CreateClient("CloudAccessOneDrive"));
            var account = await connection.GetAccountInfoAsync();
            await connections.UpdateRuntimeAsync(
                record.Id,
                credentialVault.ProtectConnectionCredentials(record, credentials),
                account?.DisplayName,
                account?.Email,
                StorageConnectionState.Ready,
                null);
            return Ok(new { state = "complete", redirect = $"/cloud-access?connected={record.Id}" });
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

    private static string RenderPage(OneDriveDeviceAuthorization authorization)
    {
        var html = HtmlEncoder.Default;
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var title = html.Encode(R("Web_CloudSync_Device_Title"));
        var instructions = html.Encode(R("Web_CloudSync_Device_Instructions"));
        var openMicrosoft = html.Encode(R("Web_CloudSync_Device_OpenMicrosoft"));
        var waiting = html.Encode(R("Web_CloudSync_Device_Waiting"));
        var cancel = html.Encode(R("Web_Button_Cancel"));
        var failed = JsonSerializer.Serialize(R("Web_CloudSync_Device_Failed"));
        var statusUrl = JsonSerializer.Serialize(
            "/api/cloud-access/onedrive/device-status?session=" + Uri.EscapeDataString(authorization.SessionId));
        return $$$"""
            <!doctype html><html lang="{{{language}}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{{title}}} - Kaimo Files</title><style>
            :root{color-scheme:light dark;font-family:Inter,system-ui,sans-serif}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#101522;color:#f5f7fb}
            main{width:min(520px,calc(100% - 40px));padding:36px;border:1px solid #34405a;border-radius:18px;background:#192132;text-align:center}p{color:#bac5d9;line-height:1.55}
            code{display:block;margin:26px auto;padding:16px;border-radius:10px;background:#0d1320;color:#8fc8ff;font:700 1.9rem ui-monospace;letter-spacing:.12em;user-select:all}
            a.button{display:inline-block;padding:11px 18px;border-radius:9px;background:#2878d0;color:white;text-decoration:none;font-weight:650}a.cancel{display:block;margin-top:20px;color:#aab6ca}.error{color:#ff9c9c}
            </style></head><body><main><h1>{{{title}}}</h1><p>{{{instructions}}}</p>
            <code>{{{html.Encode(authorization.UserCode)}}}</code><a class="button" href="{{{html.Encode(authorization.VerificationUri)}}}" target="_blank" rel="noopener noreferrer">{{{openMicrosoft}}}</a>
            <p id="status">{{{waiting}}}</p><a class="cancel" href="/cloud-access">{{{cancel}}}</a></main><script>
            const u={{{statusUrl}}},s=document.getElementById('status'),f={{{failed}}};async function p(){try{const r=await fetch(u,{cache:'no-store',credentials:'same-origin'}),j=await r.json();if(j.state==='complete'){location.replace(j.redirect);return}if(j.state==='failed'){s.textContent=j.message||f;s.className='error';return}setTimeout(p,Math.max(1,j.retryAfterSeconds||{{{authorization.PollIntervalSeconds}}})*1000)}catch(e){s.textContent=f;s.className='error'}}setTimeout(p,{{{authorization.PollIntervalSeconds}}}*1000)
            </script></body></html>
            """;
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    private async Task<Guid?> GetActorIdAsync()
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)) return null;
        return (await userContextFactory.CreateByUsernameAsync(username))?.User.Id;
    }
}
