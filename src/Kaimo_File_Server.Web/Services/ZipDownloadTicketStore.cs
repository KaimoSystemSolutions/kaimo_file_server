using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// A one-time capability token for streaming a set of share items to the browser as a ZIP.
/// Issued by the (authenticated) file browser after resolving the signed-in user; the
/// download controller resolves it back to the user + paths and re-checks access through
/// <c>IFileService</c> per entry before streaming. Mirrors <see cref="FileDownloadTicket"/>.
/// </summary>
public sealed record ZipDownloadTicket(Guid ShareId, Guid UserId, string[] RelativePaths, string ArchiveName);

/// <summary>Short-lived, single-use ticket store for ZIP downloads (mirrors <see cref="FileDownloadTicketStore"/>).</summary>
public sealed class ZipDownloadTicketStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public string Issue(ZipDownloadTicket ticket)
    {
        RemoveExpired();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _entries[token] = new Entry(ticket, DateTimeOffset.UtcNow.AddMinutes(5));
        return token;
    }

    public bool TryConsume(string token, out ZipDownloadTicket? ticket)
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

    private sealed record Entry(ZipDownloadTicket Ticket, DateTimeOffset ExpiresAt);
}
