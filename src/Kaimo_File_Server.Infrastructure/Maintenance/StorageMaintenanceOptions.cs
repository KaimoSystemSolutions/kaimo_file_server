using Microsoft.Extensions.Options;

namespace Kaimo_File_Server.Infrastructure.Maintenance;

/// <summary>
/// Startup-validated policy for the Host storage-maintenance service. Fail-fast on a
/// mistyped grace period is exactly right for a job whose mistakes delete files.
/// </summary>
public sealed record StorageMaintenanceOptions
{
    public const string SectionName = "StorageMaintenance";

    /// <summary>Master switch for all storage-maintenance sweeps.</summary>
    public bool Enabled { get; init; } = true;

    // -- Abandoned per-share upload temporaries (".{name}.kaimo-{guid}.tmp") --
    public bool TempArtifactSweepEnabled { get; init; } = true;
    public int TempArtifactGraceHours { get; init; } = 24;
    public int TempArtifactIntervalHours { get; init; } = 24;

    // -- Abandoned cross-pool move staging directories ("<name>.kaimo-moving-{guid}") --
    public bool MoveStagingSweepEnabled { get; init; } = true;
    public int MoveStagingGraceHours { get; init; } = 168;   // 7 days
    public int MoveStagingIntervalHours { get; init; } = 6;

    // -- Age-based version retention sweep --
    public bool VersionRetentionSweepEnabled { get; init; } = true;
    public int VersionRetentionIntervalHours { get; init; } = 24;
    /// <summary>Files processed per sweep run (bounded so one run stays cheap).</summary>
    public int VersionRetentionMaxPaths { get; init; } = 500;
    /// <summary>Newest versions always retained — the floor that keeps history non-empty.</summary>
    public int VersionRetentionMinVersionsToKeep { get; init; } = 1;

    // -- Orphan version blob reclaim --
    public bool OrphanBlobSweepEnabled { get; init; } = true;
    public int OrphanBlobIntervalHours { get; init; } = 24;
    public int OrphanBlobGraceHours { get; init; } = 24;
    /// <summary>Storage shards scanned per run; 256/ShardsPerRun runs complete a full cycle.</summary>
    public int OrphanBlobShardsPerRun { get; init; } = 16;

    internal TimeSpan TempArtifactGrace => TimeSpan.FromHours(TempArtifactGraceHours);
    internal TimeSpan TempArtifactInterval => TimeSpan.FromHours(TempArtifactIntervalHours);
    internal TimeSpan MoveStagingGrace => TimeSpan.FromHours(MoveStagingGraceHours);
    internal TimeSpan MoveStagingInterval => TimeSpan.FromHours(MoveStagingIntervalHours);
    internal TimeSpan VersionRetentionInterval => TimeSpan.FromHours(VersionRetentionIntervalHours);
    internal TimeSpan OrphanBlobInterval => TimeSpan.FromHours(OrphanBlobIntervalHours);
    internal TimeSpan OrphanBlobGrace => TimeSpan.FromHours(OrphanBlobGraceHours);
}

internal sealed class StorageMaintenanceOptionsValidator
    : IValidateOptions<StorageMaintenanceOptions>
{
    // Generous upper bounds — this only rejects nonsensical values (negative, zero
    // interval, absurdly large) rather than dictating policy.
    private const int MaxGraceHours = 24 * 365;
    private const int MaxIntervalHours = 24 * 30;

    public ValidateOptionsResult Validate(string? name, StorageMaintenanceOptions options)
    {
        var failures = new List<string>();

        ValidateRange(options.TempArtifactGraceHours, 0, MaxGraceHours,
            $"{StorageMaintenanceOptions.SectionName}:TempArtifactGraceHours", failures);
        ValidateRange(options.TempArtifactIntervalHours, 1, MaxIntervalHours,
            $"{StorageMaintenanceOptions.SectionName}:TempArtifactIntervalHours", failures);
        ValidateRange(options.MoveStagingGraceHours, 0, MaxGraceHours,
            $"{StorageMaintenanceOptions.SectionName}:MoveStagingGraceHours", failures);
        ValidateRange(options.MoveStagingIntervalHours, 1, MaxIntervalHours,
            $"{StorageMaintenanceOptions.SectionName}:MoveStagingIntervalHours", failures);

        ValidateRange(options.VersionRetentionIntervalHours, 1, MaxIntervalHours,
            $"{StorageMaintenanceOptions.SectionName}:VersionRetentionIntervalHours", failures);
        ValidateRange(options.VersionRetentionMaxPaths, 1, 1_000_000,
            $"{StorageMaintenanceOptions.SectionName}:VersionRetentionMaxPaths", failures);
        ValidateRange(options.VersionRetentionMinVersionsToKeep, 1, 10_000,
            $"{StorageMaintenanceOptions.SectionName}:VersionRetentionMinVersionsToKeep", failures);
        ValidateRange(options.OrphanBlobIntervalHours, 1, MaxIntervalHours,
            $"{StorageMaintenanceOptions.SectionName}:OrphanBlobIntervalHours", failures);
        ValidateRange(options.OrphanBlobGraceHours, 0, MaxGraceHours,
            $"{StorageMaintenanceOptions.SectionName}:OrphanBlobGraceHours", failures);
        ValidateRange(options.OrphanBlobShardsPerRun, 1, 256,
            $"{StorageMaintenanceOptions.SectionName}:OrphanBlobShardsPerRun", failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateRange(int value, int min, int max, string key, ICollection<string> failures)
    {
        if (value < min || value > max)
            failures.Add($"{key} must be between {min} and {max}.");
    }
}
