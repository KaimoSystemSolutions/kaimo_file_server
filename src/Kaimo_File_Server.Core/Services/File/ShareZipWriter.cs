using System.IO.Compression;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Streams a set of share-relative files and/or folders into a ZIP archive written straight
/// to an output stream (e.g. an HTTP response body) — never staged to disk. Folders are added
/// recursively. Every read goes through <see cref="IFileService"/>, so ACLs are enforced for
/// the supplied <see cref="UserContext"/>; an entry the user cannot read is skipped rather than
/// aborting the whole archive.
/// </summary>
public static class ShareZipWriter
{
    public static async Task WriteAsync(
        Stream output,
        IFileService fileService,
        IReadOnlyList<string> shareRelativePaths,
        UserContext user,
        CancellationToken ct = default)
    {
        // leaveOpen: the caller owns the output stream (the HTTP response body).
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var path in shareRelativePaths)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = ShareRelativePath.Normalize(path);
            if (normalized.Length == 0) continue;

            var name = ShareRelativePath.GetFileName(normalized);
            await AddAsync(archive, fileService, normalized, name, user, ct);
        }
    }

    private static async Task AddAsync(
        ZipArchive archive, IFileService fs, string relativePath, string entryName,
        UserContext user, CancellationToken ct)
    {
        bool isDirectory;
        try
        {
            var meta = await fs.GetMetadataAsync(relativePath, user);
            isDirectory = meta.IsDirectory;
        }
        catch (UnauthorizedAccessException) { return; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }

        if (isDirectory)
            await AddDirectoryAsync(archive, fs, relativePath, entryName, user, ct);
        else
            await AddFileAsync(archive, fs, relativePath, entryName, user, ct);
    }

    private static async Task AddDirectoryAsync(
        ZipArchive archive, IFileService fs, string dirRelative, string entryPrefix,
        UserContext user, CancellationToken ct)
    {
        List<Domain.FileMetadata> children;
        try
        {
            children = await fs.ListAsync(dirRelative, user);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }

        if (children.Count == 0)
        {
            // Preserve an empty folder as an explicit directory entry.
            archive.CreateEntry(entryPrefix.TrimEnd('/') + "/");
            return;
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var childRelative = dirRelative + "/" + child.Name;
            var childEntry = entryPrefix + "/" + child.Name;
            if (child.IsDirectory)
                await AddDirectoryAsync(archive, fs, childRelative, childEntry, user, ct);
            else
                await AddFileAsync(archive, fs, childRelative, childEntry, user, ct);
        }
    }

    private static async Task AddFileAsync(
        ZipArchive archive, IFileService fs, string fileRelative, string entryName,
        UserContext user, CancellationToken ct)
    {
        Stream content;
        try
        {
            content = await fs.ReadFileAsync(fileRelative, user);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }

        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using (content)
        await using (var target = entry.Open())
            await content.CopyToAsync(target, ct);
    }
}
