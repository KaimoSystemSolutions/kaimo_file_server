using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Services;

/// <summary>A freshly issued session token waiting to be written into the session cookie.</summary>
public sealed record WebSessionTicket(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Hands a session token from the Blazor circuit (which cannot set cookies) to the
/// <c>/auth/session</c> endpoint (which can). The circuit issues a ticket, the page posts it
/// back with a same-origin fetch, and the endpoint sets the HttpOnly cookie — the token itself
/// never reaches script or a URL. Single-use and short-lived (mirrors <see cref="FileDownloadTicketStore"/>).
/// </summary>
public sealed class WebSessionTicketStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public string Issue(WebSessionTicket ticket)
    {
        RemoveExpired();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _entries[id] = new Entry(ticket, DateTimeOffset.UtcNow.Add(Lifetime));
        return id;
    }

    public bool TryConsume(string id, out WebSessionTicket? ticket)
    {
        ticket = null;
        if (!_entries.TryRemove(id, out var entry) || entry.ExpiresAt <= DateTimeOffset.UtcNow)
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

    private sealed record Entry(WebSessionTicket Ticket, DateTimeOffset ExpiresAt);
}
