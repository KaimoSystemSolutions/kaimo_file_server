using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Services;

/// <summary>A virtual share mounted as a folder that the current user may open.</summary>
public sealed record MountedFolder(Guid ShareId, string HostShareName, string MountPath)
{
    /// <summary>Folder name shown in the host listing (last mount-path segment).</summary>
    public string Name => ShareRelativePath.GetFileName(MountPath);

    /// <summary>Route of the mounted folder: /files/{host}/{mountPath}.</summary>
    public string RouteBase => $"/files/{Uri.EscapeDataString(HostShareName)}/{MountPath}";

    /// <summary>Whether the current user may manage the virtual share (its access grants).</summary>
    public bool CanManage { get; init; }

    /// <summary>Host-side location of a path inside the mount: host share, mount path, remote path.</summary>
    public string HostLocationOf(string remoteRelativePath)
        => ShareRelativePath.Combine($"{HostShareName}/{MountPath}", ShareRelativePath.Normalize(remoteRelativePath));

    /// <summary>Host-side breadcrumbs above the mount: the host share and every parent folder.</summary>
    public IReadOnlyList<(string Name, string Href)> ParentCrumbs
    {
        get
        {
            var hostRoute = $"/files/{Uri.EscapeDataString(HostShareName)}";
            var crumbs = new List<(string, string)> { (HostShareName, hostRoute) };
            var parent = ShareRelativePath.GetParent(MountPath);
            if (parent.Length == 0) return crumbs;
            var current = "";
            foreach (var segment in parent.Split('/'))
            {
                current = ShareRelativePath.Combine(current, segment);
                crumbs.Add((segment, $"{hostRoute}/{current}"));
            }
            return crumbs;
        }
    }
}

/// <summary>A route that points into a mounted folder, and the remote path below it.</summary>
public sealed record MountMatch(MountedFolder Mount, string RemoteSubPath);

/// <summary>
/// Per-circuit view of the virtual shares mounted into local shares. <see cref="RefreshAsync"/>
/// loads the mounts of one host share the current user may open (hidden mounts are never
/// returned, so their existence does not leak); <see cref="Match"/> and
/// <see cref="ChildrenOf"/> then answer synchronously, so routing never awaits per navigation.
/// </summary>
public sealed class CloudAccessMountResolver(
    ICloudAccessRepository repository,
    IShareRepository shares,
    CloudAccessAuthorizationService authorization,
    AuthenticationStateProvider authenticationState,
    IUserContextFactory userContextFactory)
{
    private readonly Dictionary<string, List<MountedFolder>> _byHost = new(StringComparer.OrdinalIgnoreCase);

    public bool HasLoaded(string hostShareName) => _byHost.ContainsKey(hostShareName);

    /// <summary>Loads the visible mounts of <paramref name="hostShareName"/> for the signed-in user.</summary>
    public async Task RefreshAsync(string hostShareName)
    {
        var host = await shares.GetByNameAsync(hostShareName);
        var state = await authenticationState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        var actor = string.IsNullOrWhiteSpace(username) ? null : await userContextFactory.CreateByUsernameAsync(username);
        if (host is null || actor is null)
        {
            _byHost[hostShareName] = [];
            return;
        }
        await RefreshAsync(host, actor);
    }

    /// <summary>Loads the visible mounts of <paramref name="host"/> for <paramref name="actor"/>.</summary>
    public async Task RefreshAsync(ShareDefinition host, UserContext actor)
    {
        var visible = new List<MountedFolder>();
        if (host.IsEnabled && !host.IsUserHomes)
        {
            foreach (var share in await repository.GetSharesAsync())
            {
                if (share.MountShareId != host.Id || !share.IsMounted) continue;
                if (await authorization.GetEffectivePermissionAsync(actor, share) is null) continue;
                visible.Add(new MountedFolder(share.Id, host.Name, ShareRelativePath.Normalize(share.MountPath))
                {
                    CanManage = await authorization.CanManageAsync(actor, share)
                });
            }
        }
        _byHost[host.Name] = visible;
    }

    /// <summary>The mount the route <paramref name="subPath"/> of the host share lies in, if any.</summary>
    public MountMatch? Match(string hostShareName, string? subPath)
    {
        if (!_byHost.TryGetValue(hostShareName, out var mounts) || mounts.Count == 0) return null;
        var path = ShareRelativePath.Normalize(subPath);
        foreach (var mount in mounts)
        {
            if (!ShareRelativePath.IsSameOrDescendant(mount.MountPath, path)) continue;
            var remote = path.Length == mount.MountPath.Length ? "" : path[(mount.MountPath.Length + 1)..];
            return new MountMatch(mount, remote);
        }
        return null;
    }

    /// <summary>Visible mounts located directly in folder <paramref name="directory"/> of the host share.</summary>
    public IReadOnlyList<MountedFolder> ChildrenOf(string hostShareName, string? directory)
    {
        if (!_byHost.TryGetValue(hostShareName, out var mounts)) return [];
        var dir = ShareRelativePath.Normalize(directory);
        return mounts
            .Where(m => string.Equals(ShareRelativePath.GetParent(m.MountPath), dir, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
