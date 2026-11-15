namespace Kaimo_File_Server.Web.Services.Auth;

/// <summary>A freshly issued session token waiting to be written into the session cookie.</summary>
public sealed record WebSessionTicket(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Hands a session token from the Blazor circuit (which cannot set cookies) to the
/// <c>/auth/session</c> endpoint (which can). The circuit issues a ticket, the page posts it
/// back with a same-origin fetch, and the endpoint sets the HttpOnly cookie — the token itself
/// never reaches script or a URL. Single-use and short-lived (see <see cref="OneTimeTicketStore{TTicket}"/>).
/// </summary>
public sealed class WebSessionTicketStore() : OneTimeTicketStore<WebSessionTicket>(TimeSpan.FromSeconds(30));
