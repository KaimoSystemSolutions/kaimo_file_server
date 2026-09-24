namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Circuit-scoped client address. Interactive Blazor code has no reliable
/// <c>HttpContext</c>, so <c>App.razor</c> (statically rendered per request, after
/// the forwarded-headers middleware) hands the address to <c>Routes</c>, which
/// stores it here. Component parameters are server-protected, so the browser
/// cannot alter the value.
/// </summary>
public sealed class ClientConnectionInfo
{
    public string? RemoteAddress { get; set; }
}
