using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Smb.Auth;

namespace Kaimo_File_Server.Smb;

/// <summary>
/// <see cref="IIdentityBackend"/> backed by the Kaimo user database. The library's
/// <see cref="Smb.Auth.Ntlm.NtlmServerMechanism"/> drives the NTLMv2 handshake and only asks this
/// backend for the user's NT hash and identity — replacing the old hand-rolled
/// <c>NtHashAuthenticationProvider</c>.
///
/// <see cref="IAuthenticationLookup"/> is resolved per call from a fresh DI scope (it is backed by a
/// scoped DbContext), mirroring the previous SMB integration.
/// </summary>
internal sealed class KaimoIdentityBackend : IIdentityBackend
{
    private readonly IServiceProvider _services;

    public KaimoIdentityBackend(IServiceProvider services) => _services = services;

    public bool TryGetNtHash(string domain, string user, out byte[] ntHash)
    {
        ntHash = Array.Empty<byte>();
        try
        {
            using var scope = _services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
            byte[]? hash = SmbSync.Run(() => auth.GetNtHashAsync(user));
            if (hash is not { Length: > 0 })
            {
                Console.WriteLine($"[-] User '{user}' nicht gefunden");
                return false;
            }
            ntHash = hash;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NtHash ERROR] Failed to retrieve hash for '{user}': {ex.Message}");
            return false;
        }
    }

    public SecurityIdentity Resolve(string domain, string user)
    {
        using var scope = _services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationLookup>();
        UserContext? ctx = SmbSync.Run(() => auth.ResolveUserContextAsync(user));
        if (ctx == null)
            throw new KeyNotFoundException($"User '{user}' not found.");

        // Cache the rich context so per-operation authorization needs no further DB round-trip.
        KaimoUserRegistry.Register(ctx);
        Console.WriteLine($"[+] User '{user}' authentifiziert");
        return new SecurityIdentity { DomainName = domain ?? string.Empty, UserName = user };
    }
}
