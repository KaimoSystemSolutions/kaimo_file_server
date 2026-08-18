using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Database-backed, single-use authorization transaction store. Instances in
/// different Web containers validate and consume the same short-lived records.
/// Browser tokens contain 256 bits of entropy and are stored only as hashes.
/// </summary>
public sealed class CloudAuthorizationTicketStore(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeProvider timeProvider) : ICloudAuthorizationTicketStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    /// <inheritdoc />
    public async Task<string> IssueAsync(
        Guid resourceId,
        string localPath,
        string providerId,
        Guid? initiatingUserId = null,
        Guid? departmentId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.StorageAuthorizationTransactions.Add(new StorageAuthorizationTransaction
        {
            TokenHash = Hash(token),
            ResourceId = resourceId,
            ResourcePath = localPath ?? string.Empty,
            ProviderId = providerId,
            InitiatingUserId = initiatingUserId,
            DepartmentId = departmentId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(Lifetime)
        });
        await db.SaveChangesAsync(cancellationToken);
        await RemoveExpiredAsync(db, now, cancellationToken);
        return token;
    }

    /// <inheritdoc />
    public async Task<bool> IsValidAsync(
        string token,
        Guid resourceId,
        string localPath,
        string providerId,
        Guid? initiatingUserId = null,
        Guid? departmentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var hash = Hash(token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.StorageAuthorizationTransactions.AsNoTracking().AnyAsync(
            transaction => transaction.TokenHash == hash
                           && transaction.ConsumedAtUtc == null
                           && transaction.ExpiresAtUtc > now
                           && transaction.ResourceId == resourceId
                           && transaction.ResourcePath.ToLower() == (localPath ?? string.Empty).ToLower()
                           && transaction.ProviderId.ToLower() == providerId.ToLower()
                           && (!initiatingUserId.HasValue || transaction.InitiatingUserId == initiatingUserId)
                           && (!departmentId.HasValue || transaction.DepartmentId == departmentId),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TryConsumeAsync(
        string token,
        Guid resourceId,
        string localPath,
        string providerId,
        Guid? initiatingUserId = null,
        Guid? departmentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var hash = Hash(token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var affected = await db.StorageAuthorizationTransactions
            .Where(transaction => transaction.TokenHash == hash
                                  && transaction.ConsumedAtUtc == null
                                  && transaction.ExpiresAtUtc > now
                                  && transaction.ResourceId == resourceId
                                  && transaction.ResourcePath.ToLower() == (localPath ?? string.Empty).ToLower()
                                  && transaction.ProviderId.ToLower() == providerId.ToLower()
                                  && (!initiatingUserId.HasValue || transaction.InitiatingUserId == initiatingUserId)
                                  && (!departmentId.HasValue || transaction.DepartmentId == departmentId))
            .ExecuteUpdateAsync(
                update => update.SetProperty(transaction => transaction.ConsumedAtUtc, now),
                cancellationToken);
        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryAttachProtectedContextAsync(
        string token,
        Guid resourceId,
        string localPath,
        string providerId,
        Guid initiatingUserId,
        Guid departmentId,
        string protectedContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(protectedContext))
            return false;

        var hash = Hash(token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var affected = await db.StorageAuthorizationTransactions
            .Where(transaction => transaction.TokenHash == hash
                                  && transaction.ConsumedAtUtc == null
                                  && transaction.ExpiresAtUtc > now
                                  && transaction.ResourceId == resourceId
                                  && transaction.ResourcePath.ToLower() == (localPath ?? string.Empty).ToLower()
                                  && transaction.ProviderId.ToLower() == providerId.ToLower()
                                  && transaction.InitiatingUserId == initiatingUserId
                                  && transaction.DepartmentId == departmentId
                                  && transaction.ProtectedContext == null)
            .ExecuteUpdateAsync(
                update => update.SetProperty(
                    transaction => transaction.ProtectedContext,
                    protectedContext),
                cancellationToken);
        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<CloudAuthorizationTransaction?> TryConsumeCallbackAsync(
        string token,
        string providerId,
        Guid initiatingUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var hash = Hash(token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var transaction = await db.StorageAuthorizationTransactions.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TokenHash == hash
                        && item.ConsumedAtUtc == null
                        && item.ExpiresAtUtc > now
                        && item.ProviderId.ToLower() == providerId.ToLower()
                        && item.InitiatingUserId == initiatingUserId
                        && item.ProtectedContext != null,
                cancellationToken);
        if (transaction is null)
            return null;

        var affected = await db.StorageAuthorizationTransactions
            .Where(item => item.TokenHash == hash
                           && item.ConsumedAtUtc == null
                           && item.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                update => update.SetProperty(item => item.ConsumedAtUtc, now),
                cancellationToken);
        return affected == 1
            ? new CloudAuthorizationTransaction(
                transaction.ResourceId,
                transaction.ResourcePath,
                transaction.DepartmentId,
                transaction.ProtectedContext!)
            : null;
    }

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task RemoveExpiredAsync(
        ApplicationDbContext db,
        DateTime now,
        CancellationToken cancellationToken)
        => _ = await db.StorageAuthorizationTransactions
            .Where(transaction => transaction.ExpiresAtUtc <= now)
            .ExecuteDeleteAsync(cancellationToken);
}
