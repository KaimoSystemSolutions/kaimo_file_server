using Xunit;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pure logic of the restic file backup: schedule math, backend mapping, CLI arguments,
/// exit-code mapping, output parsing, source guards and release/visibility rules.
/// </summary>
public partial class ResticBackupTests
{
    // ─────────────── Schedule ───────────────

    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);
    private static BackupSchedule Daily(params (int H, int M)[] times) => new()
    {
        Times = times.Select(t => new TimeOnly(t.H, t.M)).ToList(),
        Days = Enum.GetValues<DayOfWeek>().ToList(),
    };

    [Fact]
    public void FindDueSlot_ReturnsSlotThatJustPassed()
    {
        // 02:00 Berlin (CEST, UTC+2) on 2026-10-05 = 00:00 UTC.
        var slot = BackupScheduleCalculator.FindDueSlot(Daily((2, 0)), Utc(2026, 10, 5, 1, 30), Berlin,
            Utc(2026, 10, 4, 12), TimeSpan.FromDays(7));
        Assert.Equal(Utc(2026, 10, 5, 0), slot);
    }

    [Fact]
    public void FindDueSlot_NothingDueBeforeTheNextSlot()
    {
        var slot = BackupScheduleCalculator.FindDueSlot(Daily((2, 0)), Utc(2026, 10, 4, 23, 30), Berlin,
            Utc(2026, 10, 4, 12), TimeSpan.FromDays(7));
        Assert.Null(slot);
    }

    [Fact]
    public void FindDueSlot_SeveralMissedSlots_CollapseIntoTheNewestOne()
    {
        var slot = BackupScheduleCalculator.FindDueSlot(Daily((2, 0), (14, 0)), Utc(2026, 10, 5, 1), Berlin,
            Utc(2026, 10, 1, 0, 30), TimeSpan.FromDays(7));
        Assert.Equal(Utc(2026, 10, 5, 0), slot);
    }

    [Fact]
    public void FindDueSlot_IgnoresSlotsOlderThanTheCatchUpWindow()
    {
        // Mondays only; on Sunday 2026-10-04 the last Monday (09-28) is 6.5 days ago.
        var schedule = new BackupSchedule { Times = [new TimeOnly(2, 0)], Days = [DayOfWeek.Monday] };
        Assert.Null(BackupScheduleCalculator.FindDueSlot(schedule, Utc(2026, 10, 4, 12), Berlin,
            Utc(2026, 9, 1, 0), TimeSpan.FromDays(3)));
        Assert.Equal(Utc(2026, 9, 28, 0), BackupScheduleCalculator.FindDueSlot(schedule, Utc(2026, 10, 4, 12), Berlin,
            Utc(2026, 9, 1, 0), TimeSpan.FromDays(7)));
    }

    [Fact]
    public void FindDueSlot_HonorsWeekdayFilter()
    {
        var schedule = new BackupSchedule { Times = [new TimeOnly(2, 0)], Days = [DayOfWeek.Tuesday] };
        // Monday 2026-10-05 03:00 local: no Tuesday slot since the last handled one.
        Assert.Null(BackupScheduleCalculator.FindDueSlot(schedule, Utc(2026, 10, 5, 1), Berlin,
            Utc(2026, 9, 30, 0), TimeSpan.FromDays(7)));
    }

    [Fact]
    public void FindDueSlot_SkipsStartTimeInsideTheDstGap()
    {
        // 2026-03-29 02:30 does not exist in Berlin (02:00 → 03:00).
        Assert.Null(BackupScheduleCalculator.FindDueSlot(Daily((2, 30)), Utc(2026, 3, 29, 10), Berlin,
            Utc(2026, 3, 28, 12), TimeSpan.FromDays(7)));
    }

    [Fact]
    public void FindDueSlot_RunsAmbiguousStartTimeOnceAtItsFirstOccurrence()
    {
        // 2026-10-25 02:30 exists twice (CEST 00:30 UTC, CET 01:30 UTC).
        var first = BackupScheduleCalculator.FindDueSlot(Daily((2, 30)), Utc(2026, 10, 25, 3), Berlin,
            Utc(2026, 10, 24, 12), TimeSpan.FromDays(7));
        Assert.Equal(Utc(2026, 10, 25, 0, 30), first);
        Assert.Null(BackupScheduleCalculator.FindDueSlot(Daily((2, 30)), Utc(2026, 10, 25, 3), Berlin,
            first!.Value, TimeSpan.FromDays(7)));
    }

    [Fact]
    public void FindDueSlot_EmptySchedule_NeverRuns()
        => Assert.Null(BackupScheduleCalculator.FindDueSlot(new BackupSchedule(), Utc(2026, 10, 5, 1), Berlin,
            Utc(2026, 1, 1, 0), TimeSpan.FromDays(7)));

    [Fact]
    public void NextSlot_ReturnsTheNextStart()
        => Assert.Equal(Utc(2026, 10, 5, 12), BackupScheduleCalculator.NextSlot(Daily((2, 0), (14, 0)), Utc(2026, 10, 5, 1), Berlin));
}
