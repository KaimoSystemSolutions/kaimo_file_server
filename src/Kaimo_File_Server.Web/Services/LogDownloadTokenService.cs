using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Kaimo_File_Server.Core.Logging;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Creates short-lived, single-use capability URLs for log downloads after the Blazor
/// circuit authorized the user (same pattern as <see cref="BackupDownloadTokenService"/>).
/// Logs contain user names and client addresses, so the token is bound to the requesting
/// user (the controller re-checks that user's permission) and is rejected on replay, e.g.
/// from a proxy log or browser history.
/// </summary>
public sealed class LogDownloadTokenService
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    public LogDownloadTokenService(IDataProtectionProvider dataProtectionProvider, TimeProvider? time = null)
    {
        // v2: the payload is bound to a user and a nonce; unbound v1 tokens are no longer accepted.
        _protector = dataProtectionProvider
            .CreateProtector("Kaimo.FileServer.SystemLogs.Download.v2")
            .ToTimeLimitedDataProtector();
        _time = time ?? TimeProvider.System;
    }

    public string Protect(LogArchiveQuery query, Guid userId)
    {
        var search = query.SearchText?.Trim();
        if (search is { Length: > 200 })
            search = search[..200];
        var excludedPrefixes = (query.ExcludedMessagePrefixes ?? [])
            .Select(prefix => prefix.Trim())
            .Where(prefix => prefix.Length > 0)
            .Select(prefix => prefix[..Math.Min(prefix.Length, LogArchiveQueryLimits.MaxExcludedMessagePrefixLength)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(LogArchiveQueryLimits.MaxExcludedMessagePrefixes)
            .ToArray();
        var now = _time.GetUtcNow();
        // A download is always scoped to a single UTC day (the selected one, or
        // today when the viewer shows all days) so it never streams the whole
        // retained archive.
        var utcDate = query.UtcDate ?? DateOnly.FromDateTime(now.UtcDateTime);
        var safeQuery = new LogArchiveQuery(
            query.Sources.Take(20).ToArray(),
            query.MinimumLevel,
            search,
            1000,
            utcDate,
            excludedPrefixes,
            query.Levels?.Distinct().ToArray());
        var expiresAt = now + Lifetime;
        var payload = new Payload(
            safeQuery, userId, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), expiresAt);
        return _protector.Protect(JsonSerializer.Serialize(payload, JsonOptions), expiresAt);
    }

    /// <summary>Validates and consumes the token; a second call with the same token fails.</summary>
    public bool TryConsume(string token, out LogArchiveQuery query, out Guid userId)
    {
        query = null!;
        userId = Guid.Empty;
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token, out _), JsonOptions);
        }
        catch
        {
            return false;
        }
        if (payload?.Query is null || payload.UserId == Guid.Empty || string.IsNullOrEmpty(payload.Nonce))
            return false;

        // The protector checks the expiry against the wall clock; this check uses the
        // injected clock as well, so both agree on when a link stops working.
        var now = _time.GetUtcNow();
        if (payload.ExpiresAt <= now)
            return false;

        foreach (var (nonce, expiresAt) in _consumed)
        {
            if (expiresAt <= now)
                _consumed.TryRemove(nonce, out _);
        }
        // An expired token is rejected above, so a nonce only needs to be remembered
        // until its token expires.
        if (!_consumed.TryAdd(payload.Nonce, payload.ExpiresAt))
            return false;

        query = payload.Query;
        userId = payload.UserId;
        return true;
    }

    private sealed record Payload(LogArchiveQuery Query, Guid UserId, string Nonce, DateTimeOffset ExpiresAt);
}
