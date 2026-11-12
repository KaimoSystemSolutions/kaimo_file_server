using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories.ExternalStorage;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Opens the provider session behind a virtual share, and holds the path and metadata mapping
/// every consumer of that session has to agree on (the directory cache is shared between them).
/// </summary>
public sealed class CloudAccessRemoteSessionOpener(
    ICloudAccessRepository repository,
    IStorageConnectionRepository connections,
    IStorageConnectionProviderCatalog providers)
{
    /// <summary>
    /// Loads the share and opens a session on its connection. The caller owns the session.
    /// Throws <see cref="RemoteShareUnavailableException"/> when the connection cannot be used.
    /// </summary>
    public async Task<(CloudAccessShare Share, IStorageSession Session)> OpenAsync(
        Guid shareId, CancellationToken cancellationToken = default)
    {
        var share = await repository.GetShareAsync(shareId, cancellationToken)
                    ?? throw new FileNotFoundException("The virtual share does not exist.");
        var connection = await connections.GetAsync(share.ConnectionId, cancellationToken);
        if (connection?.State != StorageConnectionState.Ready)
            throw new RemoteShareUnavailableException("The storage connection of this virtual share is not ready.");

        var session = await providers.GetRequired(connection.ProviderId).OpenSessionAsync(connection, cancellationToken);
        if (session.RemoteFiles is not null) return (share, session);

        await session.DisposeAsync();
        throw new RemoteShareUnavailableException("The provider does not expose virtual-share file access.");
    }

    /// <summary>The provider path of a share-relative path; traversal is refused.</summary>
    internal static string RemotePath(CloudAccessShare share, string relativePath)
    {
        if (!ShareRelativePath.TryNormalizeStrict(relativePath ?? string.Empty, out var normalized))
            throw new UnauthorizedAccessException("The remote path is invalid.");
        return ShareRelativePath.Combine(share.RemoteRootPath, normalized);
    }

    /// <summary>The share-relative path of a provider path; an item outside the root is refused.</summary>
    internal static string StripRoot(CloudAccessShare? share, string remotePath)
    {
        var root = ShareRelativePath.Normalize(share?.RemoteRootPath);
        var path = ShareRelativePath.Normalize(remotePath);
        if (root.Length == 0) return path;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var prefix = root + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The provider returned an item outside the configured root.");
        return path[prefix.Length..];
    }

    /// <summary>A provider item as the directory cache stores it (share-relative path).</summary>
    internal static FileMetadata ToMetadata(CloudAccessShare share, RemoteStorageItem item) => new()
    {
        Id = StableGuid(item.StableId ?? item.Path),
        ShareId = share.Id,
        Path = StripRoot(share, item.Path),
        Name = item.Name,
        IsDirectory = item.IsDirectory,
        Size = item.IsDirectory ? 0 : item.Size ?? 0,
        CreatedAt = item.CreatedAtUtc ?? item.ModifiedAtUtc ?? DateTime.UnixEpoch,
        ModifiedAt = item.ModifiedAtUtc ?? DateTime.UnixEpoch
    };

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
