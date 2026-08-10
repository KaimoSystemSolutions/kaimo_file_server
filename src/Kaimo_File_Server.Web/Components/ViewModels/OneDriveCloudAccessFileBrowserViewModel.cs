using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Clouds;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public sealed class OneDriveCloudAccessFileBrowserViewModel : RemoteFileBrowserViewModelBase, IAsyncDisposable
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
    private readonly ICloudAccessCredentialProtector _protector;
    private readonly CloudAccessAuthorizationService _authorization;
    private readonly IUserContextFactory _userContextFactory;
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OneDriveCloudAccessFileBrowserViewModel> _logger;
    private readonly CloudAccessDownloadTicketStore _downloadTickets;
    private readonly CloudAccessDirectoryCache _directoryCache;
    private CloudAccessShare? _share;
    private CloudAccessConnection? _connectionRecord;
    private OneDriveConnection? _connection;
    private Dictionary<string, string>? _credentials;
    private DateTimeOffset _rootRevalidationAt;

    public OneDriveCloudAccessFileBrowserViewModel(
        ICloudAccessRepository repository,
        ICloudAccessCredentialProtector protector,
        CloudAccessAuthorizationService authorization,
        IUserContextFactory userContextFactory,
        AuthenticationStateProvider authenticationState,
        IHttpClientFactory httpClientFactory,
        CloudAccessDownloadTicketStore downloadTickets,
        CloudAccessDirectoryCache directoryCache,
        ILogger<OneDriveCloudAccessFileBrowserViewModel> logger)
        : base(WritableCapabilities)
    {
        _repository = repository;
        _protector = protector;
        _authorization = authorization;
        _userContextFactory = userContextFactory;
        _authenticationState = authenticationState;
        _httpClientFactory = httpClientFactory;
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

            var connectionRecord = await _repository.GetConnectionAsync(share.ConnectionId)
                                   ?? throw new InvalidOperationException("The provider connection is missing.");
            if (connectionRecord.State != CloudAccessConnectionState.Ready
                || !string.Equals(connectionRecord.Provider, "onedrive", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(connectionRecord.ProtectedCredentials))
                throw new InvalidOperationException("The OneDrive connection is not ready.");

            var connectionChanged = _connectionRecord?.Id != connectionRecord.Id;
            var shareChanged = _share?.Id != share.Id;
            await ReplaceConnectionAsync(connectionRecord);
            _share = share;
            if (!string.IsNullOrWhiteSpace(share.RemoteRootItemId)
                && (connectionChanged || shareChanged || DateTimeOffset.UtcNow >= _rootRevalidationAt))
            {
                var currentRoot = await _connection!.ResolveFolderByIdAsync(share.RemoteRootItemId);
                if (!string.Equals(currentRoot.Path, share.RemoteRootPath, StringComparison.Ordinal))
                {
                    share.RemoteRootPath = currentRoot.Path;
                    await _repository.UpsertShareAsync(share);
                    _directoryCache.InvalidateShare(share.Id);
                }
                _rootRevalidationAt = DateTimeOffset.UtcNow + await _directoryCache.GetTimeToLiveAsync();
            }
            Capabilities = share.IsReadOnly ? ReadOnlyCapabilities : WritableCapabilities;
            var items = await ListCoreAsync(relativePath);
            await PersistRotatedCredentialsAsync();
            CompleteLoad(
                new BrowserShareInfo(share.Id, share.Name, BrowserShareKind.Remote, "onedrive"),
                relativePath,
                items);
        }
        catch (UnauthorizedAccessException)
        {
            FailLoad(R("Web_CloudAccess_Error_AccessDenied"));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to load OneDrive Cloud Access share {ShareKey}", shareKey);
            FailLoad(R("Web_CloudAccess_Error_LoadShare"));
        }
    }

    public override async Task RefreshCurrentDirectoryAsync()
    {
        if (_share is null) return;
        _directoryCache.InvalidateShare(_share.Id);
        _rootRevalidationAt = DateTimeOffset.MinValue;
        await LoadShareAsync(_share.Id.ToString(), CurrentPath);
    }

    public override async Task<OperationResult> CreateFolderAsync(string folderName)
        => await RunWriteAsync(async connection =>
            await connection.CreateDirectoryAsync(RemotePath(ShareRelativePath.Combine(CurrentPath, folderName))),
            R("Web_CloudAccess_Error_CreateFolder"));

    public override async Task CreateFolderAtAsync(string path)
    {
        var result = await RunWriteAsync(async connection =>
            await connection.CreateDirectoryAsync(RemotePath(path)), R("Web_CloudAccess_Error_CreateFolder"));
        if (!result.Success) throw new IOException(result.Error);
    }

    public override Task<OperationResult> DeleteAsync(FileMetadata item)
        => RunWriteAsync(connection => connection.DeleteItemAsync(RemotePath(ShareRelativeOf(item))),
            R("Web_CloudAccess_Error_Delete"));

    public override Task<OperationResult> RenameAsync(FileMetadata item, string newName)
        => RunWriteAsync(connection => connection.MoveItemAsync(
                RemotePath(ShareRelativeOf(item)),
                RemotePath(ShareRelativePath.Combine(ShareRelativePath.GetParent(ShareRelativeOf(item)), newName))),
            R("Web_CloudAccess_Error_Rename"));

    public override Task<OperationResult> MoveAsync(FileMetadata item, string destinationPath)
        => RunWriteAsync(connection => connection.MoveItemAsync(
                RemotePath(ShareRelativeOf(item)),
                RemotePath(ShareRelativePath.Combine(destinationPath, item.Name))),
            R("Web_CloudAccess_Error_Move"));

    public override Task<List<FileMetadata>> ListDirectoryAsync(string directoryPath)
        => ListCoreAsync(ShareRelativeOf(new FileMetadata { Path = directoryPath }));

    public override async Task CopyAsync(FileMetadata item, string targetPath, CancellationToken cancellationToken)
    {
        var result = await RunWriteAsync(connection => connection.CopyItemAsync(
            RemotePath(ShareRelativeOf(item)), RemotePath(targetPath), cancellationToken),
            R("Web_CloudAccess_Error_RemoteCopy"));
        if (!result.Success) throw new IOException(result.Error);
    }

    public override async Task<(byte[] Data, string ContentType, PreviewKind Kind)?> ReadFileForPreviewAsync(FileMetadata file)
    {
        if (_connection is null || file.Size > GetMaxPreviewSizeBytes()) return null;
        await using var memory = new MemoryStream(file.Size is > 0 and <= int.MaxValue ? (int)file.Size : 0);
        await _connection.DownloadAsync(RemotePath(ShareRelativeOf(file)), memory);
        await PersistRotatedCredentialsAsync();
        return (memory.ToArray(), FileHelper.GetContentType(file.Name), FileHelper.GetPreviewKind(file.Name));
    }

    public override async Task<OperationResult> UploadFileAsync(
        string fileName, Stream fileStream, CancellationToken cancellationToken = default)
        => await RunWriteAsync(connection => connection.UploadAsync(
                RemotePath(ShareRelativePath.Combine(CurrentPath, fileName)),
                fileStream,
                DateTime.UtcNow,
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
        var connection = _connection ?? throw new InvalidOperationException("The OneDrive connection is not initialized.");
        var shareId = _share?.Id ?? throw new InvalidOperationException("No virtual share is loaded.");
        return await _directoryCache.GetOrCreateAsync(shareId, relativePath, async () =>
        {
            var remoteItems = await connection.ListDetailedAsync(RemotePath(relativePath));
            return remoteItems.Select(item => new FileMetadata
            {
                Id = StableGuid(item.ProviderId),
                ShareId = shareId,
                Path = StripRoot(item.Path),
                Name = item.Name,
                IsDirectory = item.IsDirectory,
                Size = item.IsDirectory ? 0 : item.Size,
                CreatedAt = item.CreatedAtUtc,
                ModifiedAt = item.ModifiedAtUtc
            }).ToList();
        });
    }

    private async Task<OperationResult> RunWriteAsync(Func<OneDriveConnection, Task> action, string userError)
    {
        if (_share?.IsReadOnly != false || _connection is null)
            return OperationResult.Fail(R("Web_CloudAccess_Error_ReadOnly"));
        try
        {
            await action(_connection);
            await PersistRotatedCredentialsAsync();
            _directoryCache.InvalidateShare(_share.Id);
            return OperationResult.Ok();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "OneDrive write operation failed for Cloud Access share {ShareId}", _share.Id);
            return OperationResult.Fail(userError);
        }
    }

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

    private async Task ReplaceConnectionAsync(CloudAccessConnection record)
    {
        if (_connectionRecord?.Id == record.Id && _connection is not null) return;
        if (_connection is not null) await _connection.Dispose();
        _credentials = _protector.Unprotect(record.ProtectedCredentials!);
        _connection = new OneDriveConnection(
            _credentials,
            _httpClientFactory.CreateClient("CloudAccessOneDrive"));
        _connectionRecord = record;
    }

    private async Task PersistRotatedCredentialsAsync()
    {
        if (_connection is null || _connectionRecord is null || _credentials is null
            || !_connection.HasPendingCredentialChanges) return;
        await _repository.UpdateConnectionRuntimeAsync(
            _connectionRecord.Id,
            _protector.Protect(_credentials),
            null, null,
            CloudAccessConnectionState.Ready,
            null);
        _connection.AcknowledgeCredentialChanges();
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

    private static string R(string key) => Resources.ResourceManager.GetString(key) ?? key;

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.Dispose();
    }
}
