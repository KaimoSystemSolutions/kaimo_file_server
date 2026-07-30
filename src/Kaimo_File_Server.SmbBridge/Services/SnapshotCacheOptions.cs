using Microsoft.Extensions.Options;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// Startup-validated policy for the rebuildable snapshot projection cache.
/// Bounds prevent accidental immediate eviction, tight sweep loops, unbounded
/// retention, and nonsensical capacity arithmetic.
/// </summary>
public sealed class SnapshotCacheOptions
{
    public const string SectionName = "Snapshots:Cache";

    public const double DefaultTtlHours = 24;
    public const long DefaultMaxBytesPerShare = 5L * 1024 * 1024 * 1024;
    public const double DefaultSweepMinutes = 30;

    public const double MinTtlHours = 1.0 / 60;
    public const double MaxTtlHours = 24 * 365;
    public const long MinBytesPerShare = 1024L * 1024;
    public const long MaxBytesPerShareLimit = 100L * 1024 * 1024 * 1024 * 1024;
    public const double MinSweepMinutes = 1;
    public const double MaxSweepMinutes = 24 * 60;

    public string RootPath { get; set; } = SnapshotCache.DefaultRoot;
    public double TtlHours { get; set; } = DefaultTtlHours;
    public long MaxBytesPerShare { get; set; } = DefaultMaxBytesPerShare;
    public double SweepMinutes { get; set; } = DefaultSweepMinutes;

    internal TimeSpan Ttl => TimeSpan.FromHours(TtlHours);
    internal TimeSpan SweepInterval => TimeSpan.FromMinutes(SweepMinutes);
}

internal sealed class SnapshotCacheOptionsValidator
    : IValidateOptions<SnapshotCacheOptions>
{
    public ValidateOptionsResult Validate(
        string? name, SnapshotCacheOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.RootPath) ||
            !Path.IsPathRooted(options.RootPath))
        {
            failures.Add(
                $"{SnapshotCacheOptions.SectionName}:RootPath must be an absolute path outside every SMB share.");
        }
        else
        {
            try
            {
                _ = Path.GetFullPath(options.RootPath);
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                failures.Add(
                    $"{SnapshotCacheOptions.SectionName}:RootPath is not a valid absolute path.");
            }
        }

        ValidateFiniteRange(
            options.TtlHours,
            SnapshotCacheOptions.MinTtlHours,
            SnapshotCacheOptions.MaxTtlHours,
            $"{SnapshotCacheOptions.SectionName}:TtlHours",
            failures);
        ValidateFiniteRange(
            options.SweepMinutes,
            SnapshotCacheOptions.MinSweepMinutes,
            SnapshotCacheOptions.MaxSweepMinutes,
            $"{SnapshotCacheOptions.SectionName}:SweepMinutes",
            failures);

        if (options.MaxBytesPerShare < SnapshotCacheOptions.MinBytesPerShare ||
            options.MaxBytesPerShare >
            SnapshotCacheOptions.MaxBytesPerShareLimit)
        {
            failures.Add(
                $"{SnapshotCacheOptions.SectionName}:MaxBytesPerShare must be between " +
                $"{SnapshotCacheOptions.MinBytesPerShare} and " +
                $"{SnapshotCacheOptions.MaxBytesPerShareLimit} bytes.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateFiniteRange(
        double value,
        double minimum,
        double maximum,
        string key,
        ICollection<string> failures)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            failures.Add(
                $"{key} must be finite and between {minimum} and {maximum}.");
    }
}
