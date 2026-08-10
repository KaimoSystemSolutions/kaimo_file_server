using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Services;

public sealed record CloudAccessDownloadTicket(Guid ShareId, Guid UserId, string RelativePath, string FileName);

public sealed class CloudAccessDownloadTicketStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public string Issue(CloudAccessDownloadTicket ticket)
    {
        RemoveExpired();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _entries[token] = new Entry(ticket, DateTimeOffset.UtcNow.AddMinutes(5));
        return token;
    }

    public bool TryConsume(string token, out CloudAccessDownloadTicket? ticket)
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

    private sealed record Entry(CloudAccessDownloadTicket Ticket, DateTimeOffset ExpiresAt);
}
