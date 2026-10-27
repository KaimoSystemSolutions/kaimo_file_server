using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Decides which versions of one file a <see cref="VersioningPolicy"/> removes. Pure and
/// side-effect free so the write path and the background sweep apply identical rules.
///
/// Order: the newest version is always kept; with thinning, every version inside the
/// keep-all window survives, older ones keep only the newest per hour, day and finally
/// week; then the age limit and the per-file cap remove the oldest survivors.
/// </summary>
public static class VersionRetentionPlanner
{
    private const long TicksPerWeek = TimeSpan.TicksPerDay * 7;

    public static IReadOnlyList<FileVersion> SelectForDeletion(
        IReadOnlyCollection<FileVersion> versions, VersioningPolicy policy, DateTime nowUtc)
    {
        if (versions.Count <= 1) return [];

        var ordered = versions.OrderByDescending(v => v.SnapshotTimestampUtc).ToList();
        var survivors = new List<FileVersion> { ordered[0] };

        if (policy.ThinningEnabled)
        {
            var occupied = new HashSet<(int Tier, long Bucket)>();
            if (BucketOf(ordered[0], policy, nowUtc) is { } newestBucket)
                occupied.Add(newestBucket);

            foreach (var version in ordered.Skip(1))
            {
                // Newest first, so the first version seen in a bucket is its newest one.
                if (BucketOf(version, policy, nowUtc) is not { } bucket || occupied.Add(bucket))
                    survivors.Add(version);
            }
        }
        else
        {
            survivors.AddRange(ordered.Skip(1));
        }

        if (policy.MaxAgeDays > 0)
        {
            var cutoff = nowUtc - TimeSpan.FromDays(policy.MaxAgeDays);
            survivors = survivors
                .Where((v, index) => index == 0 || v.SnapshotTimestampUtc >= cutoff)
                .ToList();
        }

        if (policy.MaxVersionsPerFile > 0 && survivors.Count > policy.MaxVersionsPerFile)
            survivors = survivors.Take(policy.MaxVersionsPerFile).ToList();

        var keep = survivors.ToHashSet();
        return ordered.Where(v => !keep.Contains(v)).ToList();
    }

    /// <summary>The thinning bucket of a version, or <c>null</c> inside the keep-all window.</summary>
    private static (int Tier, long Bucket)? BucketOf(FileVersion version, VersioningPolicy policy, DateTime nowUtc)
    {
        var age = nowUtc - version.SnapshotTimestampUtc;
        var ticks = version.SnapshotTimestampUtc.Ticks;

        if (age < TimeSpan.FromHours(policy.KeepAllHours)) return null;
        if (age < TimeSpan.FromDays(policy.HourlyForDays)) return (1, ticks / TimeSpan.TicksPerHour);
        if (age < TimeSpan.FromDays(policy.DailyForDays)) return (2, ticks / TimeSpan.TicksPerDay);
        return (3, ticks / TicksPerWeek);
    }
}
