using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Short-lived, single-use, process-local ticket store. A Blazor circuit (which knows the signed-in
/// user) issues a ticket; a plain HTTP endpoint (which can stream files or set cookies) consumes it.
/// Tickets carry 256 bits of entropy, resolve exactly once and expire after <see cref="Lifetime"/>,
/// so a link cannot be replayed from browser history or a proxy log.
/// </summary>
/// <remarks>
/// ponytail: in-memory; with several web replicas the issuing and consuming request must hit the
/// same instance (sticky sessions). Move to the database like <see cref="ExternalStorage.CloudAuthorizationTicketStore"/>
/// if that ever stops holding.
/// </remarks>
public abstract class OneTimeTicketStore<TTicket>(TimeSpan lifetime, TimeProvider? time = null)
    where TTicket : class
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>How long an issued ticket stays consumable.</summary>
    public TimeSpan Lifetime { get; } = lifetime;

    public string Issue(TTicket ticket)
    {
        RemoveExpired();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _entries[token] = new Entry(ticket, _time.GetUtcNow().Add(Lifetime));
        return token;
    }

    /// <summary>Resolves and removes the ticket; a second call with the same token fails.</summary>
    public bool TryConsume(string token, out TTicket? ticket)
    {
        ticket = null;
        if (!_entries.TryRemove(token, out var entry) || entry.ExpiresAt <= _time.GetUtcNow())
            return false;
        ticket = entry.Ticket;
        return true;
    }

    private void RemoveExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _entries)
            if (entry.Value.ExpiresAt <= now) _entries.TryRemove(entry.Key, out _);
    }

    private sealed record Entry(TTicket Ticket, DateTimeOffset ExpiresAt);
}
