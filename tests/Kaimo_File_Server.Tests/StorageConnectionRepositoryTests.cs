using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class StorageConnectionRepositoryTests : DatabaseTestBase
{
    [Fact]
    public async Task SaveAsync_RoundTripsNeutralConnectionMetadata()
    {
        var repository = new StorageConnectionRepository(DbFactory);
        var connection = NewConnection();
        connection.ProviderTenantId = "tenant-id";
        connection.ProviderSubjectId = "subject-id";
        connection.EffectiveScopes = "offline_access Files.ReadWrite";

        await repository.SaveAsync(connection);
        var persisted = await repository.GetAsync(connection.Id);

        Assert.NotNull(persisted);
        Assert.Equal("onedrive", persisted.ProviderId);
        Assert.Equal(StorageAuthorizationMode.DeviceCode, persisted.AuthorizationMode);
        Assert.Equal("tenant-id", persisted.ProviderTenantId);
        Assert.Equal("subject-id", persisted.ProviderSubjectId);
        Assert.Equal(1, persisted.ConcurrencyVersion);
    }

    [Fact]
    public async Task SaveAsync_RejectsAStaleConnectionSnapshot()
    {
        var repository = new StorageConnectionRepository(DbFactory);
        var connection = NewConnection();
        await repository.SaveAsync(connection);
        var firstWriter = await repository.GetAsync(connection.Id);
        var staleWriter = await repository.GetAsync(connection.Id);
        Assert.NotNull(firstWriter);
        Assert.NotNull(staleWriter);

        firstWriter.Name = "First writer";
        await repository.SaveAsync(firstWriter);
        staleWriter.Name = "Stale writer";

        var exception = await Assert.ThrowsAsync<StorageConnectionConcurrencyException>(
            () => repository.SaveAsync(staleWriter));

        Assert.Equal(connection.Id, exception.ConnectionId);
        Assert.Equal("First writer", (await repository.GetAsync(connection.Id))!.Name);
    }

    [Fact]
    public async Task DeleteAsync_IsBlockedWhileVirtualShareReferencesConnection()
    {
        var repository = new StorageConnectionRepository(DbFactory);
        var connection = NewConnection();
        await repository.SaveAsync(connection);
        await using (var db = NewContext())
        {
            db.CloudAccessShares.Add(new CloudAccessShare
            {
                ConnectionId = connection.Id,
                DepartmentId = connection.DepartmentId,
                Name = "Remote documents",
                RemoteRootPath = "documents"
            });
            await db.SaveChangesAsync();
        }

        var usage = await repository.GetUsageAsync(connection.Id);
        var result = await repository.DeleteAsync(connection.Id);

        Assert.Equal(0, usage.SyncCount);
        Assert.Equal(1, usage.VirtualShareCount);
        Assert.Equal(1, usage.TotalCount);
        Assert.Equal(StorageConnectionDeleteResult.InUse, result);
        Assert.NotNull(await repository.GetAsync(connection.Id));
    }

    [Fact]
    public async Task RestrictiveForeignKey_RejectsDirectConnectionDeletion()
    {
        var repository = new StorageConnectionRepository(DbFactory);
        var connection = NewConnection();
        await repository.SaveAsync(connection);
        await using (var db = NewContext())
        {
            db.CloudAccessShares.Add(new CloudAccessShare
            {
                ConnectionId = connection.Id,
                DepartmentId = connection.DepartmentId,
                Name = "Protected remote share",
                RemoteRootPath = string.Empty
            });
            await db.SaveChangesAsync();
        }

        await using var deletionContext = NewContext();
        deletionContext.StorageConnections.Remove(
            await deletionContext.StorageConnections.SingleAsync(item => item.Id == connection.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => deletionContext.SaveChangesAsync());
    }

    [Fact]
    public async Task DeleteAsync_IsBlockedWhileSyncDefinitionReferencesConnection()
    {
        var repository = new StorageConnectionRepository(DbFactory);
        var connection = NewConnection();
        await repository.SaveAsync(connection);
        var share = SeedShare("sync-source");
        await using (var db = NewContext())
        {
            db.SyncDefinitions.Add(new SyncDefinition
            {
                ConnectionId = connection.Id,
                LocalShareId = share.Id,
                LocalPath = "projects",
                RemotePath = "/projects"
            });
            await db.SaveChangesAsync();
        }

        var usage = await repository.GetUsageAsync(connection.Id);
        var result = await repository.DeleteAsync(connection.Id);

        Assert.Equal(1, usage.SyncCount);
        Assert.Equal(0, usage.VirtualShareCount);
        Assert.Equal(StorageConnectionDeleteResult.InUse, result);
        Assert.NotNull(await repository.GetAsync(connection.Id));
    }

    [Fact]
    public async Task UpdateRuntimeAsync_RecordsCredentialAndIncrementsVersion()
    {
        var repository = new StorageConnectionRepository(DbFactory);
        var connection = NewConnection();
        await repository.SaveAsync(connection);

        await repository.UpdateRuntimeAsync(
            connection.Id,
            "dp:v2:encrypted",
            "Account",
            "account@example.test",
            StorageConnectionState.Ready,
            lastErrorCode: null);

        var persisted = await repository.GetAsync(connection.Id);
        Assert.NotNull(persisted);
        Assert.Equal("dp:v2:encrypted", persisted.EncryptedCredentialPayload);
        Assert.Equal(StorageConnection.CurrentProtectorPurposeVersion, persisted.ProtectorPurposeVersion);
        Assert.Equal(StorageConnectionState.Ready, persisted.State);
        Assert.Equal(2, persisted.ConcurrencyVersion);
        Assert.NotNull(persisted.CredentialUpdatedAtUtc);
        Assert.NotNull(persisted.LastVerifiedAtUtc);
    }

    private static StorageConnection NewConnection()
        => new()
        {
            DepartmentId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid(),
            ProviderId = "onedrive",
            Name = $"Connection {Guid.NewGuid():N}",
            AuthorizationMode = StorageAuthorizationMode.DeviceCode,
            State = StorageConnectionState.PendingAuthorization
        };
}
