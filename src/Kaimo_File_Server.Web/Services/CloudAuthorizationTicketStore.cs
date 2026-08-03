using System.Collections.Concurrent;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// In-memory OAuth hand-off store. Tickets intentionally do not survive a
/// server restart and expire quickly because they only cover one redirect.
/// </summary>
public sealed class CloudAuthorizationTicketStore : ICloudAuthorizationTicketStore
{
    // Microsoft device codes currently live for roughly 15 minutes. Keep the
    // local authorization proof slightly longer so a valid device flow cannot
    // fail during its final token exchange.
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Issue(Guid shareId, string localPath, string providerId)
    {
        RemoveExpired();
        var token = Guid.NewGuid().ToString("N");
        _tickets[token] = new Ticket(
            shareId,
            localPath,
            providerId,
            DateTimeOffset.UtcNow.Add(Lifetime));
        return token;
    }

    /// <inheritdoc />
    public bool IsValid(string token, Guid shareId, string localPath, string providerId)
        => _tickets.TryGetValue(token, out var ticket)
           && Matches(ticket, shareId, localPath, providerId);

    /// <inheritdoc />
    public bool TryConsume(string token, Guid shareId, string localPath, string providerId)
        => _tickets.TryRemove(token, out var ticket)
           && Matches(ticket, shareId, localPath, providerId);

    /// <summary>Opportunistically removes expired tickets when new work begins.</summary>
    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _tickets)
        {
            if (entry.Value.ExpiresAt <= now)
                _tickets.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>
    /// Verifies expiry and the complete authorization context, preventing a
    /// ticket issued for one folder or provider from being replayed elsewhere.
    /// </summary>
    private static bool Matches(Ticket ticket, Guid shareId, string localPath, string providerId)
        => ticket.ExpiresAt > DateTimeOffset.UtcNow
           && ticket.ShareId == shareId
           && string.Equals(ticket.LocalPath, localPath, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ticket.ProviderId, providerId, StringComparison.OrdinalIgnoreCase);

    private sealed record Ticket(
        Guid ShareId,
        string LocalPath,
        string ProviderId,
        DateTimeOffset ExpiresAt);
}
