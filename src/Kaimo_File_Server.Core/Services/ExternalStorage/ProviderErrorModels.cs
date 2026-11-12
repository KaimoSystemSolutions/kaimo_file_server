using System.Net;
using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Core.Services.ExternalStorage;

/// <summary>Stable, non-sensitive categories used for connection health and provider diagnostics.</summary>
public enum ProviderErrorCategory
{
    Authentication,
    Authorization,
    Throttled,
    Transient,
    InvalidResponse,
    Unknown
}

/// <summary>A provider failure stripped of response bodies, tokens, codes, and other authorization material.</summary>
/// <param name="Detail">
/// A redacted, length-bounded framing message kept only when the provider
/// returned no structured error code (a transport/route-level failure). It turns
/// an otherwise opaque <c>http_NNN</c> code into an actionable message and never
/// carries a structured error body, so no authorization material is exposed.
/// </param>
public sealed record SanitizedProviderError(
    string Code,
    ProviderErrorCategory Category,
    HttpStatusCode? StatusCode,
    string? Detail = null);
