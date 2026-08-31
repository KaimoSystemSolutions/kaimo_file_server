using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Core.Services;

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
public sealed record SanitizedProviderError(
    string Code,
    ProviderErrorCategory Category,
    HttpStatusCode? StatusCode);

/// <summary>
/// Central boundary for provider failures and diagnostic text. It extracts only
/// allow-listed error identifiers and replaces common credential representations.
/// </summary>
public static partial class ProviderErrorSanitizer
{
    private const int MaximumCodeLength = 100;

    /// <summary>Creates a safe provider error without retaining the raw response body.</summary>
    public static SanitizedProviderError FromResponse(HttpStatusCode statusCode, string? responseBody)
    {
        var code = TryReadErrorCode(responseBody) ?? $"http_{(int)statusCode}";
        code = SafeCode(code);
        return new SanitizedProviderError(code, Categorize(code, statusCode), statusCode);
    }

    /// <summary>Redacts common OAuth, authorization, password, and key material from diagnostic text.</summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var redacted = JsonSecretRegex().Replace(value, "$1[REDACTED]$3");
        redacted = FormSecretRegex().Replace(redacted, "$1[REDACTED]");
        redacted = BearerRegex().Replace(redacted, "$1[REDACTED]");
        redacted = JwtRegex().Replace(redacted, "[REDACTED_JWT]");
        return redacted;
    }

    private static string? TryReadErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var code)
                        && code.ValueKind == JsonValueKind.String)
                        return code.GetString();
                    // Dropbox API v2 tags the error kind here, e.g. {".tag":"path"}.
                    if (error.TryGetProperty(".tag", out var tag)
                        && tag.ValueKind == JsonValueKind.String)
                        return tag.GetString();
                }
            }

            // Dropbox summarizes the failure as a stable, non-sensitive category
            // string such as "path/not_found/" or "missing_scope/..".
            if (root.TryGetProperty("error_summary", out var summary)
                && summary.ValueKind == JsonValueKind.String)
                return summary.GetString();
        }
        catch (JsonException)
        {
            // Invalid provider bodies deliberately collapse to the HTTP status.
        }
        return null;
    }

    private static string SafeCode(string value)
    {
        var normalized = new string(value
            .Take(MaximumCodeLength)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'
                ? char.ToLowerInvariant(character)
                : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "provider_error" : normalized;
    }

    private static ProviderErrorCategory Categorize(string code, HttpStatusCode statusCode)
    {
        if (code is "invalid_grant" or "invalid_token" or "interaction_required"
            || statusCode == HttpStatusCode.Unauthorized)
            return ProviderErrorCategory.Authentication;
        if (code is "access_denied" or "authorization_declined"
            || statusCode == HttpStatusCode.Forbidden)
            return ProviderErrorCategory.Authorization;
        if (code is "slow_down" or "temporarily_unavailable"
            || statusCode == HttpStatusCode.TooManyRequests)
            return ProviderErrorCategory.Throttled;
        if ((int)statusCode >= 500)
            return ProviderErrorCategory.Transient;
        if (statusCode == HttpStatusCode.BadGateway)
            return ProviderErrorCategory.InvalidResponse;
        return ProviderErrorCategory.Unknown;
    }

    [GeneratedRegex("(?i)(\\\"(?:access_token|refresh_token|client_secret|device_code|code|assertion|password|private_key)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")", RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex("(?i)((?:access_token|refresh_token|client_secret|device_code|code|assertion|password|private_key)=)[^&\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex FormSecretRegex();

    [GeneratedRegex("(?i)(\\bBearer\\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();
}

/// <summary>Exception that exposes only a sanitized provider code and HTTP status.</summary>
public sealed class ProviderRequestException : HttpRequestException
{
    public ProviderRequestException(string providerId, SanitizedProviderError error)
        : base(
            $"{providerId} request failed with provider code '{error.Code}'.",
            null,
            error.StatusCode)
    {
        ProviderId = providerId;
        ErrorCode = error.Code;
        Category = error.Category;
    }

    public string ProviderId { get; }
    public string ErrorCode { get; }
    public ProviderErrorCategory Category { get; }
}
