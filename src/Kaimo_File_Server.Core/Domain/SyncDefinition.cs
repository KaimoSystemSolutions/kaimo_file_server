using System.Text.Json;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Core.Domain;

/// <summary>
/// Durable synchronization configuration. Provider credentials and mutable run
/// state deliberately live in <see cref="StorageConnection"/> and
/// <see cref="SyncDefinitionRuntime"/> respectively.
/// </summary>
public sealed class SyncDefinition
{
    public const string LegacyCloudSettingsSource = "legacy-cloud-settings";
    public const string DeletedByFirstClassEditorSource = "first-class-deleted";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public Guid LocalShareId { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = "/";
    public string? RemoteProviderItemId { get; set; }
    public SyncMode Mode { get; set; } = SyncMode.TwoWay;
    public CloudSyncSchedule Schedule { get; set; } = new();
    public CloudSyncAdvancedSettings AdvancedSettings { get; set; } = new();
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool RequiresRemoteFolderSelection { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid? RunAsUserId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Identifies an additive compatibility import. It is cleared when a later
    /// package makes the first-class editor authoritative.
    /// </summary>
    public string? MigrationSource { get; set; }

    /// <summary>SHA-256 checksum of the imported legacy configuration.</summary>
    public string? MigrationSourceChecksum { get; set; }

    public static string SerializeSchedule(CloudSyncSchedule value)
        => JsonSerializer.Serialize(value ?? new CloudSyncSchedule());

    public static CloudSyncSchedule DeserializeSchedule(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? new CloudSyncSchedule()
            : JsonSerializer.Deserialize<CloudSyncSchedule>(value) ?? new CloudSyncSchedule();

    public static string SerializeAdvancedSettings(CloudSyncAdvancedSettings value)
        => JsonSerializer.Serialize(value ?? new CloudSyncAdvancedSettings());

    public static CloudSyncAdvancedSettings DeserializeAdvancedSettings(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? new CloudSyncAdvancedSettings()
            : JsonSerializer.Deserialize<CloudSyncAdvancedSettings>(value) ?? new CloudSyncAdvancedSettings();
}

/// <summary>
/// Narrow mutable state for one sync. Updating progress or completion never
/// rewrites its schedule, paths, filters, or authorization grant.
/// </summary>
public sealed class SyncDefinitionRuntime
{
    public Guid SyncDefinitionId { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? LastSuccessfulRunAtUtc { get; set; }
    public Guid? CurrentJobId { get; set; }
    public string? LeaseOwner { get; set; }
    public int? ProgressPercent { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
