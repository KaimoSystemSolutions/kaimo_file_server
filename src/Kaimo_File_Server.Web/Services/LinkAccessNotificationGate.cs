using Microsoft.Extensions.Caching.Memory;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Lets the "link accessed" notification through once per (link, visit): a visitor who previews
/// and downloads several files in one page session notifies the creator once. Accesses without a
/// visit id (tickets issued before visits existed) always pass.
/// </summary>
public sealed class LinkAccessNotificationGate
{
    public static readonly TimeSpan VisitLifetime = TimeSpan.FromHours(12);

    // ponytail: in-process; with several web replicas a visit notifies once per replica.
    // When full, new visits are not remembered and simply notify on every access.
    private readonly MemoryCache _seen = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly Lock _lock = new();

    /// <summary>True for the first access of a visit (or any access without a visit).</summary>
    public bool TryEnter(Guid linkId, Guid? visitId)
    {
        if (visitId is null) return true;
        var key = (linkId, visitId.Value);
        lock (_lock)
        {
            if (_seen.TryGetValue(key, out _)) return false;
            _seen.Set(key, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = VisitLifetime, Size = 1 });
            return true;
        }
    }
}
