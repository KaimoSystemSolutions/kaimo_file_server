using System.IO.Pipelines;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Clouds;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Services;

public sealed record LocalTransferTarget(Guid Id, string Name);
public sealed record CloudTransferResult(bool Success, string? Error = null);

/// <summary>Streams selected remote items into an ACL-protected local share with bounded memory.</summary>
public sealed class CloudToLocalTransferService(
    ICloudAccessRepository cloudRepository,
    ICloudAccessCredentialProtector protector,
    CloudAccessAuthorizationService cloudAuthorization,
    IShareRepository shareRepository,
    IFileServiceFactory fileServiceFactory,
    IUserContextFactory userContextFactory,
    AuthenticationStateProvider authenticationState,
    IHttpClientFactory httpClientFactory,
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

        var connectionRecord = await cloudRepository.GetConnectionAsync(remoteShare.ConnectionId);
        if (connectionRecord?.State != CloudAccessConnectionState.Ready
            || string.IsNullOrWhiteSpace(connectionRecord.ProtectedCredentials))
            return new(false, R("Web_CloudAccess_ConnectionNotReady"));
        var credentials = protector.Unprotect(connectionRecord.ProtectedCredentials);
        await using var remote = new OneDriveConnection(
            credentials, httpClientFactory.CreateClient("CloudAccessOneDrive"));
        try
        {
            foreach (var item in selectedItems)
            {
                if (!ShareRelativePath.TryNormalizeStrict(item.Path, out var relative, allowRoot: false))
                    throw new UnauthorizedAccessException("An invalid remote item path was supplied.");
                await CopyItemAsync(remote, remoteShare, local, actor, relative,
                    ShareRelativePath.Combine(destination, item.Name), item.IsDirectory, cancellationToken);
            }
            if (remote.HasPendingCredentialChanges)
            {
                await cloudRepository.UpdateConnectionRuntimeAsync(
                    connectionRecord.Id, protector.Protect(credentials), null, null,
                    CloudAccessConnectionState.Ready, null, cancellationToken);
            }
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, R("Web_CloudAccess_Error_CopyCancelled"));
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
        OneDriveConnection remote,
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
            foreach (var child in await remote.ListDetailedAsync(remoteDirectory, cancellationToken))
            {
                var childRelative = StripRemoteRoot(remoteShare.RemoteRootPath, child.Path);
                await CopyItemAsync(remote, remoteShare, local, actor, childRelative,
                    ShareRelativePath.Combine(localRelativePath, child.Name), child.IsDirectory, cancellationToken);
            }
            return;
        }

        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 8 * 1024 * 1024,
            resumeWriterThreshold: 4 * 1024 * 1024,
            useSynchronizationContext: false));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                await remote.DownloadAsync(
                    ShareRelativePath.Combine(remoteShare.RemoteRootPath, remoteRelativePath),
                    pipe.Writer.AsStream(), linked.Token);
            }
            catch (Exception exception) { error = exception; throw; }
            finally { await pipe.Writer.CompleteAsync(error); }
        }, linked.Token);
        try
        {
            await local.WriteFileAsync(localRelativePath, pipe.Reader.AsStream(), actor, linked.Token);
            await producer;
        }
        catch
        {
            linked.Cancel();
            try { await producer; } catch { }
            throw;
        }
        finally { await pipe.Reader.CompleteAsync(); }
    }

    private static string StripRemoteRoot(string root, string path)
    {
        root = ShareRelativePath.Normalize(root);
        path = ShareRelativePath.Normalize(path);
        if (root.Length == 0) return path;
        var prefix = root + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Microsoft returned an item outside the configured remote root.");
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
