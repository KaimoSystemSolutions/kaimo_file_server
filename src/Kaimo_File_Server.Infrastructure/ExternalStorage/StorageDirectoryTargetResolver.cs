using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

/// <summary>Resolves selectable remote roots through the provider session contract.</summary>
public sealed class StorageDirectoryTargetResolver(IStorageConnectionProviderCatalog providers)
    : IStorageDirectoryTargetResolver
{
    public async Task<IReadOnlyList<RemoteStorageItem>> ListDirectoriesAsync(
        StorageConnection connection,
        string path,
        CancellationToken cancellationToken = default)
    {
        var provider = GetBrowsableProvider(connection);
        await using var session = await provider.OpenSessionAsync(connection, cancellationToken);
        var files = session.RemoteFiles
                    ?? throw new NotSupportedException("The storage provider does not expose directory browsing.");
        return (await files.ListAsync(Normalize(path), cancellationToken))
            .Where(item => item.IsDirectory)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<StorageDirectoryTarget> ResolveDirectoryAsync(
        StorageConnection connection,
        string path,
        CancellationToken cancellationToken = default)
    {
        string normalized = Normalize(path);
        var provider = GetBrowsableProvider(connection);
        await using var session = await provider.OpenSessionAsync(connection, cancellationToken);
        var files = session.RemoteFiles
                    ?? throw new NotSupportedException("The storage provider does not expose directory browsing.");

        // Listing the selected directory validates both the root and nested paths.
        // A path remains a valid durable identity for providers without stable item IDs.
        await files.ListAsync(normalized, cancellationToken);
        string? stableId = null;
        if (normalized != "/")
        {
            int separator = normalized.LastIndexOf('/');
            string parent = separator <= 0 ? "/" : normalized[..separator];
            string name = normalized[(separator + 1)..];
            stableId = (await files.ListAsync(parent, cancellationToken))
                .FirstOrDefault(item => item.IsDirectory
                    && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))?.StableId;
        }
        return new StorageDirectoryTarget(normalized, stableId);
    }

    private IStorageConnectionProvider GetBrowsableProvider(StorageConnection connection)
    {
        var provider = providers.GetRequired(connection.ProviderId);
        if (!provider.Capabilities.HasFlag(StorageProviderCapabilities.Browse))
            throw new NotSupportedException("The storage provider does not support directory browsing.");
        return provider;
    }

    private static string Normalize(string? path)
    {
        string value = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new UnauthorizedAccessException("The remote path is invalid.");
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new UnauthorizedAccessException("The remote path is invalid.");
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }
}
