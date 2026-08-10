namespace Kaimo_File_Server.Core.Domain;

public enum CloudAccessConnectionState
{
    PendingAuthorization = 0,
    Ready = 1,
    Error = 2,
    Disabled = 3
}

/// <summary>Server-owned credential and account binding for a remote file provider.</summary>
public sealed class CloudAccessConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepartmentId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? AccountDisplayName { get; set; }
    public string? AccountEmail { get; set; }
    public string? ProtectedCredentials { get; set; }
    public CloudAccessConnectionState State { get; set; } = CloudAccessConnectionState.PendingAuthorization;
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastVerifiedAtUtc { get; set; }
}
