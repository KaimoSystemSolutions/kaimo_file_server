using Kaimo_File_Server.Core.Services.File;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class VersioningPolicyTests
{
    [Fact]
    public void Default_HasDocumentedValues()
    {
        var policy = VersioningPolicy.Default();

        Assert.True(policy.Enabled);
        Assert.Equal(512L * 1024 * 1024, policy.MaxFileSizeBytes);
        Assert.True(policy.ThinningEnabled);
        Assert.Equal(24, policy.KeepAllHours);
        Assert.Equal(7, policy.HourlyForDays);
        Assert.Equal(30, policy.DailyForDays);
        Assert.Equal(100, policy.MaxVersionsPerFile);
        Assert.Equal(90, policy.MaxAgeDays);
        Assert.Equal(15, policy.PoolBudgetPercent);
        Assert.Contains("~$*", policy.ExcludePatterns);
        Assert.Contains("*.tmp", policy.ExcludePatterns);
        Assert.Contains(".~lock.*#", policy.ExcludePatterns);
    }

    [Fact]
    public void Default_ReturnsIndependentInstances()
    {
        var a = VersioningPolicy.Default();
        a.ExcludePatterns.Clear();

        Assert.NotEmpty(VersioningPolicy.Default().ExcludePatterns);
    }

    [Fact]
    public void Normalize_ClampsOutOfRangeValues()
    {
        var policy = new VersioningPolicy
        {
            MaxFileSizeBytes = -5,
            KeepAllHours = -1,
            HourlyForDays = 99_999,
            DailyForDays = -3,
            MaxVersionsPerFile = -10,
            MaxAgeDays = 99_999,
            PoolBudgetPercent = 150,
        };

        policy.Normalize();

        Assert.Equal(0, policy.MaxFileSizeBytes);
        Assert.Equal(0, policy.KeepAllHours);
        Assert.Equal(VersioningPolicy.MaxDays, policy.HourlyForDays);
        Assert.Equal(VersioningPolicy.MaxDays, policy.DailyForDays);
        Assert.Equal(0, policy.MaxVersionsPerFile);
        Assert.Equal(VersioningPolicy.MaxDays, policy.MaxAgeDays);
        Assert.Equal(VersioningPolicy.MaxBudgetPercent, policy.PoolBudgetPercent);
    }

    [Fact]
    public void Normalize_KeepsThinningWindowsInAscendingOrder()
    {
        var policy = new VersioningPolicy { KeepAllHours = 72, HourlyForDays = 1, DailyForDays = 2 };

        policy.Normalize();

        Assert.Equal(72, policy.KeepAllHours);
        Assert.Equal(3, policy.HourlyForDays);
        Assert.Equal(3, policy.DailyForDays);
    }

    [Fact]
    public void Normalize_CleansPatterns()
    {
        var policy = new VersioningPolicy
        {
            ExcludePatterns = ["  *.tmp ", "", "   ", "*.TMP", "~$*", new string('x', 300)],
        };

        policy.Normalize();

        Assert.Equal(["*.tmp", "~$*"], policy.ExcludePatterns);
    }

    [Fact]
    public void Normalize_NullPatternsBecomeEmpty()
    {
        var policy = new VersioningPolicy { ExcludePatterns = null! };

        policy.Normalize();

        Assert.Empty(policy.ExcludePatterns);
    }

    [Fact]
    public void Clone_CopiesAllValuesWithoutSharingThePatternList()
    {
        var policy = new VersioningPolicy { Enabled = false, MaxAgeDays = 12, ExcludePatterns = ["a*"] };

        var clone = policy.Clone();
        clone.ExcludePatterns.Add("b*");

        Assert.False(clone.Enabled);
        Assert.Equal(12, clone.MaxAgeDays);
        Assert.Equal(["a*"], policy.ExcludePatterns);
    }

    [Theory]
    [InlineData("~$report.docx", true)]
    [InlineData("folder/~$report.docx", true)]
    [InlineData("doc.txt", false)]
    public void IsExcluded_UsesThePatterns(string path, bool expected)
        => Assert.Equal(expected, VersioningPolicy.Default().IsExcluded(path));
}
