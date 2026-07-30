using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// Central enabled-share gate for every bridge data-plane RPC. Repository
/// lookups intentionally include disabled definitions because management
/// workflows need them; Samba-facing callers must never use those definitions.
/// </summary>
public static class EnabledShareResolver
{
    public static async Task<ShareDefinition?> ResolveEnabledShareAsync(
        this IShareRepository shares,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shares);

        var share = await shares.GetByNameAsync(name)
            .WaitAsync(cancellationToken);
        return share is { IsEnabled: true } ? share : null;
    }
}
