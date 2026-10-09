using Kaimo_File_Server.Core.Domain.Backup;

namespace Kaimo_File_Server.Infrastructure.Backup.Restic;

/// <summary>
/// Pure schedule math for backup jobs: fixed local start times on selected weekdays.
/// Missed slots (server down) collapse into exactly one catch-up run, bounded by a maximum
/// age. A start time that does not exist (DST gap) is skipped; one that exists twice (DST
/// overlap) runs once, at its first occurrence.
/// </summary>
public static class BackupScheduleCalculator
{
    public static readonly TimeSpan DefaultMaxCatchUp = TimeSpan.FromDays(7);

    /// <summary>
    /// The newest slot in (<paramref name="lastHandledUtc"/>, <paramref name="nowUtc"/>] that is
    /// not older than <paramref name="maxCatchUp"/>, or null when nothing is due.
    /// </summary>
    public static DateTime? FindDueSlot(
        BackupSchedule schedule, DateTime nowUtc, TimeZoneInfo zone, DateTime lastHandledUtc, TimeSpan maxCatchUp)
    {
        if (schedule.IsEmpty)
            return null;
        var earliest = Max(lastHandledUtc, nowUtc - maxCatchUp);
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        // One extra day on each side absorbs any UTC offset.
        var days = (int)Math.Ceiling(maxCatchUp.TotalDays) + 1;
        for (var d = 0; d <= days; d++)
        {
            var date = localToday.AddDays(-d);
            foreach (var slot in SlotsOn(schedule, date, zone).OrderByDescending(s => s))
            {
                if (slot > nowUtc) continue;
                if (slot <= earliest) return null;
                return slot;
            }
        }
        return null;
    }

    /// <summary>The next slot strictly after <paramref name="nowUtc"/> (for display), or null.</summary>
    public static DateTime? NextSlot(BackupSchedule schedule, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (schedule.IsEmpty)
            return null;
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        for (var d = -1; d <= 8; d++)
        {
            var next = SlotsOn(schedule, localToday.AddDays(d), zone).Where(s => s > nowUtc).OrderBy(s => s).FirstOrDefault();
            if (next != default)
                return next;
        }
        return null;
    }

    private static IEnumerable<DateTime> SlotsOn(BackupSchedule schedule, DateTime localDate, TimeZoneInfo zone)
    {
        if (!schedule.Days.Contains(localDate.DayOfWeek))
            yield break;
        foreach (var time in schedule.Times.Distinct())
        {
            var local = DateTime.SpecifyKind(localDate.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
                continue;
            if (zone.IsAmbiguousTime(local))
            {
                // First occurrence = the larger offset (daylight time) = the earlier UTC instant.
                var offset = zone.GetAmbiguousTimeOffsets(local).Max();
                yield return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
                continue;
            }
            yield return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
