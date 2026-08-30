using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// A one-time capability token for streaming a local share file to the browser.
/// The Blazor circuit (which already knows the signed-in user) issues a ticket;
/// the download controller resolves it back to the user + share-relative path and
/// re-checks access through <c>IFileService</c> before streaming.
/// </summary>
public sealed record FileDownloadTicket(Guid ShareId, Guid UserId, string RelativePath, string FileName);

/// <summary>
/// Short-lived, single-use ticket store for file downloads (mirrors
/// <see cref="CloudAccessDownloadTicketStore"/>). Tickets are consumed on first use
/// and expire after a few minutes so a link cannot be replayed indefinitely.
/// </summary>
public sealed class FileDownloadTicketStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public string Issue(FileDownloadTicket ticket)
    {
        RemoveExpired();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _entries[token] = new Entry(ticket, DateTimeOffset.UtcNow.AddMinutes(5));
        return token;
    }

    public bool TryConsume(string token, out FileDownloadTicket? ticket)
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

    private sealed record Entry(FileDownloadTicket Ticket, DateTimeOffset ExpiresAt);
}
