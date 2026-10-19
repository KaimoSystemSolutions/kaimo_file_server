using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class SnapshotCacheOptionsTests
{
    private readonly SnapshotCacheOptionsValidator _validator = new();

    [Fact]
    public void Defaults_AreValid()
    {
        ValidateOptionsResult result =
            _validator.Validate(null, new SnapshotCacheOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ExactBounds_AreValid()
    {
        ValidateOptionsResult minimum = _validator.Validate(
            null,
            new SnapshotCacheOptions
            {
                TtlHours = SnapshotCacheOptions.MinTtlHours,
                SweepMinutes = SnapshotCacheOptions.MinSweepMinutes,
                MaxBytesPerShare = SnapshotCacheOptions.MinBytesPerShare
            });
        ValidateOptionsResult maximum = _validator.Validate(
            null,
            new SnapshotCacheOptions
            {
                TtlHours = SnapshotCacheOptions.MaxTtlHours,
                SweepMinutes = SnapshotCacheOptions.MaxSweepMinutes,
                MaxBytesPerShare = SnapshotCacheOptions.MaxBytesPerShareLimit
            });

        Assert.True(minimum.Succeeded);
        Assert.True(maximum.Succeeded);
    }

    [Fact]
    public void BoundInvalidConfiguration_ThrowsOptionsValidationException()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Snapshots:Cache:RootPath"] = Path.GetTempPath(),
                ["Snapshots:Cache:TtlHours"] = "NaN",
                ["Snapshots:Cache:MaxBytesPerShare"] = "0",
                ["Snapshots:Cache:SweepMinutes"] = "0"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<
            IValidateOptions<SnapshotCacheOptions>,
            SnapshotCacheOptionsValidator>();
        services.AddOptions<SnapshotCacheOptions>()
            .Bind(configuration.GetSection(SnapshotCacheOptions.SectionName))
            .ValidateOnStart();
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<
            OptionsValidationException>(
            () => provider.GetRequiredService<
                IOptions<SnapshotCacheOptions>>().Value);

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains(
                "TtlHours", StringComparison.Ordinal));
        Assert.Contains(
            exception.Failures,
            failure => failure.Contains(
                "MaxBytesPerShare", StringComparison.Ordinal));
        Assert.Contains(
            exception.Failures,
            failure => failure.Contains(
                "SweepMinutes", StringComparison.Ordinal));
    }

    public static TheoryData<Action<SnapshotCacheOptions>, string>
        InvalidSettings => new()
        {
            { options => options.RootPath = "", "RootPath" },
            { options => options.RootPath = "relative/cache", "RootPath" },
            { options => options.TtlHours = 0, "TtlHours" },
            { options => options.TtlHours = -1, "TtlHours" },
            { options => options.TtlHours = double.NaN, "TtlHours" },
            { options => options.TtlHours = double.PositiveInfinity, "TtlHours" },
            {
                options => options.TtlHours =
                    SnapshotCacheOptions.MaxTtlHours + 1,
                "TtlHours"
            },
            { options => options.SweepMinutes = 0, "SweepMinutes" },
            { options => options.SweepMinutes = -1, "SweepMinutes" },
            { options => options.SweepMinutes = double.NaN, "SweepMinutes" },
            {
                options => options.SweepMinutes = double.PositiveInfinity,
                "SweepMinutes"
            },
            {
                options => options.SweepMinutes =
                    SnapshotCacheOptions.MaxSweepMinutes + 1,
                "SweepMinutes"
            },
            {
                options => options.MaxBytesPerShare =
                    SnapshotCacheOptions.MinBytesPerShare - 1,
                "MaxBytesPerShare"
            },
            {
                options => options.MaxBytesPerShare =
                    SnapshotCacheOptions.MaxBytesPerShareLimit + 1,
                "MaxBytesPerShare"
            }
        };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void InvalidSetting_FailsWithConfigurationKey(
        Action<SnapshotCacheOptions> mutate,
        string expectedKey)
    {
        var options = new SnapshotCacheOptions();
        mutate(options);

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(expectedKey, StringComparison.Ordinal));
    }
}
