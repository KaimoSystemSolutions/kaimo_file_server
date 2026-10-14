using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Persistence;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Recycle-bin limit fields on <see cref="ShareDefinition"/>: effective-limit resolution,
/// validation bounds and persistence of the new columns plus the tracking table.
/// </summary>
public sealed class RecycleBinLimitsModelTests : IDisposable
{
    private const long Mb = 1L << 20;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestDbContextFactory _dbFactory;

    public RecycleBinLimitsModelTests()
    {
        _connection.Open();
        _dbFactory = new TestDbContextFactory(_connection);
        using var db = _dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void NewShare_DefaultsToInheritWithoutRetention()
    {
        var share = new ShareDefinition("docs", "/pool/docs");

        Assert.Equal(RecycleLimitMode.Inherit, share.RecycleMaxSizeMode);
        Assert.Null(share.RecycleMaxSizeBytes);
        Assert.Null(share.RecycleRetentionDays);
    }

    [Theory]
    [InlineData(RecycleLimitMode.Inherit, null, 500L, 500L)]
    [InlineData(RecycleLimitMode.Inherit, null, 0L, null)]
    [InlineData(RecycleLimitMode.Inherit, 42L, 500L, 500L)]
    [InlineData(RecycleLimitMode.Custom, 42L, 500L, 42L)]
    [InlineData(RecycleLimitMode.Custom, 42L, 0L, 42L)]
    [InlineData(RecycleLimitMode.Unlimited, 42L, 500L, null)]
    public void EffectiveMaxSize_ResolvesModeAgainstGlobalDefault(
        RecycleLimitMode mode, long? custom, long global, long? expected)
    {
        var share = new ShareDefinition("docs", "/pool/docs")
        {
            RecycleMaxSizeMode = mode,
            RecycleMaxSizeBytes = custom
        };

        Assert.Equal(expected, share.GetEffectiveRecycleMaxSizeBytes(global));
    }

    [Theory]
    [InlineData(RecycleLimitMode.Inherit, null, null, true)]
    [InlineData(RecycleLimitMode.Unlimited, null, null, true)]
    [InlineData(RecycleLimitMode.Custom, Mb, null, true)]
    [InlineData(RecycleLimitMode.Custom, Mb - 1, null, false)]
    [InlineData(RecycleLimitMode.Custom, null, null, false)]
    [InlineData(RecycleLimitMode.Inherit, null, 1, true)]
    [InlineData(RecycleLimitMode.Inherit, null, 3650, true)]
    [InlineData(RecycleLimitMode.Inherit, null, 0, false)]
    [InlineData(RecycleLimitMode.Inherit, null, 3651, false)]
    public void Validation_EnforcesBounds(RecycleLimitMode mode, long? bytes, int? days, bool valid)
        => Assert.Equal(valid, ShareDefinition.IsValidRecycleLimits(mode, bytes, days));

    [Fact]
    public void ApplyRecycleLimits_DropsSizeUnlessCustom()
    {
        var share = new ShareDefinition("docs", "/pool/docs");

        share.ApplyRecycleLimits(new RecycleLimits(RecycleLimitMode.Unlimited, 5 * Mb, 30));

        Assert.Equal(new RecycleLimits(RecycleLimitMode.Unlimited, null, 30), share.GetRecycleLimits());
        share.ApplyRecycleLimits(new RecycleLimits(RecycleLimitMode.Custom, 5 * Mb, null));
        Assert.Equal(new RecycleLimits(RecycleLimitMode.Custom, 5 * Mb, null), share.GetRecycleLimits());
    }

    [Fact]
    public async Task LimitColumns_RoundTrip()
    {
        var share = new ShareDefinition("docs", "/pool/docs")
        {
            RecycleMaxSizeMode = RecycleLimitMode.Custom,
            RecycleMaxSizeBytes = 5 * Mb,
            RecycleRetentionDays = 30
        };
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.ShareDefinitions.Add(share);
            await db.SaveChangesAsync();
        }

        await using var read = _dbFactory.CreateDbContext();
        var loaded = await read.ShareDefinitions.SingleAsync(s => s.Id == share.Id);
        Assert.Equal(RecycleLimitMode.Custom, loaded.RecycleMaxSizeMode);
        Assert.Equal(5 * Mb, loaded.RecycleMaxSizeBytes);
        Assert.Equal(30, loaded.RecycleRetentionDays);
    }

    [Fact]
    public async Task TrackingRows_AreRemovedWithTheirShare()
    {
        var share = new ShareDefinition("docs", "/pool/docs");
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.ShareDefinitions.Add(share);
            db.RecycleBinEntries.Add(new RecycleBinEntry
            {
                ShareId = share.Id,
                Path = ".RECYCLE_BIN/a.txt",
                CtimeTicks = 1,
                SizeBytes = 2,
                FirstSeenUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.ShareDefinitions.Remove(await db.ShareDefinitions.SingleAsync());
            await db.SaveChangesAsync();
        }

        await using var read = _dbFactory.CreateDbContext();
        Assert.Empty(await read.RecycleBinEntries.ToListAsync());
    }
}
