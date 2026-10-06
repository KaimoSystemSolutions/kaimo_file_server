using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kaimo_File_Server.Web.Controllers;

/// <summary>
/// Sets and clears the web session cookie (<see cref="WebSessionCookie"/>). The Blazor circuit
/// verifies the credentials and issues a one-time ticket; the page then posts the ticket here
/// with a same-origin fetch. Deliberately outside <c>/api</c>: the security monitor classifies
/// <c>/api</c> traffic as client-API requests.
///
/// Both endpoints accept only same-origin HTTPS fetches (<see cref="WebSessionCookie.IsSameOriginFetch"/>),
/// which rules out login CSRF and cross-site sign-out. The JSON-only body adds a second barrier:
/// a cross-site HTML form cannot send it, and a cross-site fetch with it needs a CORS preflight
/// this server never grants.
/// </summary>
[ApiController]
[Route("auth")]
[AllowAnonymous]
public sealed class WebSessionController(
    WebSessionTicketStore tickets,
    IConfiguration configuration) : ControllerBase
{
    public sealed record SessionTicketRequest(string Ticket);

    [HttpPost("session")]
    public IActionResult Establish([FromBody] SessionTicketRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        if (!WebSessionCookie.IsSameOriginFetch(Request, WebSessionCookie.ReadAllowedOrigins(configuration)))
            return StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(request.Ticket) || !tickets.TryConsume(request.Ticket, out var ticket))
            return BadRequest();

        WebSessionCookie.Append(Response, ticket!.Token, ticket.ExpiresAt);
        return NoContent();
    }

    /// <summary>
    /// Clears the cookie. The token itself is revoked by the circuit before this call, so a
    /// cookie that survives a failed call is already worthless.
    /// </summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Headers.CacheControl = "no-store";
        if (!WebSessionCookie.IsSameOriginFetch(Request, WebSessionCookie.ReadAllowedOrigins(configuration)))
            return StatusCode(StatusCodes.Status403Forbidden);

        WebSessionCookie.Delete(Response);
        return NoContent();
    }
}
