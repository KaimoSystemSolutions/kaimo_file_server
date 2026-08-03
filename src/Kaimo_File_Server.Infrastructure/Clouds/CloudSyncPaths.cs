using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Infrastructure.Clouds;

/// <summary>
/// Canonical path and OAuth-state helpers shared by every cloud provider.
/// Keeping these rules central avoids provider-specific path-key behavior.
/// </summary>
public static class CloudSyncPaths
{
    /// <summary>
    /// Canonical form for a folder path used as a CloudSettings.Folders key:
    /// forward slashes, no leading/trailing slash, "" = share root.
    /// </summary>
    public static string Normalize(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? ""
            : path.Replace('\\', '/').Trim('/');

    /// <summary>Returns empty settings for missing persistence data or deserializes existing data.</summary>
    public static CloudSettings ParseSettings(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new CloudSettings(new Dictionary<string, SyncedFolder>())
            : CloudSettings.Deserialize(json);

    /// <summary>True if <paramref name="ancestor"/> is <paramref name="candidate"/> itself,
    /// or a directory that contains it. "" (share root) is an ancestor of everything.</summary>
    public static bool IsSameOrAncestor(string ancestor, string candidate)
    {
        if (ancestor.Length == 0) return true;
        if (string.Equals(ancestor, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the key of the first already-synced folder that conflicts with
    /// <paramref name="path"/> — either because it's the same folder, an
    /// ancestor of it, or a descendant of it. Null if no conflict.
    /// </summary>
    public static string? FindConflict(CloudSettings settings, string path)
    {
        foreach (var existing in settings.Folders.Keys)
        {
            if (IsSameOrAncestor(existing, path) || IsSameOrAncestor(path, existing))
                return existing;
        }
        return null;
    }

    private sealed record StatePayload(Guid ShareId, string Path, string? AuthorizationTicket);

    /// <summary>
    /// Encodes the authorization context as URL-safe Base64. Authenticity is
    /// provided separately by the short-lived ticket store, not by this payload.
    /// </summary>
    public static string EncodeState(Guid shareId, string path, string? authorizationTicket = null)
    {
        var json = JsonSerializer.Serialize(new StatePayload(shareId, path, authorizationTicket));
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Decodes a provider callback state without leaking malformed-input
    /// exceptions into controller endpoints.
    /// </summary>
    public static (Guid ShareId, string Path, string? AuthorizationTicket)? DecodeState(string state)
    {
        try
        {
            var padded = state.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var payload = JsonSerializer.Deserialize<StatePayload>(json);

            return payload is null
                ? null
                : (payload.ShareId, payload.Path, payload.AuthorizationTicket);
        }
        catch
        {
            return null;
        }
    }
}
