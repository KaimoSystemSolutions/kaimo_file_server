namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Circuit-scoped client address and request host. Interactive Blazor code has no reliable
/// <c>HttpContext</c>, so <c>App.razor</c> (statically rendered per request, after
/// the forwarded-headers middleware) hands both to <c>Routes</c>, which
/// stores it here. Component parameters are server-protected, so the browser
/// cannot alter the value.
/// </summary>
public sealed class ClientConnectionInfo
{
    public string? RemoteAddress { get; set; }

    /// <summary>Host name the page was requested under (no port); used by the access restriction rules.</summary>
    public string? Host { get; set; }
}
