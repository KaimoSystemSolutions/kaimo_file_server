using Kaimo_File_Server.Core.Domain.Identity;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Repositories.Identity;

/// <summary>
/// Verifies <see cref="Kaimo_File_Server.Infrastructure.Repositories.Identity.UserRepository.GetSummariesAsync"/>:
/// the slim projection that principal pickers use instead of loading whole user entities
/// (photo bytes, password and NT hashes are never materialized).
/// </summary>
public sealed class UserSummaryProjectionTests : DatabaseTestBase
{
    [Fact]
    public async Task GetSummaries_ReturnsIdDisplayNameUsernameAndEmail()
    {
        var alice = new User(Guid.NewGuid(), "Alice Example", "alice", "pw", "nt", email: "alice@example.com");
        using (var db = NewContext()) { db.Users.Add(alice); db.SaveChanges(); }

        var summaries = await UserRepo().GetSummariesAsync();

        var summary = Assert.Single(summaries, s => s.Id == alice.Id);
        Assert.Equal(new PrincipalSummary(alice.Id, "Alice Example", "alice", "alice@example.com"), summary);
    }

    [Fact]
    public async Task GetSummaries_IncludesEveryUser()
    {
        var alice = SeedUser("alice");
        var bob = SeedUser("bob", isEnabled: false);

        var ids = (await UserRepo().GetSummariesAsync()).Select(s => s.Id).ToList();

        Assert.Contains(alice.Id, ids);
        Assert.Contains(bob.Id, ids);
    }

    [Fact]
    public async Task GetSummaries_MatchesFullLoad()
    {
        SeedUser("carol");
        // Whatever the schema seeds, the projection must agree with the full load.
        var full = (await UserRepo().GetAllAsync()).Select(u => u.Id).OrderBy(id => id);
        var slim = (await UserRepo().GetSummariesAsync()).Select(s => s.Id).OrderBy(id => id);

        Assert.Equal(full, slim);
    }
}
