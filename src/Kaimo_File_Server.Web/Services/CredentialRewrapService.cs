using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;

namespace Kaimo_File_Server.Web.Services;

/// <summary>Result of one bounded credential rewrap pass.</summary>
public sealed record CredentialRewrapResult(int Examined, int Rewrapped, int Busy, int ChangedConcurrently);

/// <summary>
/// Rewrites legacy credential envelopes in bounded batches. Every record is
/// decrypted and protected only while its distributed credential lease is held.
/// </summary>
public sealed class CredentialRewrapService(
    IStorageConnectionRepository connections,
    ICredentialVault credentialVault,
    IStorageConnectionCredentialLeaseManager leases)
{
    public async Task<CredentialRewrapResult> RewrapBatchAsync(
        int maximumRecords = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumRecords is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));

        var candidates = (await connections.GetAllAsync(cancellationToken))
            .Where(connection => !string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload)
                                 && credentialVault.NeedsRewrap(connection.EncryptedCredentialPayload))
            .Take(maximumRecords)
            .ToList();
        var rewrapped = 0;
        var busy = 0;
        var changedConcurrently = 0;

        foreach (var snapshot in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var lease = await leases.TryAcquireAsync(snapshot.Id, cancellationToken);
            if (lease is null)
            {
                busy++;
                continue;
            }

            var current = await connections.GetAsync(snapshot.Id, cancellationToken);
            if (current?.EncryptedCredentialPayload is null
                || !credentialVault.NeedsRewrap(current.EncryptedCredentialPayload))
                continue;

            var context = CredentialContext.ForAuthorizationGrant(current.Id, current.ProviderId);
            var protectedValue = credentialVault.Rewrap<Dictionary<string, string>>(
                current.EncryptedCredentialPayload, context);
            if (await connections.TryUpdateCredentialAsync(
                    current.Id,
                    current.ConcurrencyVersion,
                    protectedValue,
                    StorageConnection.CurrentProtectorPurposeVersion,
                    cancellationToken))
                rewrapped++;
            else
                changedConcurrently++;
        }

        return new CredentialRewrapResult(candidates.Count, rewrapped, busy, changedConcurrently);
    }
}
