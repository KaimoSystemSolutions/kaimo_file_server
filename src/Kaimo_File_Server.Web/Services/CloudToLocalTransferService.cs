using System.IO.Pipelines;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Services;

public sealed record LocalTransferTarget(Guid Id, string Name);
public sealed record CloudTransferResult(bool Success, string? Error = null);

/// <summary>Streams selected remote items into an ACL-protected local share with bounded memory.</summary>
public sealed class CloudToLocalTransferService(
    ICloudAccessRepository cloudRepository,
    IStorageConnectionRepository connections,
    CloudAccessAuthorizationService cloudAuthorization,
    IShareRepository shareRepository,
    IFileServiceFactory fileServiceFactory,
    IUserContextFactory userContextFactory,
    AuthenticationStateProvider authenticationState,
    IStorageConnectionProviderCatalog storageProviders,
    ILogger<CloudToLocalTransferService> logger)
{
    public async Task<List<LocalTransferTarget>> GetTargetsAsync()
    {
        var actor = await GetActorAsync();
        if (actor is null) return [];
        var result = new List<LocalTransferTarget>();
        foreach (var share in await shareRepository.GetAllEnabledAsync())
        {
            try
            {
                var service = fileServiceFactory.CreateForShare(share.Id, share.Path);
                if (await service.CanCreateAsync(string.Empty, actor))
                    result.Add(new LocalTransferTarget(share.Id, share.Name));
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Local share {ShareId} is not a Cloud Access transfer target", share.Id);
            }
        }
        return result.OrderBy(x => x.Name).ToList();
    }

    public async Task<CloudTransferResult> CopyAsync(
        Guid remoteShareId,
        IReadOnlyCollection<FileMetadata> selectedItems,
        Guid localShareId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (selectedItems.Count == 0) return new(false, R("Web_CloudAccess_Error_NoSelection"));
        var actor = await GetActorAsync();
        if (actor is null) return new(false, R("Web_CloudAccess_Error_SignInRequired"));
        var remoteShare = await cloudRepository.GetShareAsync(remoteShareId);
        if (remoteShare is null || !await cloudAuthorization.CanAccessAsync(actor, remoteShare))
            return new(false, R("Web_CloudAccess_Error_AccessDenied"));
        if (!ShareRelativePath.TryNormalizeStrict(destinationDirectory ?? string.Empty, out var destination))
            return new(false, R("Web_CloudAccess_Error_InvalidLocalPath"));
        var localShare = await shareRepository.GetByIdAsync(localShareId);
        if (localShare is null || !localShare.IsEnabled) return new(false, R("Web_CloudAccess_Error_LocalUnavailable"));
        var local = fileServiceFactory.CreateForShare(localShare.Id, localShare.Path);
        if (!await local.CanCreateAsync(destination, actor)) return new(false, R("Web_CloudAccess_Error_LocalWriteDenied"));

        var connectionRecord = await connections.GetAsync(remoteShare.ConnectionId);
        if (connectionRecord?.State != StorageConnectionState.Ready)
            return new(false, R("Web_CloudAccess_ConnectionNotReady"));
        await using var session = await storageProviders.GetRequired(connectionRecord.ProviderId)
            .OpenSessionAsync(connectionRecord, cancellationToken);
        var remote = session.RemoteFiles;
        if (remote is null) return new(false, R("Web_CloudAccess_ConnectionNotReady"));
        try
        {
            foreach (var item in selectedItems)
            {
                if (!ShareRelativePath.TryNormalizeStrict(item.Path, out var relative, allowRoot: false))
                    throw new UnauthorizedAccessException("An invalid remote item path was supplied.");
                await CopyItemAsync(remote, remoteShare, local, actor, relative,
                    ShareRelativePath.Combine(destination, item.Name), item.IsDirectory, cancellationToken);
            }
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, R("Web_CloudAccess_Error_CopyCancelled"));
        }
        catch (RemoteStorageAccessDeniedException exception)
        {
            logger.LogWarning(exception, "Remote storage denied read access for virtual share {RemoteShareId}",
                remoteShareId);
            return new(false, R("Web_ExternalStorage_RemoteReadDenied"));
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Cloud-to-local transfer from {RemoteShareId} to {LocalShareId} failed",
                remoteShareId, localShareId);
            return new(false, R("Web_CloudAccess_Error_CopyFailed"));
        }
    }

    private static async Task CopyItemAsync(
        IRemoteFileStore remote,
        CloudAccessShare remoteShare,
        IFileService local,
        Kaimo_File_Server.Core.Domain.Identity.UserContext actor,
        string remoteRelativePath,
        string localRelativePath,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        if (isDirectory)
        {
            await local.CreateDirectoryAsync(localRelativePath, actor);
            var remoteDirectory = ShareRelativePath.Combine(remoteShare.RemoteRootPath, remoteRelativePath);
            foreach (var child in await remote.ListAsync(remoteDirectory, cancellationToken))
            {
                var childRelative = StripRemoteRoot(remoteShare.RemoteRootPath, child.Path);
                await CopyItemAsync(remote, remoteShare, local, actor, childRelative,
                    ShareRelativePath.Combine(localRelativePath, child.Name), child.IsDirectory, cancellationToken);
            }
            return;
        }

        await using var content = await remote.OpenReadAsync(
            ShareRelativePath.Combine(remoteShare.RemoteRootPath, remoteRelativePath), cancellationToken);
        await local.WriteFileAsync(localRelativePath, content, actor, cancellationToken);
    }

    private static string StripRemoteRoot(string root, string path)
    {
        root = ShareRelativePath.Normalize(root);
        path = ShareRelativePath.Normalize(path);
        if (root.Length == 0) return path;
        var prefix = root + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The provider returned an item outside the configured remote root.");
        return path[prefix.Length..];
    }

    private async Task<Kaimo_File_Server.Core.Domain.Identity.UserContext?> GetActorAsync()
    {
        var state = await authenticationState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;
}
