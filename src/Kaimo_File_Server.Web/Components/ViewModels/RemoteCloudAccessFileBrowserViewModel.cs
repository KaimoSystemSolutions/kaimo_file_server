using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public sealed class RemoteCloudAccessFileBrowserViewModel : RemoteFileBrowserViewModelBase, IAsyncDisposable
{
    private static readonly BrowserCapabilities WritableCapabilities = new()
    {
        CanOpen = true, CanUpload = true, CanCreateDirectory = true, CanRename = true,
        CanDelete = true, CanMove = true, CanCopy = true, CanCopyToLocal = true, HasProperties = true
    };
    private static readonly BrowserCapabilities ReadOnlyCapabilities = new()
    {
        CanOpen = true, CanCopyToLocal = true, HasProperties = true
    };

    private readonly ICloudAccessRepository _repository;
    private readonly IStorageConnectionRepository _connections;
    private readonly CloudAccessAuthorizationService _authorization;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly IStorageConnectionProviderCatalog _providers;
    private readonly ILogger<RemoteCloudAccessFileBrowserViewModel> _logger;
    private readonly CloudAccessDownloadTicketStore _downloadTickets;
    private readonly CloudAccessDirectoryCache _directoryCache;
    private CloudAccessShare? _share;
    private StorageConnection? _connectionRecord;
    private IStorageSession? _session;
    private IRemoteFileStore? _remoteFiles;

    public RemoteCloudAccessFileBrowserViewModel(
        ICloudAccessRepository repository,
        IStorageConnectionRepository connections,
        CloudAccessAuthorizationService authorization,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authenticationState,
        IStorageConnectionProviderCatalog providers,
        CloudAccessDownloadTicketStore downloadTickets,
        CloudAccessDirectoryCache directoryCache,
        ILogger<RemoteCloudAccessFileBrowserViewModel> logger)
        : base(WritableCapabilities)
    {
        _repository = repository;
        _connections = connections;
        _authorization = authorization;
        _userContextFactory = userContextFactory;
        _authenticationState = authenticationState;
        _providers = providers;
        _downloadTickets = downloadTickets;
        _directoryCache = directoryCache;
        _logger = logger;
    }

    public override async Task LoadShareAsync(string shareKey, string subPath = "")
    {
        BeginLoad();
        try
        {
            if (!Guid.TryParse(shareKey, out var shareId))
                throw new UnauthorizedAccessException(R("Web_CloudAccess_Error_InvalidShare"));
            if (!ShareRelativePath.TryNormalizeStrict(subPath ?? string.Empty, out var relativePath))
                throw new UnauthorizedAccessException(R("Web_CloudAccess_Error_InvalidPath"));

            var share = await _repository.GetShareAsync(shareId)
                        ?? throw new FileNotFoundException("The virtual share does not exist.");
            var actor = await GetActorAsync() ?? throw new UnauthorizedAccessException("Sign-in is required.");
            if (!await _authorization.CanAccessAsync(actor, share))
                throw new UnauthorizedAccessException("You may not access this virtual share.");

            var connectionRecord = await _connections.GetAsync(share.ConnectionId)
                                   ?? throw new InvalidOperationException("The provider connection is missing.");

            // A disabled or not-yet-authorized connection is an expected configuration
            // state, not a load failure. Explain the concrete reason to the user instead
            // of surfacing the generic "could not be loaded" message, and stop here.
            if (connectionRecord.State != StorageConnectionState.Ready)
            {
                _logger.LogInformation(
                    "Virtual share {ShareKey} is unavailable: storage connection {ConnectionId} is in state {State}.",
                    shareKey, connectionRecord.Id, connectionRecord.State);
                FailLoad(ConnectionUnavailableMessage(connectionRecord));
                return;
            }

            await ReplaceConnectionAsync(connectionRecord);
            _share = share;
            Capabilities = BuildCapabilities(_session!.Capabilities, share.IsReadOnly);
            var items = await ListCoreAsync(relativePath);
            CompleteLoad(
                new BrowserShareInfo(share.Id, share.Name, BrowserShareKind.Remote, connectionRecord.ProviderId),
                relativePath,
                items);
        }
        catch (RemoteStorageAccessDeniedException exception)
        {
            _logger.LogWarning(exception, "Remote storage denied read access to virtual share {ShareKey}", shareKey);
            FailLoad(R("Web_ExternalStorage_RemoteReadDenied"));
        }
        catch (UnauthorizedAccessException)
        {
            FailLoad(R("Web_CloudAccess_Error_AccessDenied"));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to load Cloud Access virtual share {ShareKey}", shareKey);
            FailLoad(R("Web_CloudAccess_Error_LoadShare"));
        }
    }

    public override async Task RefreshCurrentDirectoryAsync()
    {
        if (_share is null) return;
        _directoryCache.InvalidateShare(_share.Id);
        await LoadShareAsync(_share.Id.ToString(), CurrentPath);
    }

    public override async Task<OperationResult> CreateFolderAsync(string folderName)
        => await RunWriteAsync(files =>
            files.CreateDirectoryAsync(RemotePath(ShareRelativePath.Combine(CurrentPath, folderName))),
            R("Web_CloudAccess_Error_CreateFolder"));

    public override async Task CreateFolderAtAsync(string path)
    {
        var result = await RunWriteAsync(files => files.CreateDirectoryAsync(RemotePath(path)),
            R("Web_CloudAccess_Error_CreateFolder"));
        if (!result.Success) throw new IOException(result.Error);
    }

    public override Task<OperationResult> DeleteAsync(FileMetadata item)
        => RunWriteAsync(files => files.DeleteAsync(RemotePath(ShareRelativeOf(item)), item.IsDirectory),
            R("Web_CloudAccess_Error_Delete"));

    public override Task<OperationResult> RenameAsync(FileMetadata item, string newName)
        => RunWriteAsync(files => files.MoveAsync(
                RemotePath(ShareRelativeOf(item)),
                RemotePath(ShareRelativePath.Combine(ShareRelativePath.GetParent(ShareRelativeOf(item)), newName))),
            R("Web_CloudAccess_Error_Rename"));

    public override Task<OperationResult> MoveAsync(FileMetadata item, string destinationPath)
        => RunWriteAsync(files => files.MoveAsync(
                RemotePath(ShareRelativeOf(item)),
                RemotePath(ShareRelativePath.Combine(destinationPath, item.Name))),
            R("Web_CloudAccess_Error_Move"));

    public override Task<List<FileMetadata>> ListDirectoryAsync(string directoryPath)
        => ListCoreAsync(ShareRelativeOf(new FileMetadata { Path = directoryPath }));

    public override async Task CopyAsync(FileMetadata item, string targetPath, CancellationToken cancellationToken)
    {
        var result = await RunWriteAsync(files => CopyRemoteItemAsync(files,
            RemotePath(ShareRelativeOf(item)), RemotePath(targetPath), item.IsDirectory, cancellationToken),
            R("Web_CloudAccess_Error_RemoteCopy"));
        if (!result.Success) throw new IOException(result.Error);
    }

    public override async Task<(byte[] Data, string ContentType, PreviewKind Kind)?> ReadFileForPreviewAsync(FileMetadata file)
    {
        if (_remoteFiles is null || file.Size > GetMaxPreviewSizeBytes()) return null;
        await using var memory = new MemoryStream(file.Size is > 0 and <= int.MaxValue ? (int)file.Size : 0);
        await using var source = await _remoteFiles.OpenReadAsync(RemotePath(ShareRelativeOf(file)));
        await source.CopyToAsync(memory);
        return (memory.ToArray(), FileHelper.GetContentType(file.Name), FileHelper.GetPreviewKind(file.Name));
    }

    /// <summary>
    /// Remote directory listings do not carry an aggregate size. Walk the selected
    /// subtree on demand for Properties, keeping normal browsing metadata-only.
    /// </summary>
    public override async Task<long?> CalculateDirectorySizeAsync(
        FileMetadata directory, CancellationToken cancellationToken = default)
    {
        if (!directory.IsDirectory || _remoteFiles is null || _share is null)
            return null;

        try
        {
            var total = await CalculateDirectorySizeCoreAsync(ShareRelativeOf(directory), cancellationToken);
            return total;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to calculate size for Cloud Access directory {Path}", directory.Path);
            return null;
        }
    }

    public override async Task<OperationResult> UploadFileAsync(
        string fileName, Stream fileStream, CancellationToken cancellationToken = default)
        => await RunWriteAsync(files => files.WriteAsync(
                RemotePath(ShareRelativePath.Combine(CurrentPath, fileName)),
                fileStream,
                overwrite: true,
                cancellationToken),
            R("Web_CloudAccess_Error_Upload"));

    public async Task<string?> GetDownloadUrlAsync(FileMetadata file)
    {
        if (_share is null || file.IsDirectory) return null;
        var actor = await GetActorAsync();
        if (actor is null || !await _authorization.CanAccessAsync(actor, _share)) return null;
        var path = ShareRelativeOf(file);
        if (!ShareRelativePath.TryNormalizeStrict(path, out path, allowRoot: false)) return null;
        var ticket = _downloadTickets.Issue(new CloudAccessDownloadTicket(
            _share.Id, actor.User.Id, path, file.Name));
        return "/api/cloud-access/download?ticket=" + Uri.EscapeDataString(ticket);
    }

    private async Task<List<FileMetadata>> ListCoreAsync(string relativePath)
    {
        var files = _remoteFiles ?? throw new InvalidOperationException("The storage connection is not initialized.");
        var shareId = _share?.Id ?? throw new InvalidOperationException("No virtual share is loaded.");
        return await _directoryCache.GetOrCreateAsync(shareId, relativePath, async () =>
        {
            var remoteItems = await files.ListAsync(RemotePath(relativePath));
            return remoteItems.Select(item => new FileMetadata
            {
                Id = StableGuid(item.StableId ?? item.Path),
                ShareId = shareId,
                Path = StripRoot(item.Path),
                Name = item.Name,
                IsDirectory = item.IsDirectory,
                Size = item.IsDirectory ? 0 : item.Size ?? 0,
                CreatedAt = item.ModifiedAtUtc ?? DateTime.UnixEpoch,
                ModifiedAt = item.ModifiedAtUtc ?? DateTime.UnixEpoch
            }).ToList();
        });
    }

    private async Task<long> CalculateDirectorySizeCoreAsync(string relativePath, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var item in await _remoteFiles!.ListAsync(RemotePath(relativePath), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = checked(total + (item.IsDirectory
                ? await CalculateDirectorySizeCoreAsync(StripRoot(item.Path), cancellationToken)
                : item.Size ?? 0));
        }
        return total;
    }

    private async Task<OperationResult> RunWriteAsync(Func<IRemoteFileStore, Task> action, string userError)
    {
        if (_share?.IsReadOnly != false || _remoteFiles is null)
            return OperationResult.Fail(R("Web_CloudAccess_Error_ReadOnly"));
        try
        {
            await action(_remoteFiles);
            _directoryCache.InvalidateShare(_share.Id);
            return OperationResult.Ok();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Provider write operation failed for virtual share {ShareId}", _share.Id);
            return OperationResult.Fail(WriteError(exception, userError));
        }
    }

    internal static string WriteError(Exception exception, string fallback)
        => exception is RemoteStorageAccessDeniedException
            ? R("Web_ExternalStorage_RemoteWriteDenied")
            : fallback;

    private string RemotePath(string relativePath)
    {
        if (_share is null) throw new InvalidOperationException("No virtual share is loaded.");
        if (!ShareRelativePath.TryNormalizeStrict(relativePath ?? string.Empty, out var normalized))
            throw new UnauthorizedAccessException("The remote path is invalid.");
        return ShareRelativePath.Combine(_share.RemoteRootPath, normalized);
    }

    private string StripRoot(string remotePath)
    {
        var root = ShareRelativePath.Normalize(_share?.RemoteRootPath);
        var path = ShareRelativePath.Normalize(remotePath);
        if (root.Length == 0) return path;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var prefix = root + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Microsoft returned an item outside the configured root.");
        return path[prefix.Length..];
    }

    private async Task ReplaceConnectionAsync(StorageConnection record)
    {
        if (_connectionRecord?.Id == record.Id && _session is not null) return;
        if (_session is not null) await _session.DisposeAsync();
        _session = await _providers.GetRequired(record.ProviderId).OpenSessionAsync(record);
        _remoteFiles = _session.RemoteFiles
                       ?? throw new NotSupportedException("The provider does not expose virtual-share file access.");
        _connectionRecord = record;
    }

    private static BrowserCapabilities BuildCapabilities(
        StorageProviderCapabilities capabilities,
        bool readOnly)
    {
        if (readOnly) return ReadOnlyCapabilities;
        return new BrowserCapabilities
        {
            CanOpen = capabilities.HasFlag(StorageProviderCapabilities.Read),
            CanUpload = capabilities.HasFlag(StorageProviderCapabilities.Write),
            CanCreateDirectory = capabilities.HasFlag(StorageProviderCapabilities.CreateDirectory),
            CanRename = capabilities.HasFlag(StorageProviderCapabilities.Rename),
            CanDelete = capabilities.HasFlag(StorageProviderCapabilities.Delete),
            CanMove = capabilities.HasFlag(StorageProviderCapabilities.Move),
            CanCopy = capabilities.HasFlag(StorageProviderCapabilities.Read)
                      && capabilities.HasFlag(StorageProviderCapabilities.Write),
            CanCopyToLocal = capabilities.HasFlag(StorageProviderCapabilities.Read),
            HasProperties = true
        };
    }

    private static async Task CopyRemoteItemAsync(
        IRemoteFileStore files,
        string sourcePath,
        string destinationPath,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        if (!isDirectory)
        {
            await using var content = await files.OpenReadAsync(sourcePath, cancellationToken);
            await files.WriteAsync(destinationPath, content, overwrite: false, cancellationToken);
            return;
        }
        await files.CreateDirectoryAsync(destinationPath, cancellationToken);
        foreach (var child in await files.ListAsync(sourcePath, cancellationToken))
            await CopyRemoteItemAsync(files, child.Path,
                ShareRelativePath.Combine(destinationPath, child.Name), child.IsDirectory, cancellationToken);
    }

    private async Task<Kaimo_File_Server.Core.Domain.Identity.UserContext?> GetActorAsync()
    {
        var state = await _authenticationState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(username)
            ? null
            : await _userContextFactory.CreateByUsernameAsync(username);
    }

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Builds a provider-neutral, user-facing explanation for a virtual share whose
    /// backing <see cref="StorageConnection"/> is not <see cref="StorageConnectionState.Ready"/>.
    /// The message names the affected connection and states the concrete reason (disabled,
    /// awaiting authorization, or otherwise unavailable) so the reader can tell an
    /// administrator exactly which connection needs attention.
    /// </summary>
    private static string ConnectionUnavailableMessage(StorageConnection connection)
    {
        var key = connection.State switch
        {
            StorageConnectionState.Disabled => "Web_CloudAccess_Error_ConnectionDisabled",
            StorageConnectionState.PendingAuthorization or StorageConnectionState.NeedsReauthorization
                => "Web_CloudAccess_Error_ConnectionNeedsAuthorization",
            _ => "Web_CloudAccess_Error_ConnectionUnavailable"
        };
        var label = string.IsNullOrWhiteSpace(connection.Name) ? connection.ProviderId : connection.Name;
        return string.Format(R(key), label);
    }

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    public async ValueTask DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
    }
}
