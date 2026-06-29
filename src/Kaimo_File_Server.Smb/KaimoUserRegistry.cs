using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Smb;

/// <summary>
/// Maps an authenticated SMB username to its fully-resolved Kaimo <see cref="UserContext"/>
/// (roles, departments, permissions). Populated once per session by
/// <see cref="KaimoIdentityBackend"/> during auth, then read by the file store and share policy —
/// so per-operation authorization needs no extra DB round-trip.
///
/// The SMB library only carries the username (via the ambient <see cref="Smb.FileSystem.SmbCaller"/>
/// or <see cref="Smb.Auth.SecurityIdentity"/>); this registry is the bridge back to the rich context.
/// </summary>
internal static class KaimoUserRegistry
{
    private static readonly ConcurrentDictionary<string, UserContext> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(UserContext user)
    {
        if (user?.User?.Username is { } name)
            _byName[name] = user;
    }

    public static UserContext? Lookup(string? username)
        => username != null && _byName.TryGetValue(username, out var u) ? u : null;
}
