using System.Text.Encodings.Web;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Middleware;
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
        if (connection is null || !string.Equals(connection.ProviderId, "dropbox", StringComparison.OrdinalIgnoreCase))
            return NotFound("Cloud Access connection not found.");
        // The single-use ticket is the authorization proof: it is issued only after
        // the ManageConnections check and is bound to this connection. The web
        // session cookie is honored on the Blazor hub only (not on controllers), so the
        // session is intentionally not required here (see the download endpoint).
        if (!await tickets.IsValidAsync(
                ticket, connectionId, string.Empty, "dropbox-access"))
            return BadRequest("The Cloud Access authorization request is invalid or has expired.");
        if (!dropboxAuthorization.IsConfigured)
            return Redirect(ConnectionsPage(error: R("Web_CloudAccess_Dropbox_NotConfigured")));

        try
        {
            var start = await dropboxAuthorization.StartAsync(connectionId, ticket);
            return Content(RenderPage(start, SecurityHeadersMiddleware.GetNonce(HttpContext)), "text/html; charset=utf-8");
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            // Log the real cause server-side; return only a generic message so
            // internal exception text never leaks into the redirect URL/history.
            logger.LogWarning(exception, "Unable to start Dropbox authorization for connection {ConnectionId}", connectionId);
            return Redirect(ConnectionsPage(error: R("Web_CloudAccess_Dropbox_Failed")));
        }
    }

    // POST with the session secret and pasted code in the body (see Connect).
    [HttpPost("submit")]
    [IgnoreAntiforgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Submit([FromForm] string session, [FromForm] string code)
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

    private static string RenderPage(DropboxAuthorizationStart start, string cspNonce)
    {
        var html = HtmlEncoder.Default;
        var open = html.Encode(R("Web_CloudAccess_Dropbox_Open"));
        var codeLabel = html.Encode(R("Web_CloudAccess_Dropbox_CodeLabel"));
        var submit = html.Encode(R("Web_CloudAccess_Dropbox_Submit"));
        var waiting = JsonSerializer.Serialize(R("Web_CloudAccess_Dropbox_Waiting"));
        var failed = JsonSerializer.Serialize(R("Web_CloudAccess_Dropbox_Failed"));
        var codeRequired = JsonSerializer.Serialize(R("Web_CloudAccess_Dropbox_CodeRequired"));
        var submitUrl = JsonSerializer.Serialize("/api/cloud-access/dropbox/submit");
        var sessionId = JsonSerializer.Serialize(start.SessionId);
        var body = $$$"""
            <div class="auth-actions"><a class="btn-secondary" href="{{{html.Encode(start.AuthorizeUrl)}}}" target="_blank" rel="noopener noreferrer">{{{open}}}</a></div>
            <div class="form-group auth-form"><label for="code">{{{codeLabel}}}</label><input id="code" autocomplete="off" spellcheck="false" /></div>
            <div class="auth-actions"><button class="btn-primary" id="submit" type="button">{{{submit}}}</button>
            <p id="status" role="status"></p></div>
            """;
        var script = $$$"""
            const url={{{submitUrl}}},session={{{sessionId}}},s=document.getElementById('status'),b=document.getElementById('submit'),i=document.getElementById('code'),f={{{failed}}},w={{{waiting}}},req={{{codeRequired}}};
            b.addEventListener('click',async()=>{const code=i.value.trim();if(!code){s.textContent=req;s.className='error-banner';return}b.disabled=true;s.className='';s.textContent=w;try{const body=new URLSearchParams({session:session,code:code});const r=await fetch(url,{method:'POST',cache:'no-store',credentials:'same-origin',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:body});const j=await r.json();if(j.state==='complete'){location.replace(j.redirect);return}s.textContent=j.message||f;s.className='error-banner';b.disabled=false}catch(e){s.textContent=f;s.className='error-banner';b.disabled=false}});
            i.addEventListener('keydown',e=>{if(e.key==='Enter')b.click()});
            """;
        return CloudAuthorizationPage.Render(
            R("Web_CloudAccess_Dropbox_Title"),
            R("Web_CloudAccess_Dropbox_Instructions"),
            """<svg viewBox="0 0 24 24"><path d="M7 3 1.5 6.5 7 10l5-3.5L7 3z"/><path d="M17 3l-5 3.5L17 10l5.5-3.5L17 3z"/><path d="M1.5 13.5 7 17l5-3.5L6.5 10 1.5 13.5z"/><path d="M17 10l-5 3.5 5 3.5 5.5-3.5L17 10z"/><path d="M7 18.5 12 22l5-3.5L12 15 7 18.5z"/></svg>""",
            "--provider-dropbox",
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
