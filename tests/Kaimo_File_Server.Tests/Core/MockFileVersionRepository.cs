using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;
using System.IO.Compression;
using System.Security.Cryptography;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

// ═══════════════════════════════════════════════════════════════
//  In-Memory Mock Repository (no EF Core / no DB required)
// ═══════════════════════════════════════════════════════════════

public class MockFileVersionRepository : IFileVersionRepository
{
    private readonly List<FileVersion> _versions = new();

    public Task<List<FileVersion>> GetVersionsAsync(Guid shareId, string filePath)
    {
        var result = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .OrderByDescending(v => v.SnapshotTimestampUtc)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<FileVersion?> GetVersionAsync(Guid shareId, string filePath, DateTime snapshotTimestampUtc)
    {
        var result = _versions.FirstOrDefault(v =>
            v.ShareId == shareId &&
            v.FilePath == filePath &&
            v.SnapshotTimestampUtc == snapshotTimestampUtc);
        return Task.FromResult(result);
    }

    public Task<List<DateTime>> GetSnapshotTimestampsAsync(Guid shareId, string filePath)
    {
        var result = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .Select(v => v.SnapshotTimestampUtc)
            .Distinct()
            .OrderByDescending(t => t)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<List<DateTime>> GetAllSnapshotTimestampsAsync(Guid shareId, string pathPrefix = "")
    {
        var query = _versions.Where(v => v.ShareId == shareId);
        if (!string.IsNullOrEmpty(pathPrefix))
            query = query.Where(v => v.FilePath.StartsWith(pathPrefix));

        var result = query
            .Select(v => v.SnapshotTimestampUtc)
            .Distinct()
            .OrderByDescending(t => t)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<List<FileVersion>> GetLatestVersionsUnderPrefixAsync(Guid shareId, string pathPrefix, DateTime asOfUtc)
    {
        var query = _versions.Where(v => v.ShareId == shareId && v.SnapshotTimestampUtc <= asOfUtc);
        if (!string.IsNullOrEmpty(pathPrefix))
            query = query.Where(v => v.FilePath.StartsWith(pathPrefix));

        var result = query
            .GroupBy(v => v.FilePath)
            .Select(g => g.OrderByDescending(v => v.SnapshotTimestampUtc).First())
            .OrderBy(v => v.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    public virtual Task<FileVersion> CreateAsync(FileVersion version)
    {
        _versions.Add(version);
        return Task.FromResult(version);
    }

    public Task<int> GetMaxVersionNumberAsync(Guid shareId, string filePath)
    {
        var max = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .Select(v => (int?)v.VersionNumber)
            .Max();
        return Task.FromResult(max ?? 0);
    }

    public Task<List<FileVersion>> DeleteOlderThanAsync(Guid shareId, string filePath, DateTime cutoff)
    {
        var toRemove = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath && v.SnapshotTimestampUtc < cutoff)
            .ToList();
        foreach (var v in toRemove) _versions.Remove(v);
        return Task.FromResult(toRemove);
    }

    public Task<List<FileVersion>> TrimToMaxVersionsAsync(Guid shareId, string filePath, int maxCount)
    {
        var ordered = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .OrderByDescending(v => v.SnapshotTimestampUtc)
            .ToList();

        if (ordered.Count <= maxCount) return Task.FromResult(new List<FileVersion>());

        var toRemove = ordered.Skip(maxCount).ToList();
        foreach (var v in toRemove) _versions.Remove(v);
        return Task.FromResult(toRemove);
    }

    public Task<List<FileVersion>> DeletePathAsync(Guid shareId, string path)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        var removed = _versions.Where(v => v.ShareId == shareId
            && (path.Length == 0 || v.FilePath == path || v.FilePath.StartsWith(prefix))).ToList();
        foreach (var version in removed) _versions.Remove(version);
        return Task.FromResult(removed);
    }

    public Task<List<FileVersion>> RenamePathAsync(
        Guid shareId,
        string oldPath,
        string newPath,
        Guid? sambaLifecycleEventId = null)
    {
        var oldPrefix = oldPath + "/";
        var newPrefix = newPath + "/";
        var source = _versions.Where(v => v.ShareId == shareId
            && (v.FilePath == oldPath || v.FilePath.StartsWith(oldPrefix))).ToList();
        var displaced = _versions.Where(v => v.ShareId == shareId
            && (v.FilePath == newPath || v.FilePath.StartsWith(newPrefix))
            && !source.Contains(v)).ToList();
        foreach (var version in displaced) _versions.Remove(version);
        foreach (var version in source)
            version.FilePath = version.FilePath == oldPath
                ? newPath
                : newPath + version.FilePath.Substring(oldPath.Length);
        return Task.FromResult(displaced);
    }

    public Task<List<FileVersion>> DeleteShareAsync(Guid shareId)
        => DeletePathAsync(shareId, "");

    public virtual Task<bool> IsStoragePathReferencedAsync(string storagePath)
        => Task.FromResult(_versions.Any(v => v.StoragePath == storagePath));

    public Task<List<Guid>> GetShareIdsReferencingStoragePathAsync(string storagePath)
        => Task.FromResult(_versions.Where(v => v.StoragePath == storagePath)
            .Select(v => v.ShareId).Distinct().ToList());

    public Task<List<(Guid ShareId, string StoragePath)>> GetAllBlobReferencesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(_versions.Select(v => (v.ShareId, v.StoragePath)).Distinct().ToList());

    public Task<List<FileVersion>> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids)
    {
        var removed = _versions.Where(v => ids.Contains(v.Id)).ToList();
        foreach (var version in removed) _versions.Remove(version);
        return Task.FromResult(removed);
    }

    public Task<List<Guid>> GetShareIdsWithVersionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_versions.Select(v => v.ShareId).Distinct().ToList());

    public Task<List<string>> GetVersionedPathsPageAsync(
        Guid shareId, string? afterPath, int take, CancellationToken cancellationToken = default)
        => Task.FromResult(_versions
            .Where(v => v.ShareId == shareId && (afterPath is null || string.CompareOrdinal(v.FilePath, afterPath) > 0))
            .Select(v => v.FilePath)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Take(take)
            .ToList());

    public Task<List<FileVersion>> GetBudgetPruneCandidatesAsync(
        IReadOnlyCollection<Guid> shareIds, int take, CancellationToken cancellationToken = default)
        => Task.FromResult(_versions
            .Where(v => shareIds.Contains(v.ShareId)
                        && _versions.Any(n => n.ShareId == v.ShareId && n.FilePath == v.FilePath
                                              && n.SnapshotTimestampUtc > v.SnapshotTimestampUtc))
            .OrderBy(v => v.SnapshotTimestampUtc)
            .Take(take)
            .ToList());

    public Task<bool> ExistsWithHashAsync(Guid shareId, string filePath, string contentHash)
    {
        var latestHash = _versions
            .Where(v => v.ShareId == shareId && v.FilePath == filePath)
            .OrderByDescending(v => v.SnapshotTimestampUtc)
            .Select(v => v.ContentHash)
            .FirstOrDefault();
        return Task.FromResult(latestHash == contentHash);
    }
}
