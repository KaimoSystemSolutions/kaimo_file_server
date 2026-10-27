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

    public const long DefaultMaxFileSizeBytes = 256L * 1024 * 1024;
    public const int MaxDays = 3650;
    public const int MaxVersionsLimit = 10_000;
    public const int MaxBudgetPercent = 90;
    public const int MaxPatternLength = 256;
    public const int MaxPatternCount = 200;

    /// <summary>
    /// Lock, swap and OS metadata files that never need a history. Temporary and partial
    /// files (*.tmp, *.part, *.crdownload …) are deliberately absent: safe-save editors
    /// (Word, LibreOffice) and browsers write the new content under such a name and rename
    /// it into place, and only that temporary file's snapshot carries the content over.
    ///
    /// Video, photos, audio, PDF, disk images, archives and installers are excluded as well: they
    /// are written once and rarely edited, yet a snapshot taken after the write stores each of
    /// them twice. Layered editor formats (*.psd) stay versioned.
    /// </summary>
    public static IReadOnlyList<string> DefaultExcludePatterns { get; } =
    [
        "~$*", ".~lock.*#", "*.swp", "*.swx", "Thumbs.db", ".DS_Store", "desktop.ini",
        // Video
        "*.mp4", "*.m4v", "*.mkv", "*.mov", "*.avi", "*.wmv", "*.webm", "*.mpg", "*.mpeg", "*.mts", "*.m2ts",
        // Photos (incl. camera RAW)
        "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.tif", "*.tiff", "*.webp", "*.heic", "*.heif",
        "*.raw", "*.cr2", "*.cr3", "*.nef", "*.arw", "*.dng", "*.orf", "*.rw2",
        // Audio
        "*.mp3", "*.wav", "*.flac", "*.aac", "*.ogg", "*.m4a", "*.wma", "*.opus",
        // PDF
        "*.pdf",
        // Disk images
        "*.iso", "*.img", "*.vhd", "*.vhdx", "*.vmdk", "*.qcow2", "*.dmg",
        // Archives
        "*.zip", "*.7z", "*.rar", "*.tar", "*.gz", "*.tgz", "*.bz2", "*.xz", "*.zst",
        // Executables and installers
        "*.exe", "*.msi", "*.dll", "*.msix", "*.appx", "*.deb", "*.rpm", "*.apk", "*.pkg", "*.appimage",
    ];

    /// <summary>Revision the store writes on every save; see <see cref="Revision"/>.</summary>
    public const int CurrentRevision = 1;

    /// <summary>
    /// Format revision of a stored policy. 0 (absent in JSON) = saved before revision 1, whose
    /// defaults still excluded temporary files; <see cref="UpgradeStored"/> drops those.
    /// </summary>
    public int Revision { get; set; }

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

    /// <summary>
    /// Up to this age (days) the newest version per clock hour is kept. Two days keep a file
    /// saved hourly at about 85 versions up to <see cref="MaxAgeDays"/>, inside
    /// <see cref="MaxVersionsPerFile"/>; a longer hourly tier lets the cap cut the daily
    /// and weekly tiers away.
    /// </summary>
    public int HourlyForDays { get; set; } = 2;

    /// <summary>Up to this age (days) the newest version per day is kept; beyond it one per week.</summary>
    public int DailyForDays { get; set; } = 30;

    /// <summary>Hard cap per file; the oldest versions go first. 0 = no limit.</summary>
    public int MaxVersionsPerFile { get; set; } = 100;

    /// <summary>Versions older than this are removed. 0 = no limit.</summary>
    public int MaxAgeDays { get; set; } = 90;

    /// <summary>
    /// Older versions may occupy at most this share of a pool's capacity. The newest version
    /// of each file mirrors live content and does not count. 0 = no budget.
    /// </summary>
    public int PoolBudgetPercent { get; set; } = 10;

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

    /// <summary>Temporary-file patterns of the revision-0 defaults; they kept safe-save content unversioned.</summary>
    private static readonly string[] Revision0TemporaryPatterns =
        ["*.tmp", "*.temp", "*.part", "*.partial", "*.crdownload", "*.download"];

    /// <summary>
    /// Brings a policy read from storage up to <see cref="CurrentRevision"/>. New defaults never
    /// reach a saved policy, so the temporary-file exclusions of revision 0 are dropped here. A
    /// policy saved afterwards carries the current revision and keeps whatever the admin chose.
    /// </summary>
    public void UpgradeStored()
    {
        if (Revision >= CurrentRevision) return;
        ExcludePatterns = (ExcludePatterns ?? [])
            .Where(p => !Revision0TemporaryPatterns.Contains(p?.Trim(), StringComparer.OrdinalIgnoreCase))
            .ToList();
        Revision = CurrentRevision;
    }

    /// <summary>Whether a share-relative path matches one of the <see cref="ExcludePatterns"/>.</summary>
    public bool IsExcluded(string shareRelativePath)
        => VersioningExclusionMatcher.For(ExcludePatterns).IsExcluded(shareRelativePath);
}
