namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Global rules for file versioning, persisted as JSON under <see cref="ConfigKey"/> and
/// edited from the settings page ("Versioning" tab). They decide what is versioned
/// (<see cref="Enabled"/>, <see cref="MaxFileSizeBytes"/>, <see cref="ExcludePatterns"/>)
/// and how much history is kept (thinning tiers, <see cref="MaxVersionsPerFile"/>,
/// <see cref="MaxAgeDays"/>, <see cref="PoolBudgetPercent"/>).
///
/// Retention never removes the newest version of an existing file: snapshots are taken
/// after a write, so the newest version is the only copy of the current content once the
/// file is overwritten in place.
/// </summary>
public sealed class VersioningPolicy
{
    public const string ConfigKey = "versioning.policy";

    public const long DefaultMaxFileSizeBytes = 512L * 1024 * 1024;
    public const int MaxDays = 3650;
    public const int MaxVersionsLimit = 10_000;
    public const int MaxBudgetPercent = 90;
    public const int MaxPatternLength = 256;
    public const int MaxPatternCount = 200;

    /// <summary>Lock, swap, partial-download and OS metadata files that never need a history.</summary>
    public static IReadOnlyList<string> DefaultExcludePatterns { get; } =
    [
        "~$*", "*.tmp", "*.temp", "*.part", "*.partial", "*.crdownload", "*.download",
        ".~lock.*#", "*.swp", "*.swx", "Thumbs.db", ".DS_Store", "desktop.ini",
    ];

    /// <summary>Global switch. When off, no new versions are created; existing ones stay under retention.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Files larger than this are not versioned. 0 = no limit.</summary>
    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;

    /// <summary>
    /// Glob patterns (<c>*</c>, <c>?</c>, <c>**</c>), case-insensitive. A pattern without
    /// <c>/</c> matches the file name, one with <c>/</c> the share-relative path.
    /// </summary>
    public List<string> ExcludePatterns { get; set; } = [.. DefaultExcludePatterns];

    /// <summary>Thin out older versions by time tiers (all → hourly → daily → weekly).</summary>
    public bool ThinningEnabled { get; set; } = true;

    /// <summary>Every version younger than this many hours is kept.</summary>
    public int KeepAllHours { get; set; } = 24;

    /// <summary>Up to this age (days) the newest version per clock hour is kept.</summary>
    public int HourlyForDays { get; set; } = 7;

    /// <summary>Up to this age (days) the newest version per day is kept; beyond it one per week.</summary>
    public int DailyForDays { get; set; } = 30;

    /// <summary>Hard cap per file; the oldest versions go first. 0 = no limit.</summary>
    public int MaxVersionsPerFile { get; set; } = 100;

    /// <summary>Versions older than this are removed. 0 = no limit.</summary>
    public int MaxAgeDays { get; set; } = 90;

    /// <summary>Versions may occupy at most this share of a pool's capacity. 0 = no budget.</summary>
    public int PoolBudgetPercent { get; set; } = 15;

    public static VersioningPolicy Default() => new();

    public VersioningPolicy Clone()
    {
        var clone = (VersioningPolicy)MemberwiseClone();
        clone.ExcludePatterns = [.. ExcludePatterns ?? []];
        return clone;
    }

    /// <summary>Clamps every value into its valid range and cleans the pattern list.</summary>
    public void Normalize()
    {
        MaxFileSizeBytes = Math.Max(0, MaxFileSizeBytes);
        KeepAllHours = Math.Clamp(KeepAllHours, 0, MaxDays * 24);
        HourlyForDays = Math.Clamp(HourlyForDays, 0, MaxDays);
        DailyForDays = Math.Clamp(DailyForDays, 0, MaxDays);
        MaxVersionsPerFile = Math.Clamp(MaxVersionsPerFile, 0, MaxVersionsLimit);
        MaxAgeDays = Math.Clamp(MaxAgeDays, 0, MaxDays);
        PoolBudgetPercent = Math.Clamp(PoolBudgetPercent, 0, MaxBudgetPercent);

        // The tiers must widen: hourly covers at least the keep-all window, daily the hourly one.
        HourlyForDays = Math.Max(HourlyForDays, (KeepAllHours + 23) / 24);
        DailyForDays = Math.Max(DailyForDays, HourlyForDays);

        ExcludePatterns = (ExcludePatterns ?? [])
            .Select(p => p?.Trim() ?? "")
            .Where(p => p.Length > 0 && p.Length <= MaxPatternLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPatternCount)
            .ToList();
    }

    /// <summary>Whether a share-relative path matches one of the <see cref="ExcludePatterns"/>.</summary>
    public bool IsExcluded(string shareRelativePath)
        => VersioningExclusionMatcher.For(ExcludePatterns).IsExcluded(shareRelativePath);
}
