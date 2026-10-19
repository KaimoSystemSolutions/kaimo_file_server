using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed class SambaCredentialRepositoryTests : DatabaseTestBase
{
    [Fact]
    public async Task BatchProjection_IsOrderedBoundedAndExcludesDisabledUsers()
    {
        SeedUser("charlie");
        SeedUser("alice");
        SeedUser("disabled", isEnabled: false);
        SeedUser("bob");

        var firstPage = await UserRepo().GetSambaCredentialBatchAsync(
            0,
            2,
            CancellationToken.None);
        var secondPage = await UserRepo().GetSambaCredentialBatchAsync(
            2,
            2,
            CancellationToken.None);

        Assert.Equal(["alice", "bob"], firstPage.Select(row => row.Username));
        Assert.Equal(["charlie"], secondPage.Select(row => row.Username));
        Assert.All(
            firstPage.Concat(secondPage),
            row => Assert.Equal("nt-hash", row.StoredNtHash));
    }
}
