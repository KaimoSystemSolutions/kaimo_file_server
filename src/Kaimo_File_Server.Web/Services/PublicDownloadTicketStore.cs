using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// A one-time capability token for an anonymous public-share download. Issued by the public
/// landing page only after the link's policy (window) and optional password have been
/// validated on the circuit, so nothing sensitive rides the browser GET. The public download
/// controller resolves it back to the link <see cref="Token"/> and the selected paths, then
/// atomically consumes one access and streams under the link creator's identity.
/// </summary>
public sealed record PublicDownloadTicket(string Token, string[] RelativePaths, string DownloadName, bool Zip);

/// <summary>Short-lived, single-use ticket store for public downloads (mirrors <see cref="FileDownloadTicketStore"/>).</summary>
public sealed class PublicDownloadTicketStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public string Issue(PublicDownloadTicket ticket)
    {
        RemoveExpired();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _entries[token] = new Entry(ticket, DateTimeOffset.UtcNow.AddMinutes(5));
        return token;
    }

    public bool TryConsume(string token, out PublicDownloadTicket? ticket)
    {
        ticket = null;
        if (!_entries.TryRemove(token, out var entry) || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;
        ticket = entry.Ticket;
        return true;
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _entries)
            if (entry.Value.ExpiresAt <= now) _entries.TryRemove(entry.Key, out _);
    }

    private sealed record Entry(PublicDownloadTicket Ticket, DateTimeOffset ExpiresAt);
}
