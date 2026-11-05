namespace Kaimo_File_Server.Core.Domain;

/// <summary>Access level a virtual-share ACL entry grants. Write implies Read.</summary>
public enum CloudAccessPermission
{
    Read = 0,
    Write = 1
}

/// <summary>How a virtual share is offered to users.</summary>
public enum CloudAccessProvisioning
{
    /// <summary>Standalone entry in the share overview, browsed under /files/access/{id}.</summary>
    VirtualShare = 0,

    /// <summary>
    /// Mounted as a folder inside a local share (<see cref="CloudAccessShare.MountShareId"/> +
    /// <see cref="CloudAccessShare.MountPath"/>). Without a mount point the share is orphaned and
    /// reachable nowhere; it never falls back to standalone mode.
    /// </summary>
    Folder = 1
}

/// <summary>A provider folder exposed as a web-only, lazily accessed virtual share.</summary>
public sealed class CloudAccessShare
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public Guid DepartmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string RemoteRootPath { get; set; } = string.Empty;
    public string? RemoteRootItemId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public CloudAccessProvisioning Provisioning { get; set; } = CloudAccessProvisioning.VirtualShare;

    /// <summary>Local host share of the mount point (Folder mode only).</summary>
    public Guid? MountShareId { get; set; }

    /// <summary>Normalized share-relative path of the mount point (Folder mode only).</summary>
    public string? MountPath { get; set; }

    public bool IsFolderMode => Provisioning == CloudAccessProvisioning.Folder;
    public bool IsMounted => IsFolderMode && MountShareId is not null && !string.IsNullOrEmpty(MountPath);
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Root-level ACL entry: a granted user or group may access the complete virtual
/// share at the given <see cref="Permission"/> level (Read or Write).
/// </summary>
public sealed class CloudAccessGrant
{
    public Guid ShareId { get; set; }
    public Guid PrincipalId { get; set; }
    public CloudAccessPermission Permission { get; set; }
    public Guid GrantedByUserId { get; set; }
    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;
}
