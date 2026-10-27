using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.File;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class VersionRetentionPlannerTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Share = Guid.NewGuid();

    private static FileVersion At(TimeSpan age, int number = 0)
        => new(Share, "doc.txt", Now - age, $"AA/BB/{Guid.NewGuid():N}.bin.gz",
            Guid.NewGuid().ToString("N"), 1, null, number);

    private static VersioningPolicy Policy(Action<VersioningPolicy>? configure = null)
    {
        var policy = VersioningPolicy.Default();
        configure?.Invoke(policy);
        return policy;
    }

    private static IReadOnlyList<FileVersion> Plan(VersioningPolicy policy, params FileVersion[] versions)
        => VersionRetentionPlanner.SelectForDeletion(versions, policy, Now);

    [Fact]
    public void EmptyHistory_DeletesNothing()
        => Assert.Empty(Plan(Policy()));

    [Fact]
    public void NewestVersion_IsKeptEvenWhenOlderThanMaxAge()
    {
        var newest = At(TimeSpan.FromDays(400));
        var older = At(TimeSpan.FromDays(500));

        var deleted = Plan(Policy(), older, newest);

        Assert.Equal([older], deleted);
    }

    [Fact]
    public void NewestVersion_IsKeptWhenMaxVersionsIsOne()
    {
        var newest = At(TimeSpan.FromMinutes(1));
        var older = At(TimeSpan.FromMinutes(2));

        var deleted = Plan(Policy(p => p.MaxVersionsPerFile = 1), newest, older);

        Assert.Equal([older], deleted);
    }

    [Fact]
    public void KeepAllWindow_KeepsEveryRecentVersion()
    {
        var versions = Enumerable.Range(0, 30).Select(i => At(TimeSpan.FromMinutes(i * 10))).ToArray();

        Assert.Empty(Plan(Policy(), versions));
    }

    [Fact]
    public void HourlyTier_KeepsNewestVersionPerHour()
    {
        // Three versions inside one clock hour two days ago, one in the next hour.
        var baseAge = TimeSpan.FromDays(2);
        var hourStart = Now - baseAge;
        hourStart = new DateTime(hourStart.Year, hourStart.Month, hourStart.Day, hourStart.Hour, 0, 0, DateTimeKind.Utc);
        FileVersion InHour(int minute) => new(Share, "doc.txt", hourStart.AddMinutes(minute),
            $"AA/BB/{Guid.NewGuid():N}.bin.gz", Guid.NewGuid().ToString("N"), 1, null, 0);

        var newestInHour = InHour(50);
        var middle = InHour(30);
        var oldest = InHour(5);
        var newest = At(TimeSpan.Zero);

        var deleted = Plan(Policy(), newest, oldest, middle, newestInHour);

        Assert.Equal(2, deleted.Count);
        Assert.Contains(middle, deleted);
        Assert.Contains(oldest, deleted);
    }

    [Fact]
    public void DailyTier_KeepsOneVersionPerDay()
    {
        var newest = At(TimeSpan.Zero);
        var dayTen = Now.Date.AddDays(-10);
        var morning = new FileVersion(Share, "doc.txt", dayTen.AddHours(8), "AA/BB/m.bin.gz", "M", 1, null, 0);
        var evening = new FileVersion(Share, "doc.txt", dayTen.AddHours(20), "AA/BB/e.bin.gz", "E", 1, null, 0);

        var deleted = Plan(Policy(), newest, morning, evening);

        Assert.Equal([morning], deleted);
    }

    [Fact]
    public void WeeklyTier_KeepsOneVersionPerWeek()
    {
        var newest = At(TimeSpan.Zero);
        // Weekly buckets are 7-day ranges counted from DateTime.MinValue; both versions
        // fall into the same range about 58 days ago.
        var weekStart = new DateTime((Now.AddDays(-60).Ticks / TimeSpan.TicksPerDay / 7) * 7 * TimeSpan.TicksPerDay, DateTimeKind.Utc);
        var first = new FileVersion(Share, "doc.txt", weekStart.AddDays(1), "AA/BB/1.bin.gz", "1", 1, null, 0);
        var second = new FileVersion(Share, "doc.txt", weekStart.AddDays(2), "AA/BB/2.bin.gz", "2", 1, null, 0);

        var deleted = Plan(Policy(), newest, first, second);

        Assert.Equal([first], deleted);
    }

    [Fact]
    public void MaxAge_DeletesOlderVersions()
    {
        var newest = At(TimeSpan.Zero);
        var young = At(TimeSpan.FromDays(80));
        var old = At(TimeSpan.FromDays(100));

        var deleted = Plan(Policy(), newest, young, old);

        Assert.Equal([old], deleted);
    }

    [Fact]
    public void MaxAgeZero_KeepsOldVersions()
    {
        var newest = At(TimeSpan.Zero);
        var old = At(TimeSpan.FromDays(1000));

        Assert.Empty(Plan(Policy(p => p.MaxAgeDays = 0), newest, old));
    }

    [Fact]
    public void MaxVersions_DropsOldestSurvivorsFirst()
    {
        var versions = Enumerable.Range(0, 10).Select(i => At(TimeSpan.FromMinutes(i))).ToArray();

        var deleted = Plan(Policy(p => p.MaxVersionsPerFile = 4), versions);

        Assert.Equal(6, deleted.Count);
        Assert.All(deleted, v => Assert.True(v.SnapshotTimestampUtc <= Now - TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void ThinningDisabled_AppliesOnlyCountAndAge()
    {
        var policy = Policy(p => { p.ThinningEnabled = false; p.MaxAgeDays = 0; p.MaxVersionsPerFile = 0; });
        var versions = Enumerable.Range(0, 50).Select(i => At(TimeSpan.FromHours(i * 5))).ToArray();

        Assert.Empty(Plan(policy, versions));
    }

    [Fact]
    public void InputOrder_DoesNotMatter()
    {
        var a = At(TimeSpan.Zero);
        var b = At(TimeSpan.FromDays(200));

        Assert.Equal([b], Plan(Policy(), b, a));
        Assert.Equal([b], Plan(Policy(), a, b));
    }
}
