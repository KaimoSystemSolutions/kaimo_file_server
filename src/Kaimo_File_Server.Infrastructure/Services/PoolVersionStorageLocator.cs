using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Infrastructure.Services;

/// <summary>
/// Stores version blobs in <c>&lt;pool&gt;/.kaimo-versions</c> of the pool that holds the
/// share. Shares are always direct child folders of a pool, so this folder is outside
/// every share (not visible via SMB/WebDAV/web) and, being a dot folder, never listed
/// as an unreferenced share.
/// </summary>
public sealed class PoolVersionStorageLocator : IVersionStorageLocator
{
    public const string FolderName = ".kaimo-versions";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IShareRepository _shares;
    private readonly IReadOnlyList<string> _pools;

    public PoolVersionStorageLocator(IShareRepository shares, IReadOnlyList<string> poolStoragePaths)
    {
        _shares = shares;
        _pools = poolStoragePaths.Select(Normalize).ToList();
        AllRoots = _pools.Select(RootOf).ToList();
    }

    public IReadOnlyList<string> AllRoots { get; }

    public async Task<string?> GetRootAsync(Guid shareId)
    {
        var share = await _shares.GetByIdAsync(shareId);
        return share is null || string.IsNullOrWhiteSpace(share.Path)
            ? null
            : RootForSharePath(share.Path);
    }

    /// <summary>
    /// The version root of the pool containing <paramref name="sharePath"/>. A share that
    /// IS a pool root gets none, since the version folder would then be inside the share.
    /// </summary>
    public string? RootForSharePath(string sharePath)
    {
        var path = Normalize(sharePath);
        var pool = _pools
            .Where(p => path.StartsWith(p + Path.DirectorySeparatorChar, PathComparison))
            .MaxBy(p => p.Length);
        return pool is null ? null : RootOf(pool);
    }

    private static string RootOf(string pool) => Path.Combine(pool, FolderName);

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
