using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Web.Helpers;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Web.Services;

/// <summary>Outcome of one anonymous upload through an upload link.</summary>
public enum PublicUploadStatus
{
    Uploaded,
    InvalidName,
    TypeNotAllowed,
    TooLarge,
    QuotaExceeded,
    Unavailable,
    Cancelled,
    Failed,
}

/// <summary>Result of <see cref="PublicUploadService.UploadAsync"/>; <paramref name="StoredName"/> is the final file name.</summary>
public sealed record PublicUploadResult(PublicUploadStatus Status, string? StoredName = null);

/// <summary>
/// Writes files uploaded by an anonymous visitor through an upload link (<see cref="ShareLinkKind.Upload"/>).
/// Every file is validated (name, extension, size), atomically reserves one file slot and its
/// bytes of quota, and is written under the link creator's identity through the ACL-enforcing
/// <see cref="IFileService"/> into the link's folder — never overwriting an existing file.
/// </summary>
public sealed class PublicUploadService(
    IShareLinkRepository links,
    IShareRepository shares,
    IFileServiceFactory fileServices,
    ShareLinkService shareLinks,
    ILogger<PublicUploadService> logger,
    Core.Security.DemoModeOptions? demo = null,
    Core.Services.Notifications.INotificationPublisher? notifications = null)
{
    // Target paths currently being written by any visitor, so two parallel uploads of the same
    // name never pick the same free name. Keyed by share id + share-relative path.
    // ponytail: in-process only; an SMB write racing the existence check could still collide.
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How deep an uploaded folder may nest below the link folder.</summary>
    public const int MaxFolderDepth = 16;

    /// <summary>Whether uploads are currently possible at all (settings switch, demo mode).</summary>
    public async Task<bool> IsUploadEnabledAsync()
        => demo?.ReadOnly != true && (await shareLinks.GetSettingsAsync()).AllowUploadLinks;

    /// <summary>
    /// Uploads one file. <paramref name="openStream"/> is called with the maximum number of bytes
    /// the stream may deliver (the reserved size) and must enforce it — e.g.
    /// <c>IBrowserFile.OpenReadStream(maxAllowedSize)</c> — so a client that lies about the size
    /// cannot exceed its reservation.
    /// <paramref name="relativeDir"/> is the file's folder within an uploaded folder
    /// ("Photos/2024"); it is created below the link folder and existing folders are merged into.
    /// </summary>
    public async Task<PublicUploadResult> UploadAsync(
        ShareLink link, string fileName, long size, Func<long, Stream> openStream, CancellationToken ct = default,
        string? relativeDir = null)
    {
        // The read-only demo never accepts writes, anonymous ones least of all.
        if (demo?.ReadOnly == true || link.Kind != ShareLinkKind.Upload || string.IsNullOrEmpty(link.Token))
            return new(PublicUploadStatus.Unavailable);

        var settings = await shareLinks.GetSettingsAsync();
        if (!settings.AllowUploadLinks)
            return new(PublicUploadStatus.Unavailable);

        // Only the leaf name of fileName counts. A visitor can choose a sub-path only through
        // relativeDir, which rejects "." and ".." — so it always stays inside the link folder.
        // Names Windows cannot use are renamed ("a:b" → "a_b").
        // Not Path.GetFileName: on Windows it treats "a:" in "a:b.txt" as a drive and drops it.
        var rawName = (fileName ?? string.Empty).Replace('\\', '/');
        var name = rawName[(rawName.LastIndexOf('/') + 1)..].Trim();
        if (name.Length == 0 || name is "." or "..")
            return new(PublicUploadStatus.InvalidName);
        name = WindowsFileNameHelper.Sanitize(name);
        if (!UploadFolderPath.TryNormalize(relativeDir, out var subDir, MaxFolderDepth))
            return new(PublicUploadStatus.InvalidName);

        if (!link.IsExtensionAllowed(name))
            return new(PublicUploadStatus.TypeNotAllowed);

        if (size < 0 || size > settings.EffectiveMaxFileSize(link))
            return new(PublicUploadStatus.TooLarge);

        var share = await shares.GetByIdAsync(link.ShareId);
        var creator = await shareLinks.ResolveCreatorAsync(link);
        if (share is null || !share.IsEnabled || creator is null)
            return new(PublicUploadStatus.Unavailable);

        // Atomic policy gate: enabled, window, file count and byte quota — plus the reservation.
        var reserved = await links.TryReserveUploadAsync(link.Token, size);
        if (reserved is null)
        {
            var current = await links.GetByTokenAsync(link.Token);
            return new(current is not null && current.IsWindowOpen(DateTime.UtcNow)
                ? PublicUploadStatus.QuotaExceeded
                : PublicUploadStatus.Unavailable);
        }

        var fs = fileServices.CreateForShare(share);
        var linkFolder = ShareRelativePath.Normalize(link.RootRelativePath);
        var folder = subDir.Length == 0 ? linkFolder : Combine(linkFolder, subDir);
        string? target = null;
        List<string> createdFolders = [];
        try
        {
            // Folders only appear together with a file: empty folders of an upload are not
            // created, and ones created here are removed again if the file fails.
            createdFolders = await UploadFolderPath.EnsureFoldersAsync(fs, linkFolder, subDir, creator, ct);

            target = await ClaimFreeNameAsync(share.Id, fs, folder, name, creator);

            await using (var stream = RateLimitedStream.Wrap(openStream(size), link.MaxBytesPerSecond))
                await fs.WriteFileAsync(target, stream, creator, ct);

            var stored = subDir.Length == 0
                ? ShareRelativePath.GetFileName(target)
                : $"{subDir}/{ShareRelativePath.GetFileName(target)}";
            logger.LogInformation(
                "Public upload via link {LinkId}: '{Path}' ({Size} bytes) on share {ShareId} as {User}",
                link.Id, target, size, share.Id, creator.User.Username);
            if (notifications is not null)
                await notifications.PublishAsync(Core.Services.Notifications.NotificationEvents.UploadReceived(
                    link.Id, link.DisplayName, link.CreatedByUserId, stored, size), CancellationToken.None);
            return new(PublicUploadStatus.Uploaded, stored);
        }
        catch (Exception ex)
        {
            // The storage layer publishes only complete files, so nothing partial remains;
            // just give the reservation back. Folders this upload created would otherwise let a
            // visitor create folders for free by cancelling uploads.
            await links.ReleaseUploadAsync(link.Id, size);
            if (target is not null)
                InFlight.TryRemove(Key(share.Id, target), out _);
            await RemoveEmptyFoldersAsync(share.Id, fs, createdFolders, creator);

            if (ex is OperationCanceledException)
                return new(PublicUploadStatus.Cancelled);

            logger.LogWarning(ex, "Public upload via link {LinkId} failed for '{Name}'", link.Id, name);
            return new(ex is IOException { Message: var m } && m.Contains("exceeds", StringComparison.OrdinalIgnoreCase)
                ? PublicUploadStatus.TooLarge
                : PublicUploadStatus.Failed);
        }
        finally
        {
            if (target is not null)
                InFlight.TryRemove(Key(share.Id, target), out _);
        }
    }

    /// <summary>
    /// Picks "name.ext", else "name (2).ext", … — the first name that neither exists in the folder
    /// nor is being written by another upload — and claims it. When the folder cannot be listed
    /// (write-only drop box), a unique random suffix guarantees nothing is overwritten.
    /// </summary>
    private static async Task<string> ClaimFreeNameAsync(
        Guid shareId, IFileService fs, string folder, string name, Core.Domain.Identity.UserContext creator)
    {
        HashSet<string>? existing;
        try
        {
            existing = (await fs.ListAsync(folder, creator))
                .Select(f => f.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            existing = null;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);

        if (existing is not null)
        {
            for (var n = 1; n < 10000; n++)
            {
                var candidate = n == 1 ? name : $"{stem} ({n}){ext}";
                if (existing.Contains(candidate)) continue;
                var path = Combine(folder, candidate);
                if (InFlight.TryAdd(Key(shareId, path), 0)) return path;
            }
        }

        var fallback = Combine(folder, $"{stem} ({Guid.NewGuid():N}){ext}");
        InFlight.TryAdd(Key(shareId, fallback), 0);
        return fallback;
    }

    // Best effort, deepest first. The file-service delete is recursive, so a folder is only
    // removed while it is empty on disk and no other upload is writing into it.
    private static async Task RemoveEmptyFoldersAsync(
        Guid shareId, IFileService fs, List<string> folders, Core.Domain.Identity.UserContext creator)
    {
        for (var i = folders.Count - 1; i >= 0; i--)
        {
            var folder = folders[i];
            var inFlightPrefix = Key(shareId, folder + "/");
            try
            {
                if (InFlight.Keys.Any(k => k.StartsWith(inFlightPrefix, StringComparison.OrdinalIgnoreCase)) ||
                    Directory.EnumerateFileSystemEntries(fs.ToAbsolutePath(folder)).Any())
                    return; // its parents are not empty either

                await fs.DeleteFileAsync(folder, creator, isRecycleEnabled: false);
            }
            catch
            {
                return;
            }
        }
    }

    private static string Combine(string folder, string leaf)
        => ShareRelativePath.Normalize(folder.Length == 0 ? leaf : $"{folder}/{leaf}");

    private static string Key(Guid shareId, string path) => shareId.ToString("N") + "|" + path;
}
