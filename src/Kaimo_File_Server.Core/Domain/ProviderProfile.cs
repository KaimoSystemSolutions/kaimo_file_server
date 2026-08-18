namespace Kaimo_File_Server.Core.Domain;

/// <summary>
/// Deployment-level provider identity and policy. Secret values are referenced
/// indirectly and must never be copied into this database entity.
/// </summary>
public sealed class ProviderProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StorageAuthorizationMode AuthorizationMode { get; set; }
    public string? TenantOrOrganizationId { get; set; }
    public string? PublicClientId { get; set; }
    public string? SecretReference { get; set; }
    public string? AllowedRedirectBaseUri { get; set; }
    public string? AllowedScopes { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Stable IDs for profiles supplied by the application.</summary>
public static class WellKnownProviderProfiles
{
    /// <summary>Zero-configuration Microsoft public-client device-code profile.</summary>
    public static readonly Guid MicrosoftPublicClient =
        Guid.Parse("e76c2a65-8bbd-4fac-b3f5-f663bc9df5df");
}
