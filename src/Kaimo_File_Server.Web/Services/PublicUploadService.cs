using System.Collections.Concurrent;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
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
    Core.Security.DemoModeOptions? demo = null)
{
    // Target paths currently being written by any visitor, so two parallel uploads of the same
    // name never pick the same free name. Keyed by share id + share-relative path.
    // ponytail: in-process only; an SMB write racing the existence check could still collide.
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether uploads are currently possible at all (settings switch, demo mode).</summary>
    public async Task<bool> IsUploadEnabledAsync()
        => demo?.ReadOnly != true && (await shareLinks.GetSettingsAsync()).AllowUploadLinks;

    /// <summary>
    /// Uploads one file. <paramref name="openStream"/> is called with the maximum number of bytes
    /// the stream may deliver (the reserved size) and must enforce it — e.g.
    /// <c>IBrowserFile.OpenReadStream(maxAllowedSize)</c> — so a client that lies about the size
    /// cannot exceed its reservation.
    /// </summary>
    public async Task<PublicUploadResult> UploadAsync(
        ShareLink link, string fileName, long size, Func<long, Stream> openStream, CancellationToken ct = default)
    {
        // The read-only demo never accepts writes, anonymous ones least of all.
        if (demo?.ReadOnly == true || link.Kind != ShareLinkKind.Upload || string.IsNullOrEmpty(link.Token))
            return new(PublicUploadStatus.Unavailable);

        var settings = await shareLinks.GetSettingsAsync();
        if (!settings.AllowUploadLinks)
            return new(PublicUploadStatus.Unavailable);

        // Only the leaf name counts; a visitor can never choose a sub-path.
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();
        if (name.Length == 0 || name is "." or ".." || !WindowsFileNameHelper.IsValid(name))
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

        var fs = fileServices.CreateForShare(share.Id, share.Path);
        var folder = ShareRelativePath.Normalize(link.RootRelativePath);
        string? target = null;
        try
        {
            target = await ClaimFreeNameAsync(share.Id, fs, folder, name, creator);

            await using (var stream = RateLimitedStream.Wrap(openStream(size), link.MaxBytesPerSecond))
                await fs.WriteFileAsync(target, stream, creator, ct);

            var stored = ShareRelativePath.GetFileName(target);
            logger.LogInformation(
                "Public upload via link {LinkId}: '{Path}' ({Size} bytes) on share {ShareId} as {User}",
                link.Id, target, size, share.Id, creator.User.Username);
            return new(PublicUploadStatus.Uploaded, stored);
        }
        catch (Exception ex)
        {
            // The storage layer publishes only complete files, so nothing partial remains;
            // just give the reservation back.
            await links.ReleaseUploadAsync(link.Id, size);

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

    private static string Combine(string folder, string leaf)
        => ShareRelativePath.Normalize(folder.Length == 0 ? leaf : $"{folder}/{leaf}");

    private static string Key(Guid shareId, string path) => shareId.ToString("N") + "|" + path;
}
