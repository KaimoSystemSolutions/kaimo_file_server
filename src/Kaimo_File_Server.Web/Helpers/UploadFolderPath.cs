using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Web.Helpers;

/// <summary>
/// The folder part of a browser folder upload ("Photos/2024" for "Photos/2024/a.jpg").
/// It comes from the client, so it is validated segment by segment before it is ever
/// combined with a share path: "." and ".." are rejected (no traversal out of the upload
/// target) and every other segment is renamed to a valid Windows name ("b:c" → "b_c").
/// </summary>
public static class UploadFolderPath
{
    public const int DefaultMaxDepth = 32;
    public const int MaxLength = 1024;

    /// <summary>
    /// Normalizes <paramref name="raw"/> to "a/b/c" ("" = no sub-folder, upload into the
    /// target itself). Returns false when the path is too deep or long, or contains "." or "..".
    /// </summary>
    public static bool TryNormalize(string? raw, out string relativeDir, int maxDepth = DefaultMaxDepth)
        => TryNormalize(raw, out relativeDir, out _, maxDepth);

    /// <inheritdoc cref="TryNormalize(string?, out string, int)"/>
    /// <param name="renamed">True when a segment had to be renamed to be valid on Windows.</param>
    public static bool TryNormalize(string? raw, out string relativeDir, out bool renamed, int maxDepth = DefaultMaxDepth)
    {
        relativeDir = "";
        renamed = false;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var segments = raw.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > maxDepth)
            return false;

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i] is "." or "..")
                return false;
            var sanitized = WindowsFileNameHelper.Sanitize(segments[i]);
            renamed |= sanitized != segments[i];
            segments[i] = sanitized;
        }

        var joined = string.Join('/', segments);
        if (joined.Length > MaxLength)
            return false;

        relativeDir = joined;
        return true;
    }

    /// <summary>"a/b/c" → "a", "a/b", "a/b/c": the folders to create, parents first.</summary>
    public static IEnumerable<string> Prefixes(string relativeDir)
    {
        if (string.IsNullOrEmpty(relativeDir))
            yield break;

        for (var i = relativeDir.IndexOf('/'); i >= 0; i = relativeDir.IndexOf('/', i + 1))
            yield return relativeDir[..i];
        yield return relativeDir;
    }

    /// <summary>
    /// fileUpload.js sends every distinct folder once plus one index per file (keeps the
    /// SignalR message small); this expands it back to one folder per file. An index out of
    /// range yields null, which callers treat as an invalid folder.
    /// </summary>
    public static string?[] Unpack(string[] dirs, int[] dirIndex)
        => Array.ConvertAll(dirIndex, i => (uint)i < (uint)dirs.Length ? dirs[i] : null);

    /// <summary>
    /// Creates the folders of <paramref name="relativeDir"/> below <paramref name="baseDir"/>,
    /// parents first, and returns the share paths it actually created. Existing folders are
    /// merged into (creating them again would re-stamp their owner and log a bogus change);
    /// a file in the way throws <see cref="IOException"/>.
    /// Existence is probed on disk: GetMetadataAsync needs list rights, which a write-only
    /// drop box does not grant.
    /// </summary>
    public static async Task<List<string>> EnsureFoldersAsync(
        IFileService fs, string baseDir, string relativeDir, UserContext user,
        CancellationToken cancellationToken = default)
    {
        var created = new List<string>();
        foreach (var prefix in Prefixes(relativeDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ShareRelativePath.Combine(baseDir, prefix);
            var absolute = fs.ToAbsolutePath(path);
            if (Directory.Exists(absolute))
                continue;
            if (File.Exists(absolute))
                throw new IOException($"'{path}' exists and is not a folder.");

            await fs.CreateDirectoryAsync(path, user);
            created.Add(path);
        }

        return created;
    }
}
