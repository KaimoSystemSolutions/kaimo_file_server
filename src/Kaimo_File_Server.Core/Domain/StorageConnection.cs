namespace Kaimo_File_Server.Core.Domain;

/// <summary>Lifecycle state of a reusable external-storage connection.</summary>
public enum StorageConnectionState
{
    // Values 0-3 preserve the former Cloud Access database representation.
    PendingAuthorization = 0,
    Ready = 1,
    Degraded = 2,
    Disabled = 3,
    PendingConfiguration = 4,
    NeedsReauthorization = 5
}

/// <summary>Authentication mechanism used by a provider profile or connection.</summary>
public enum StorageAuthorizationMode
{
    DeviceCode = 0,
    DelegatedAuthorizationCode = 1,
    ApplicationCredential = 2,
    ServiceAccount = 3,
    UsernamePassword = 4,
    SshKey = 5,
    HostMount = 6,
    /// <summary>Network protocol identity supplied by the runtime host.</summary>
    NetworkIdentity = 7
}

/// <summary>
/// Durable provider identity and encrypted authorization grant shared by syncs
/// and virtual shares. Paths, schedules, filters, and live access tokens do not
/// belong to this entity.
/// </summary>
public sealed class StorageConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CreatedByUserId { get; set; }
    public Guid? ProviderProfileId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StorageAuthorizationMode AuthorizationMode { get; set; } = StorageAuthorizationMode.DeviceCode;
    public string? SettingsJson { get; set; }
    public string? AccountDisplayName { get; set; }
    public string? AccountEmail { get; set; }
    public string? ProviderAccountId { get; set; }
    public string? ProviderTenantId { get; set; }
    public string? ProviderSubjectId { get; set; }
    public string? EffectiveScopes { get; set; }
    public string? EncryptedCredentialPayload { get; set; }
    public int CredentialFormatVersion { get; set; } = CredentialContextVersion;
    public int ProtectorPurposeVersion { get; set; } = CurrentProtectorPurposeVersion;
    public DateTime? CredentialUpdatedAtUtc { get; set; }
    public StorageConnectionState State { get; set; } = StorageConnectionState.PendingAuthorization;
    public string? LastErrorCode { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastVerifiedAtUtc { get; set; }
    public DateTime? LastSuccessfulUseAtUtc { get; set; }

    /// <summary>
    /// Application-managed optimistic concurrency token. Zero identifies an
    /// entity that has not been inserted through the repository yet.
    /// </summary>
    public long ConcurrencyVersion { get; set; }

    public const int CredentialContextVersion = 1;
    public const int CurrentProtectorPurposeVersion = 2;
}
