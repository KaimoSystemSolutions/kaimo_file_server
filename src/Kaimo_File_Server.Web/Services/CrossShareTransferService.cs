using System.IO.Pipelines;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Services;

/// <summary>A browser-visible destination which the current user may write to.</summary>
public sealed record CrossShareTransferTarget(BrowserShareInfo Share);

/// <summary>Result of a copy or cut operation between browser backends.</summary>
public sealed record CrossShareTransferResult(bool Success, string? Error = null);

/// <summary>
/// Copies browser items between local and Cloud Access shares without materialising a file
/// in memory. A cut is deliberately accepted only for a local source and removes the
/// source only after every destination write completed successfully.
/// </summary>
public sealed class CrossShareTransferService(
    ICloudAccessRepository cloudRepository,
    IStorageConnectionRepository connections,
    ICredentialVault credentialVault,
    CloudAccessAuthorizationService cloudAuthorization,
    IShareRepository shareRepository,
    IFileServiceFactory fileServiceFactory,
    IUserContextFactory userContextFactory,
    AuthenticationStateProvider authenticationState,
    IHttpClientFactory httpClientFactory,
    ILogger<CrossShareTransferService> logger)
{
    public async Task<List<CrossShareTransferTarget>> GetTargetsAsync()
    {
        var actor = await GetActorAsync();
        if (actor is null) return [];

        var targets = new List<CrossShareTransferTarget>();
        foreach (var share in await shareRepository.GetAllEnabledAsync())
        {
            var local = fileServiceFactory.CreateForShare(share.Id, share.Path);
            if (await local.CanCreateAsync(string.Empty, actor))
                targets.Add(new(new BrowserShareInfo(share.Id, share.Name, BrowserShareKind.Local)));
        }

        foreach (var share in await cloudRepository.GetSharesAsync())
        {
            if (share.IsEnabled && !share.IsReadOnly && await cloudAuthorization.CanAccessAsync(actor, share))
                targets.Add(new(new BrowserShareInfo(share.Id, share.Name, BrowserShareKind.Remote, "onedrive")));
        }
        return targets.OrderBy(target => target.Share.Name).ToList();
    }

    public async Task<CrossShareTransferResult> TransferAsync(
        BrowserShareInfo source,
        IReadOnlyCollection<FileMetadata> selectedItems,
        BrowserShareInfo target,
        string destinationDirectory,
        bool cut,
        CancellationToken cancellationToken = default,
        Action<string, int>? reportProgress = null)
    {
        if (selectedItems.Count == 0) return Fail("Web_Transfer_Error_NoSelection");
        if (cut && source.Kind != BrowserShareKind.Local) return Fail("Web_Transfer_Error_RemoteCut");
        if (!ShareRelativePath.TryNormalizeStrict(destinationDirectory ?? string.Empty, out var destination))
            return Fail("Web_Transfer_Error_InvalidPath");

        var actor = await GetActorAsync();
        if (actor is null) return Fail("Web_Transfer_Error_SignInRequired");

        try
        {
            await using var sourceBackend = await OpenBackendAsync(source, actor, requireWrite: cut, cancellationToken);
            await using var targetBackend = await OpenBackendAsync(target, actor, requireWrite: true, cancellationToken);

            var completed = 0;
            foreach (var item in selectedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeItemPath(item.Path, sourceBackend.LocalShare);
                await CopyItemAsync(sourceBackend, targetBackend, relative,
                    ShareRelativePath.Combine(destination, item.Name), item.IsDirectory, actor, cancellationToken);
                completed++;
                reportProgress?.Invoke(item.Name, (int)(completed * 100f / selectedItems.Count));
            }

            // A cut is transactional at the item level: sources are deleted only when
            // all copies have succeeded. A failed copy therefore leaves every source intact.
            if (cut)
            {
                foreach (var item in selectedItems)
                    await sourceBackend.Local!.DeleteFileAsync(
                        NormalizeItemPath(item.Path, sourceBackend.LocalShare), actor,
                        sourceBackend.LocalShare!.IsRecycleEnabled);
            }

            await sourceBackend.PersistCredentialsAsync(connections, credentialVault, cancellationToken);
            await targetBackend.PersistCredentialsAsync(connections, credentialVault, cancellationToken);
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail("Web_Transfer_Error_Cancelled");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Cross-share transfer was denied from {Source} to {Target}", source.Id, target.Id);
            return Fail("Web_Transfer_Error_AccessDenied");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cross-share transfer failed from {Source} to {Target}", source.Id, target.Id);
            return Fail("Web_Transfer_Error_Failed");
        }
    }

    private async Task<TransferBackend> OpenBackendAsync(
        BrowserShareInfo info, UserContext actor, bool requireWrite, CancellationToken cancellationToken)
    {
        if (info.Kind == BrowserShareKind.Local)
        {
            var share = await shareRepository.GetByIdAsync(info.Id)
                ?? throw new UnauthorizedAccessException("The local share does not exist.");
            if (!share.IsEnabled) throw new UnauthorizedAccessException("The local share is disabled.");
            var local = fileServiceFactory.CreateForShare(share.Id, share.Path);
            if (requireWrite ? !await local.CanCreateAsync(string.Empty, actor) : !await local.CanListAsync(string.Empty, actor))
                throw new UnauthorizedAccessException("The local share is not accessible.");
            return new(local, share);
        }

        var shareRemote = await cloudRepository.GetShareAsync(info.Id, cancellationToken)
            ?? throw new UnauthorizedAccessException("The virtual share does not exist.");
        if (!shareRemote.IsEnabled || (requireWrite && shareRemote.IsReadOnly)
            || !await cloudAuthorization.CanAccessAsync(actor, shareRemote))
            throw new UnauthorizedAccessException("The virtual share is not accessible.");
        var record = await connections.GetAsync(shareRemote.ConnectionId, cancellationToken);
        if (record?.State != StorageConnectionState.Ready || string.IsNullOrWhiteSpace(record.EncryptedCredentialPayload))
            throw new InvalidOperationException("The virtual-share connection is unavailable.");
        var credentials = credentialVault.UnprotectConnectionCredentials(record);
        return new(new OneDriveConnection(credentials, httpClientFactory.CreateClient("CloudAccessOneDrive")),
            shareRemote, record, credentials);
    }

    private static async Task CopyItemAsync(TransferBackend source, TransferBackend target,
        string sourcePath, string targetPath, bool isDirectory, UserContext actor, CancellationToken cancellationToken)
    {
        if (isDirectory)
        {
            await target.CreateDirectoryAsync(targetPath, actor, cancellationToken);
            foreach (var child in await source.ListAsync(sourcePath, actor, cancellationToken))
                await CopyItemAsync(source, target, child.Path, ShareRelativePath.Combine(targetPath, child.Name),
                    child.IsDirectory, actor, cancellationToken);
            return;
        }

        await using var input = await source.ReadAsync(sourcePath, actor, cancellationToken);
        await target.WriteAsync(targetPath, input, actor, cancellationToken);
    }

    private static string NormalizeItemPath(string path, ShareDefinition? localShare)
    {
        if (localShare is not null && path.StartsWith(localShare.Path, StringComparison.OrdinalIgnoreCase))
            path = path[localShare.Path.Length..].TrimStart('/');
        if (!ShareRelativePath.TryNormalizeStrict(path, out var normalized, allowRoot: false))
            throw new UnauthorizedAccessException("An invalid source path was supplied.");
        return normalized;
    }

    private async Task<UserContext?> GetActorAsync()
    {
        var state = await authenticationState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
    }

    private static CrossShareTransferResult Fail(string key)
        => new(false, Core.Language.Resources.ResourceManager.GetString(key) ?? key);

    private sealed class TransferBackend : IAsyncDisposable
    {
        public TransferBackend(IFileService local, ShareDefinition share) { Local = local; LocalShare = share; }
        public TransferBackend(OneDriveConnection remote, CloudAccessShare share, StorageConnection record, Dictionary<string, string> credentials)
        { Remote = remote; RemoteShare = share; ConnectionRecord = record; Credentials = credentials; }
        public IFileService? Local { get; }
        public ShareDefinition? LocalShare { get; }
        public OneDriveConnection? Remote { get; }
        public CloudAccessShare? RemoteShare { get; }
        public StorageConnection? ConnectionRecord { get; }
        public Dictionary<string, string>? Credentials { get; }

        public async Task<List<FileMetadata>> ListAsync(string path, UserContext actor, CancellationToken cancellationToken)
        {
            if (Local is not null) return await Local.ListAsync(path, actor);
            var items = await Remote!.ListDetailedAsync(RemotePath(path), cancellationToken);
            return items.Select(item => new FileMetadata { Name = item.Name, Path = StripRoot(item.Path), IsDirectory = item.IsDirectory }).ToList();
        }
        public async Task<Stream> ReadAsync(string path, UserContext actor, CancellationToken cancellationToken)
        {
            if (Local is not null) return await Local.ReadFileAsync(path, actor);
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                Exception? error = null;
                try { await Remote!.DownloadAsync(RemotePath(path), pipe.Writer.AsStream(), cancellationToken); }
                catch (Exception exception) { error = exception; }
                finally { await pipe.Writer.CompleteAsync(error); }
            }, cancellationToken);
            return pipe.Reader.AsStream();
        }
        public async Task WriteAsync(string path, Stream input, UserContext actor, CancellationToken cancellationToken)
        {
            if (Local is not null) { await Local.WriteFileAsync(path, input, actor, cancellationToken); return; }
            await Remote!.UploadAsync(RemotePath(path), input, DateTime.UtcNow, cancellationToken);
        }
        public async Task CreateDirectoryAsync(string path, UserContext actor, CancellationToken cancellationToken)
        {
            if (Local is not null) { await Local.CreateDirectoryAsync(path, actor); return; }
            await Remote!.CreateDirectoryAsync(RemotePath(path), cancellationToken);
        }
        public async Task PersistCredentialsAsync(IStorageConnectionRepository repository, ICredentialVault credentialVault, CancellationToken cancellationToken)
        {
            if (Remote?.HasPendingCredentialChanges == true)
                await repository.UpdateRuntimeAsync(ConnectionRecord!.Id,
                    credentialVault.ProtectConnectionCredentials(ConnectionRecord, Credentials!), null, null,
                    StorageConnectionState.Ready, null, cancellationToken);
        }
        private string RemotePath(string path) => ShareRelativePath.Combine(RemoteShare!.RemoteRootPath, path);
        private string StripRoot(string path)
        {
            var root = ShareRelativePath.Normalize(RemoteShare!.RemoteRootPath);
            path = ShareRelativePath.Normalize(path);
            return root.Length == 0 ? path : path[(root.Length + 1)..];
        }
        public async ValueTask DisposeAsync() { if (Remote is not null) await Remote.DisposeAsync(); }
    }
}
