using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Creates short-lived, single-use capability tokens for backup downloads after
/// the Blazor circuit authorized the user (mirrors <see cref="LogDownloadTokenService"/>).
/// A backup contains every password hash and protected credential, so the token
/// is bound to the requesting user (the controller re-checks that user's
/// permission) and is rejected on replay, e.g. from a proxy or browser history.
/// </summary>
public sealed class BackupDownloadTokenService
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    public BackupDownloadTokenService(IDataProtectionProvider dataProtectionProvider, TimeProvider? time = null)
    {
        _protector = dataProtectionProvider
            .CreateProtector("Kaimo.FileServer.DatabaseBackup.Download.v2")
            .ToTimeLimitedDataProtector();
        _time = time ?? TimeProvider.System;
    }

    public string Protect(string fileName, Guid userId)
    {
        var payload = new Payload(fileName, userId, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        return _protector.Protect(JsonSerializer.Serialize(payload), _time.GetUtcNow() + Lifetime);
    }

    /// <summary>Validates and consumes the token; a second call with the same token fails.</summary>
    public bool TryConsume(string token, out string fileName, out Guid userId)
    {
        fileName = string.Empty;
        userId = Guid.Empty;
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token, out _));
        }
        catch
        {
            return false;
        }
        if (payload is null || string.IsNullOrWhiteSpace(payload.FileName) || payload.UserId == Guid.Empty)
            return false;

        var now = _time.GetUtcNow();
        foreach (var (nonce, expiresAt) in _consumed)
        {
            if (expiresAt <= now)
                _consumed.TryRemove(nonce, out _);
        }
        // The protector already rejects expired tokens, so a nonce only needs to
        // be remembered for the token lifetime.
        if (!_consumed.TryAdd(payload.Nonce, now + Lifetime))
            return false;

        fileName = payload.FileName;
        userId = payload.UserId;
        return true;
    }

    private sealed record Payload(string FileName, Guid UserId, string Nonce);
}
