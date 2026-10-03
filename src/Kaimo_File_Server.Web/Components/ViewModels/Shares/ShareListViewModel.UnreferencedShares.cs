using Kaimo_File_Server.Core.Language;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>A folder directly inside a storage pool that no share points at.</summary>
public sealed record UnreferencedFolder(
    string Name, string Path, string PoolName, long SizeBytes, DateTime LastWriteUtc);

/// <summary>
/// "Deleted / unreferenced shares": deleting a share keeps its folder, and admins may
/// copy folders into a pool by hand. Both show up here via a plain folder scan, and
/// can be adopted as a share or purged. Global admins only — such a folder has no
/// department, so no scoped admin owns it.
/// </summary>
public partial class ShareListViewModel
{
    public IReadOnlyList<UnreferencedFolder> UnreferencedShares { get; private set; } = [];
    public bool IsScanning { get; private set; }
    /// <summary>The folder with a running adopt/purge action, to disable its row.</summary>
    public string? BusyPath { get; private set; }
    public string? UnreferencedErrorMessage { get; private set; }

    public bool CanManageUnreferencedShares => _manageAllShares;

    public Task LoadUnreferencedSharesAsync()
    {
        UnreferencedErrorMessage = null;
        return RescanUnreferencedAsync();
    }

    // Rescan without clearing the message, so an action's error survives its refresh.
    private async Task RescanUnreferencedAsync()
    {
        if (!_manageAllShares) return;

        IsScanning = true;
        try
        {
            var used = await GetUsedSharePathsAsync();
            var pools = StoragePools;
            UnreferencedShares = await Task.Run(() => pools
                .Where(pool => Directory.Exists(pool.Path))
                .SelectMany(pool => new DirectoryInfo(pool.Path).EnumerateDirectories()
                    .Where(dir => IsCandidateFolder(dir) && !used.Contains(NormalizePath(dir.FullName)))
                    .Select(dir => new UnreferencedFolder(
                        dir.Name, dir.FullName, pool.Name,
                        GetDirectorySize(dir.FullName), dir.LastWriteTimeUtc)))
                .OrderBy(f => f.PoolName).ThenBy(f => f.Name)
                .ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning the storage pools for unreferenced folders");
            UnreferencedErrorMessage = Resources.Web_Error_LoadSharesFailed;
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Deletes the folder and all its data. The entry only leaves the list once the
    /// folder is really gone; a failed or partial delete stays listed for a retry.
    /// </summary>
    public async Task<bool> PurgeUnreferencedFolderAsync(string path)
    {
        UnreferencedErrorMessage = null;
        if (!_manageAllShares)
        {
            UnreferencedErrorMessage = Resources.Web_Error_NoPermission;
            return false;
        }

        BusyPath = path;
        var shareLock = _lockManager.GetLock(Path.GetFileName(path));
        try
        {
            if (!await shareLock.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                UnreferencedErrorMessage = Resources.Web_Error_ShareInUse;
                return false;
            }

            try
            {
                // Already gone (e.g. a second admin purged it) — that is the goal.
                if (!Directory.Exists(path))
                    return true;

                if (!await IsStillUnreferencedAsync(path))
                {
                    UnreferencedErrorMessage = Resources.Web_Error_DeleteFailed;
                    return false;
                }

                try
                {
                    // Directory.Delete removes links, it never follows them out of the pool.
                    await Task.Run(() => Directory.Delete(path, recursive: true));
                }
                catch (DirectoryNotFoundException)
                {
                    // Raced with another purge; the folder is gone either way.
                }

                _logger.LogInformation("Unreferenced share folder '{Path}' deleted permanently", path);
                return true;
            }
            finally
            {
                shareLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting unreferenced share folder '{Path}'", path);
            UnreferencedErrorMessage = Resources.Web_Error_DeleteFailed;
            return false;
        }
        finally
        {
            BusyPath = null;
            await RescanUnreferencedAsync();
        }
    }

    /// <summary>
    /// Turns the folder into a share with the folder name, keeping all data. Uses the
    /// regular create path, so the share gets the same owner/admin ACLs as a new one.
    /// </summary>
    public async Task<bool> AdoptUnreferencedFolderAsync(string path)
    {
        UnreferencedErrorMessage = null;
        if (!_manageAllShares || !CanCreateShare)
        {
            UnreferencedErrorMessage = Resources.Web_Error_NoPermission;
            return false;
        }

        var name = Path.GetFileName(path);
        BusyPath = path;
        var shareLock = _lockManager.GetLock(name);
        try
        {
            if (!await shareLock.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                UnreferencedErrorMessage = Resources.Web_Error_ShareInUse;
                return false;
            }

            try
            {
                if (!await IsStillUnreferencedAsync(path))
                {
                    UnreferencedErrorMessage = Resources.Web_Error_CreateShareFailed;
                    return false;
                }

                CreateErrorMessage = null;
                if (!await CreateShareCoreAsync(
                        name, Path.GetDirectoryName(path)!, adoptExistingFolder: true))
                {
                    UnreferencedErrorMessage = CreateErrorMessage;
                    CreateErrorMessage = null;
                    return false;
                }
            }
            finally
            {
                shareLock.Release();
            }

            await LoadAsync();
            if (Shares.FirstOrDefault(s => PathComparer.Equals(
                    NormalizePath(s.Path), NormalizePath(path))) is { } created)
                SelectShare(created);
            return true;
        }
        finally
        {
            BusyPath = null;
            await RescanUnreferencedAsync();
        }
    }

    // Dot folders (.kaimo-moving-*, .RECYCLE_BIN, …), lost+found, links and reserved
    // system names are never share candidates, neither for listing nor for a purge.
    // A "users" folder without a share holds orphaned home folders: it is re-adopted by
    // the home-folder setup and must never be purged from here.
    private static bool IsCandidateFolder(DirectoryInfo dir)
        => !dir.Name.StartsWith('.')
           && dir.Name != "lost+found"
           && !IsReservedSystemShareName(dir.Name)
           && dir.LinkTarget is null;

    /// <summary>
    /// Re-validates right before a destructive or adopting action: the folder still
    /// exists directly in a configured pool, is a candidate, and — per a FRESH read of
    /// all shares (homes and disabled ones included) — no share points at it.
    /// </summary>
    private async Task<bool> IsStillUnreferencedAsync(string path)
    {
        var dir = new DirectoryInfo(path);
        return dir.Exists
               && IsCandidateFolder(dir)
               && dir.Parent is not null
               && GetConfiguredPoolPath(dir.Parent.FullName) is not null
               && !(await GetUsedSharePathsAsync()).Contains(NormalizePath(path));
    }

    private async Task<HashSet<string>> GetUsedSharePathsAsync()
        => (await _shareRepo.GetAllAsync())
            .Where(s => !string.IsNullOrWhiteSpace(s.Path))
            .Select(s => NormalizePath(s.Path))
            .ToHashSet(PathComparer);

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
