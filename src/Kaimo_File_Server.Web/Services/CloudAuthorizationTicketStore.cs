using System.Collections.Concurrent;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// In-memory OAuth hand-off store. Tickets intentionally do not survive a
/// server restart and expire quickly because they only cover one redirect.
/// </summary>
public sealed class CloudAuthorizationTicketStore : ICloudAuthorizationTicketStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

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

    public bool IsValid(string token, Guid shareId, string localPath, string providerId)
        => _tickets.TryGetValue(token, out var ticket)
           && Matches(ticket, shareId, localPath, providerId);

    public bool TryConsume(string token, Guid shareId, string localPath, string providerId)
        => _tickets.TryRemove(token, out var ticket)
           && Matches(ticket, shareId, localPath, providerId);

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _tickets)
        {
            if (entry.Value.ExpiresAt <= now)
                _tickets.TryRemove(entry.Key, out _);
        }
    }

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
