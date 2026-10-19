using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Web.Helpers;
using Kaimo_File_Server.Web.Services;
using System.Security.Cryptography;

namespace Kaimo_File_Server.Web.Components.ViewModels;

public partial class FileBrowserViewModel
{
    // Inline preview buffers the whole file into a server-side byte[]; cap it so a
    // huge file can never blow up server memory. Larger files fall back to download.
    private long MaxPreviewSizeBytes => 25L * 1024 * 1024; // 25 MB

    public async Task<(byte[] Data, string ContentType, PreviewKind Kind)?> ReadFileForPreviewAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null) return null;

        // Never buffer an oversized file into memory — the caller shows a download
        // fallback for anything above the cap.
        if (file.Size > MaxPreviewSizeBytes) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        var relativePath = file.Path;
        if (relativePath.StartsWith(CurrentShare.Path))
            relativePath = relativePath[CurrentShare.Path.Length..].TrimStart('/');

        await using var stream = await _fileService.ReadFileAsync(relativePath, userContext);
        var data = await ReadAllBytesAsync(stream);

        var contentType = FileHelper.GetContentType(file.Name);
        var kind = FileHelper.GetPreviewKind(file.Name);

        return (data, contentType, kind);
    }

    /// <summary>Maximum file size that can be previewed inline; larger files download instead.</summary>
    public long GetMaxPreviewSizeBytes() => MaxPreviewSizeBytes;

    /// <summary>
    /// Issues a one-time URL that streams the file straight to the browser via
    /// <c>FileDownloadController</c>, so a download never buffers the file into the
    /// preview first — and files above the inline-preview cap can still be downloaded.
    /// Returns null when no file is addressable or the user is not authenticated;
    /// the controller re-checks ACL access before streaming.
    /// </summary>
    public virtual async Task<string?> GetDownloadUrlAsync(FileMetadata file)
    {
        if (_fileService is null || CurrentShare is null || file.IsDirectory) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        if (!ShareRelativePath.TryNormalizeStrict(ToShareRelative(file.Path), out var relative, allowRoot: false))
            return null;

        var ticket = _downloadTickets.Issue(new FileDownloadTicket(
            CurrentShare.Id, userContext.User.Id, relative, file.Name));
        return "/api/files/download?ticket=" + Uri.EscapeDataString(ticket);
    }

    /// <summary>
    /// Builds a download URL for a whole selection: one plain file streams directly, a folder
    /// or a multi-item selection streams as a ZIP via <c>ShareZipDownloadController</c>.
    /// </summary>
    public virtual async Task<string?> GetSelectionDownloadUrlAsync(IReadOnlyList<FileMetadata> items)
    {
        if (_fileService is null || CurrentShare is null || items.Count == 0) return null;

        // A single plain file needs no archive.
        if (items.Count == 1 && !items[0].IsDirectory)
            return await GetDownloadUrlAsync(items[0]);

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        var relatives = new List<string>();
        foreach (var item in items)
            if (ShareRelativePath.TryNormalizeStrict(ToShareRelative(item.Path), out var rel, allowRoot: false))
                relatives.Add(rel);
        if (relatives.Count == 0) return null;

        var archiveName = items.Count == 1
            ? items[0].Name + ".zip"
            : CurrentShare.Name + ".zip";

        var ticket = _zipTickets.Issue(new ZipDownloadTicket(
            CurrentShare.Id, userContext.User.Id, relatives.ToArray(), archiveName));
        return "/api/files/download-zip?ticket=" + Uri.EscapeDataString(ticket);
    }

    /// <summary>
    /// Hashes a file's content with the requested algorithm, streaming it through the
    /// hasher so files of any size stay off the heap. Returns the lowercase hex digest.
    /// </summary>
    public async Task<string?> ComputeFileHashAsync(
        FileMetadata file, HashAlgorithmName algorithm, CancellationToken cancellationToken = default)
    {
        if (_fileService is null || CurrentShare is null || file.IsDirectory) return null;

        var userContext = await GetCurrentUserContextAsync();
        if (userContext is null) return null;

        await using var stream = await _fileService.ReadFileAsync(ToShareRelative(file.Path), userContext);
        using var hasher = IncrementalHash.CreateHash(algorithm);

        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            hasher.AppendData(buffer, 0, read);

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }
}
