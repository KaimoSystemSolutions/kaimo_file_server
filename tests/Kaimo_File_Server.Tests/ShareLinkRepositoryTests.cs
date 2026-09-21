using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Database-backed tests for <see cref="ShareLinkRepository.TryConsumeAccessAsync"/>: the atomic,
/// guarded access counter that enforces the download cap and time window at stream time.
/// </summary>
public sealed class ShareLinkRepositoryTests : DatabaseTestBase
{
    private ShareLinkRepository Repo() => new(DbFactory);

    private async Task<ShareLink> SeedLink(
        bool enabled = true, DateTime? start = null, DateTime? expiry = null,
        int? maxAccess = null, string token = "tok")
    {
        var link = new ShareLink
        {
            Token = token,
            ShareId = Guid.NewGuid(),
            RootRelativePath = "docs/file.txt",
            DisplayName = "file.txt",
            CreatedByUserId = Guid.NewGuid(),
            IsEnabled = enabled,
            StartsAtUtc = start,
            ExpiresAtUtc = expiry,
            MaxAccessCount = maxAccess,
        };
        return await Repo().CreateAsync(link);
    }

    [Fact]
    public async Task TryConsumeAccess_IncrementsThenExhaustsAtCap()
    {
        await SeedLink(maxAccess: 2);
        var repo = Repo();

        var first = await repo.TryConsumeAccessAsync("tok");
        var second = await repo.TryConsumeAccessAsync("tok");
        var third = await repo.TryConsumeAccessAsync("tok");

        Assert.NotNull(first);
        Assert.Equal(1, first!.AccessCount);
        Assert.NotNull(second);
        Assert.Equal(2, second!.AccessCount);
        Assert.Null(third); // cap reached — no further access, and no over-increment

        var persisted = await repo.GetByTokenAsync("tok");
        Assert.Equal(2, persisted!.AccessCount);
    }

    [Fact]
    public async Task TryConsumeAccess_NullWhenDisabled()
    {
        await SeedLink(enabled: false, maxAccess: 5);
        Assert.Null(await Repo().TryConsumeAccessAsync("tok"));
    }

    [Fact]
    public async Task TryConsumeAccess_NullWhenExpired()
    {
        await SeedLink(expiry: DateTime.UtcNow.AddHours(-1));
        Assert.Null(await Repo().TryConsumeAccessAsync("tok"));
    }

    [Fact]
    public async Task TryConsumeAccess_NullBeforeStart()
    {
        await SeedLink(start: DateTime.UtcNow.AddHours(1));
        Assert.Null(await Repo().TryConsumeAccessAsync("tok"));
    }

    [Fact]
    public async Task TryConsumeAccess_UnlimitedKeepsGranting()
    {
        await SeedLink(maxAccess: null);
        var repo = Repo();

        for (var i = 0; i < 5; i++)
            Assert.NotNull(await repo.TryConsumeAccessAsync("tok"));

        Assert.Equal(5, (await repo.GetByTokenAsync("tok"))!.AccessCount);
    }
}
