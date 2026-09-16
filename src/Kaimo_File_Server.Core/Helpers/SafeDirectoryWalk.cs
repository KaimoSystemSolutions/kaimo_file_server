namespace Kaimo_File_Server.Core.Helpers;

/// <summary>A file yielded by <see cref="SafeDirectoryWalk.EnumerateFiles"/>.</summary>
public readonly record struct WalkedFile(string FullPath, long Length);

/// <summary>Why a directory or entry was skipped rather than descended into.</summary>
public enum WalkSkipReason
{
    /// <summary>A symbolic link / reparse point — never followed, so a link cycle
    /// cannot loop and a link out of the tree cannot leak its target.</summary>
    ReparsePoint,
    /// <summary>The configured maximum depth was reached.</summary>
    DepthLimit,
    /// <summary>The configured maximum number of yielded entries was reached.</summary>
    EntryLimit,
    /// <summary>The directory could not be read (missing or access denied).</summary>
    Inaccessible
}

/// <summary>A single skipped directory/entry and the reason it was skipped.</summary>
public readonly record struct WalkSkip(string FullPath, WalkSkipReason Reason);

/// <summary>The outcome of a walk: what was skipped and whether it stopped early.</summary>
public readonly record struct WalkResult(IReadOnlyList<WalkSkip> Skips, bool Truncated);

/// <summary>
/// A depth- and cycle-safe directory walker shared by every recursive filesystem
/// traversal (directory sizing, archive building, index rebuilds).
///
/// A user can create a symlink cycle over SMB. A naive recursion — or
/// <c>Directory.EnumerateDirectories</c> with <c>SearchOption.AllDirectories</c>,
/// which follows links — turns that cycle into either a <see cref="StackOverflowException"/>
/// (which NO catch block can handle: instant process death) or unbounded work. This
/// walker makes both impossible:
///
/// <list type="bullet">
/// <item>It is <b>iterative</b>, driven by an explicit stack, so recursion depth is
/// constant regardless of tree shape — a StackOverflow cannot occur.</item>
/// <item>It <b>never follows a reparse point / symbolic link</b> (detected via
/// <see cref="FileSystemInfo.LinkTarget"/>, matching the one correct precedent in the
/// codebase), so a link cycle terminates and a link pointing outside the root is not
/// descended.</item>
/// <item>It enforces a <b>depth cap</b> and an optional <b>entry cap</b>, and tracks a
/// <b>visited set</b> of canonical paths, so even a pathological hard-link/junction
/// arrangement produces bounded work.</item>
/// <item>Directory listings are <b>materialized inside the exception boundary</b>
/// (lazy enumeration would defer I/O failures to the caller), so an inaccessible
/// subdirectory is reported as a skip, never thrown.</item>
/// </list>
/// </summary>
public static class SafeDirectoryWalk
{
    /// <summary>Default maximum directory depth below the root. Shared so every call
    /// site — including the ones that build their own <c>EnumerationOptions</c> — caps
    /// recursion at the same number.</summary>
    public const int DefaultMaxDepth = 64;

    /// <summary>
    /// Walks every regular file below <paramref name="rootFullPath"/>, invoking
    /// <paramref name="onFile"/> for each. Symlinks are never followed; depth and entry
    /// limits and access errors are reported through the returned <see cref="WalkResult"/>
    /// instead of throwing.
    /// </summary>
    public static WalkResult EnumerateFiles(
        string rootFullPath,
        Action<WalkedFile> onFile,
        int maxDepth = DefaultMaxDepth,
        long maxEntries = long.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onFile);

        var skips = new List<WalkSkip>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Dir, int Depth)>();

        var rootDir = new DirectoryInfo(rootFullPath);
        if (!rootDir.Exists)
            return new WalkResult(skips, Truncated: false);

        // A root that is itself a link: refuse to follow it, exactly as any nested link.
        if (IsLink(rootDir))
        {
            skips.Add(new WalkSkip(rootFullPath, WalkSkipReason.ReparsePoint));
            return new WalkResult(skips, Truncated: false);
        }

        stack.Push((Path.GetFullPath(rootFullPath), 0));
        long emitted = 0;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();

            if (!visited.Add(dir))
                continue;

            // Materialize children INSIDE the try — lazy enumeration would throw in the
            // caller's foreach, outside this boundary. Copy the SnapshotCacheCleanup
            // discipline verbatim.
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(dir).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skips.Add(new WalkSkip(dir, WalkSkipReason.Inaccessible));
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Never follow a link, whether it points at a file or a directory.
                if (IsLink(entry))
                {
                    skips.Add(new WalkSkip(entry.FullName, WalkSkipReason.ReparsePoint));
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    if (depth + 1 > maxDepth)
                    {
                        skips.Add(new WalkSkip(entry.FullName, WalkSkipReason.DepthLimit));
                        continue;
                    }
                    stack.Push((entry.FullName, depth + 1));
                }
                else if (entry is FileInfo file)
                {
                    if (emitted >= maxEntries)
                    {
                        skips.Add(new WalkSkip(file.FullName, WalkSkipReason.EntryLimit));
                        return new WalkResult(skips, Truncated: true);
                    }

                    long length;
                    try { length = file.Length; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skips.Add(new WalkSkip(file.FullName, WalkSkipReason.Inaccessible));
                        continue;
                    }

                    emitted++;
                    onFile(new WalkedFile(file.FullName, length));
                }
            }
        }

        return new WalkResult(skips, Truncated: false);
    }

    /// <summary>
    /// True if <paramref name="info"/> is a symbolic link / reparse point. Uses
    /// <see cref="FileSystemInfo.LinkTarget"/> rather than
    /// <see cref="FileAttributes.ReparsePoint"/>, which misses Linux symlinks — matching
    /// the one correct precedent (ShareListViewModel.CopyDirectoryAsync).
    /// </summary>
    public static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null;
}
