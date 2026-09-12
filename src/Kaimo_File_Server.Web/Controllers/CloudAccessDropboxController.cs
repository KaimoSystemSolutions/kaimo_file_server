using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kaimo_File_Server.Web.Controllers;

/// <summary>
/// Hosts the redirect-free Dropbox authorization page. The browser only displays
/// a link to Dropbox and accepts the authorization code the user pastes back;
/// every token request originates from this server, and no inbound internet
/// endpoint or application secret is required.
/// </summary>
[ApiController]
[Route("api/cloud-access/dropbox")]
public sealed class CloudAccessDropboxController(
    IStorageConnectionRepository connections,
    ICredentialVault credentialVault,
    ICloudAuthorizationTicketStore tickets,
    IDropboxAuthorizationService dropboxAuthorization,
    DropboxIdentityConfiguration identity,
    IHttpClientFactory httpClientFactory,
    ILogger<CloudAccessDropboxController> logger) : ControllerBase
{
    [HttpGet("connect")]
    public async Task<IActionResult> Connect(Guid connectionId, string ticket)
    {
        var connection = await connections.GetAsync(connectionId);
        if (connection is null || !string.Equals(connection.ProviderId, "dropbox", StringComparison.OrdinalIgnoreCase))
            return NotFound("Cloud Access connection not found.");
        // The single-use ticket is the authorization proof: it is issued only after
        // the ManageConnections check and is bound to this connection. The Blazor
        // localStorage JWT is not present on this full-page navigation, so the
        // session is intentionally not required here (see the download endpoint).
        if (!await tickets.IsValidAsync(
                ticket, connectionId, string.Empty, "dropbox-access"))
            return BadRequest("The Cloud Access authorization request is invalid or has expired.");
        if (!dropboxAuthorization.IsConfigured)
            return Redirect(ConnectionsPage(error: R("Web_CloudAccess_Dropbox_NotConfigured")));

        try
        {
            var start = await dropboxAuthorization.StartAsync(connectionId, ticket);
            return Content(RenderPage(start), "text/html; charset=utf-8");
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            logger.LogWarning(exception, "Unable to start Dropbox authorization for connection {ConnectionId}", connectionId);
            return Redirect(ConnectionsPage(error: exception.Message));
        }
    }

    [HttpGet("submit")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Submit(string session, string code)
    {
        var result = await dropboxAuthorization.CompleteAsync(session, code);
        if (result.State == DropboxAuthorizationState.Failed)
            return Ok(new { state = "failed", message = result.ErrorMessage });

        var record = await connections.GetAsync(result.ConnectionId);
        if (record is null)
            return Ok(new { state = "failed", message = R("Web_CloudAccess_ConnectionMissing") });
        if (result.AuthorizationTicket is null || result.RefreshToken is null || result.Scope is null
            || !await tickets.TryConsumeAsync(
                result.AuthorizationTicket, result.ConnectionId, string.Empty, "dropbox-access"))
            return Ok(new { state = "failed", message = R("Web_CloudAccess_Dropbox_Expired") });

        var credentials = new Dictionary<string, string>
        {
            ["refreshToken"] = result.RefreshToken,
            ["scope"] = result.Scope
        };
        try
        {
            await using var connection = new DropboxConnection(
                credentials, httpClientFactory.CreateClient("CloudAccessDropbox"), identity);
            var account = await connection.GetAccountInfoAsync();
            // Verify the granted token can actually browse (files.metadata.read),
            // not merely read the account (account_info.read). A scope-deficient
            // token would otherwise connect successfully and only fail later, with
            // an opaque provider error, when a remote folder is selected.
            await connection.ListAsync("/");
            await connections.UpdateRuntimeAsync(
                record.Id,
                credentialVault.ProtectConnectionCredentials(record, credentials),
                account?.DisplayName,
                account?.Email,
                StorageConnectionState.Ready,
                null);
            return Ok(new { state = "complete", redirect = ConnectionsPage(connected: record.Id) });
        }
        catch (ProviderRequestException exception)
            when (exception.ErrorCode.Contains("scope", StringComparison.OrdinalIgnoreCase))
        {
            // The account was reachable but the grant is missing a browse scope.
            // Re-authorization (after enabling the scopes on the Dropbox app) is the
            // only remedy, so the connection is flagged accordingly rather than Ready.
            logger.LogWarning(
                exception, "Dropbox connection {ConnectionId} is missing required scopes", record.Id);
            await connections.UpdateRuntimeAsync(
                record.Id,
                credentialVault.ProtectConnectionCredentials(record, credentials),
                null, null,
                StorageConnectionState.NeedsReauthorization,
                R("Web_CloudAccess_Dropbox_MissingScope"));
            return Ok(new { state = "failed", message = R("Web_CloudAccess_Dropbox_MissingScope") });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Dropbox verification failed for connection {ConnectionId}", record.Id);
            await connections.UpdateRuntimeAsync(
                record.Id,
                credentialVault.ProtectConnectionCredentials(record, credentials),
                null, null,
                StorageConnectionState.Degraded,
                R("Web_CloudAccess_Dropbox_Failed"));
            return Ok(new { state = "failed", message = R("Web_CloudAccess_Dropbox_Failed") });
        }
    }

    private static string RenderPage(DropboxAuthorizationStart start)
    {
        var html = HtmlEncoder.Default;
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var title = html.Encode(R("Web_CloudAccess_Dropbox_Title"));
        var instructions = html.Encode(R("Web_CloudAccess_Dropbox_Instructions"));
        var open = html.Encode(R("Web_CloudAccess_Dropbox_Open"));
        var codeLabel = html.Encode(R("Web_CloudAccess_Dropbox_CodeLabel"));
        var submit = html.Encode(R("Web_CloudAccess_Dropbox_Submit"));
        var cancel = html.Encode(R("Web_Button_Cancel"));
        var waiting = JsonSerializer.Serialize(R("Web_CloudAccess_Dropbox_Waiting"));
        var failed = JsonSerializer.Serialize(R("Web_CloudAccess_Dropbox_Failed"));
        var codeRequired = JsonSerializer.Serialize(R("Web_CloudAccess_Dropbox_CodeRequired"));
        var submitUrl = JsonSerializer.Serialize("/api/cloud-access/dropbox/submit");
        var sessionId = JsonSerializer.Serialize(start.SessionId);
        return $$$"""
            <!doctype html><html lang="{{{language}}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{{title}}} - Kaimo Files</title><style>
            :root{color-scheme:light dark;font-family:Inter,system-ui,sans-serif}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#101522;color:#f5f7fb}
            main{width:min(520px,calc(100% - 40px));padding:36px;border:1px solid #34405a;border-radius:18px;background:#192132;text-align:center}p{color:#bac5d9;line-height:1.55}
            a.button{display:inline-block;margin:8px 0 22px;padding:11px 18px;border-radius:9px;background:#0061ff;color:white;text-decoration:none;font-weight:650}
            label{display:block;text-align:left;margin:0 auto;max-width:360px;color:#bac5d9;font-size:.9rem}
            input{width:100%;box-sizing:border-box;margin-top:6px;padding:11px 12px;border-radius:9px;border:1px solid #34405a;background:#0d1320;color:#f5f7fb;font:1rem ui-monospace,monospace}
            button.submit{margin-top:16px;padding:11px 18px;border-radius:9px;border:0;background:#2878d0;color:white;font-weight:650;cursor:pointer}button.submit:disabled{opacity:.6;cursor:default}
            a.cancel{display:block;margin-top:20px;color:#aab6ca}#status{margin-top:18px;font-size:.92rem}#status.error{color:#ff9c9c}
            </style></head><body><main><h1>{{{title}}}</h1><p>{{{instructions}}}</p>
            <a class="button" href="{{{html.Encode(start.AuthorizeUrl)}}}" target="_blank" rel="noopener noreferrer">{{{open}}}</a>
            <label>{{{codeLabel}}}<input id="code" autocomplete="off" spellcheck="false" /></label>
            <button class="submit" id="submit" type="button">{{{submit}}}</button>
            <p id="status"></p><a class="cancel" href="/cloud-access">{{{cancel}}}</a></main><script>
            const url={{{submitUrl}}},session={{{sessionId}}},s=document.getElementById('status'),b=document.getElementById('submit'),i=document.getElementById('code'),f={{{failed}}},w={{{waiting}}},req={{{codeRequired}}};
            b.addEventListener('click',async()=>{const code=i.value.trim();if(!code){s.textContent=req;s.className='error';return}b.disabled=true;s.className='';s.textContent=w;try{const q=url+'?session='+encodeURIComponent(session)+'&code='+encodeURIComponent(code);const r=await fetch(q,{cache:'no-store',credentials:'same-origin'});const j=await r.json();if(j.state==='complete'){location.replace(j.redirect);return}s.textContent=j.message||f;s.className='error';b.disabled=false}catch(e){s.textContent=f;s.className='error';b.disabled=false}});
            i.addEventListener('keydown',e=>{if(e.key==='Enter')b.click()});
            </script></body></html>
            """;
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    /// <summary>Builds a return URL to the External Storage → Connections tab.</summary>
    private static string ConnectionsPage(string? error = null, Guid? connected = null)
    {
        var url = "/external-storage?tab=connections";
        if (connected is Guid id)
            url += $"&connected={id}";
        if (!string.IsNullOrEmpty(error))
            url += $"&error={Uri.EscapeDataString(error)}";
        return url;
    }
}
