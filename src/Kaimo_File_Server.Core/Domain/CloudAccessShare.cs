namespace Kaimo_File_Server.Core.Domain;

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
    public bool IsReadOnly { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>First-level ACL: a granted user or group may access the complete virtual share.</summary>
public sealed class CloudAccessGrant
{
    public Guid ShareId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid GrantedByUserId { get; set; }
    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;
}
