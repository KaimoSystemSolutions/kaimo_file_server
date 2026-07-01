using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Smb;

/// <summary>
/// Maps an authenticated SMB username to its fully-resolved Kaimo <see cref="UserContext"/>
/// (roles, departments, permissions). Populated at auth time by <see cref="KaimoIdentityBackend"/>,
/// then read by the file store and share policy — so per-operation authorization avoids a DB
/// round-trip on the hot path.
///
/// Entries carry a short TTL. Once expired, the next lookup re-resolves the context from the
/// database, so revoked group/role/department membership (and disabled/deleted accounts) take
/// effect within the TTL instead of being served stale for the lifetime of the process. A user
/// who can no longer be resolved is evicted and denied; a transient DB error keeps serving the
/// last known context (the per-path ACL query downstream is still fail-closed).
///
/// The SMB library only carries the username (via the ambient <see cref="Smb.FileSystem.SmbCaller"/>
/// or <see cref="Smb.Auth.SecurityIdentity"/>); this registry is the bridge back to the rich context.
/// </summary>
internal static class KaimoUserRegistry
{
    private static readonly ConcurrentDictionary<string, Entry> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    private static IServiceProvider? _services;
    private static TimeSpan _ttl = TimeSpan.FromSeconds(30);
    private static Func<DateTime> _clock = () => DateTime.UtcNow;

    private sealed record Entry(UserContext Context, DateTime ResolvedUtc);

    /// <summary>
    /// Wires the registry to DI so expired entries can be re-resolved. Called once when the SMB
    /// server starts; safe to call again on restart.
    /// </summary>
    public static void Initialize(IServiceProvider services, TimeSpan? ttl = null)
    {
        _services = services;
        if (ttl is { } t) _ttl = t;
    }

    public static void Register(UserContext user)
    {
        if (user?.User?.Username is { } name)
            _byName[name] = new Entry(user, _clock());
    }

    /// <summary>
    /// Returns the cached context if still fresh; otherwise re-resolves it from the database.
    /// Returns null when the user cannot be resolved (unknown/disabled) — callers treat that as
    /// access denied.
    /// </summary>
    public static UserContext? Lookup(string? username)
    {
        if (string.IsNullOrEmpty(username))
            return null;

        if (_byName.TryGetValue(username, out var entry)
            && _clock() - entry.ResolvedUtc < _ttl)
            return entry.Context;

        return Refresh(username);
    }

    private static UserContext? Refresh(string username)
    {
        // Not wired to DI yet (shouldn't happen once the server has started): serve whatever we
        // have rather than locking the user out.
        if (_services == null)
            return _byName.TryGetValue(username, out var cached) ? cached.Context : null;

        try
        {
            using var scope = _services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
            UserContext? ctx = SmbSync.Run(() => auth.ResolveUserContextAsync(username));
            if (ctx != null)
            {
                Register(ctx);
                return ctx;
            }

            // Resolved to nothing → the account is gone or disabled: drop the stale grant.
            _byName.TryRemove(username, out _);
            return null;
        }
        catch
        {
            // Transient failure (DB down, etc.): keep serving the last known context so a blip
            // doesn't drop every live session. Downstream per-path ACL checks still hit the DB
            // and fail closed on their own if it is truly unavailable.
            return _byName.TryGetValue(username, out var cached) ? cached.Context : null;
        }
    }

    /// <summary>Test-only: resets all state so each test starts from a clean registry.</summary>
    internal static void ResetForTests(TimeSpan ttl, Func<DateTime> clock, IServiceProvider? services)
    {
        _byName.Clear();
        _ttl = ttl;
        _clock = clock;
        _services = services;
    }
}
