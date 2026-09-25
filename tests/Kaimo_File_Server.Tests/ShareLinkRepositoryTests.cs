using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Repositories;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    // ─────────────── Token storage (no plain text in the database) ───────────────

    private ShareLinkTokenProtector Protector() => new(
        new EphemeralDataProtectionProvider(), NullLogger<ShareLinkTokenProtector>.Instance);

    [Fact]
    public async Task Create_StoresOnlyHashAndEncryptedToken()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        var protector = Protector();
        var link = new ShareLink
        {
            Token = token, ProtectedToken = protector.Protect(token),
            ShareId = Guid.NewGuid(), RootRelativePath = "a.txt", DisplayName = "a.txt",
            CreatedByUserId = Guid.NewGuid(),
        };
        await Repo().CreateAsync(link);

        await using var db = NewContext();
        var row = await db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Null(row.LegacyToken);
        Assert.Equal(ShareLink.HashToken(token), row.TokenHash);
        Assert.DoesNotContain(token, row.ProtectedToken);
        // A row read back for listing has no plain token, but the URL can still be rebuilt.
        Assert.Equal(string.Empty, row.Token);
        Assert.Equal(token, protector.Reveal(row));

        // Visitors resolve the link with the presented token.
        Assert.Equal(row.Id, (await Repo().GetByTokenAsync(token))!.Id);
        Assert.Null(await Repo().GetByTokenAsync(row.TokenHash)); // the stored hash is not a token
    }

    [Fact]
    public async Task Backfill_ProtectsLegacyPlainTextTokens_AndLinkKeepsWorking()
    {
        const string token = "legacy-token-created-before-hashing";
        await using (var db = NewContext())
        {
            db.ShareLinks.Add(new ShareLink
            {
                LegacyToken = token, TokenHash = ShareLink.HashToken(token),
                ShareId = Guid.NewGuid(), RootRelativePath = "b.txt", DisplayName = "b.txt",
                CreatedByUserId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }
        var protector = Protector();

        Assert.Equal(1, await protector.ProtectLegacyTokensAsync(Repo()));
        Assert.Equal(0, await protector.ProtectLegacyTokensAsync(Repo())); // idempotent

        await using var check = NewContext();
        var row = await check.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Null(row.LegacyToken);
        Assert.Equal(token, protector.Reveal(row));
        Assert.NotNull(await Repo().TryConsumeAccessAsync(token));
    }
}
