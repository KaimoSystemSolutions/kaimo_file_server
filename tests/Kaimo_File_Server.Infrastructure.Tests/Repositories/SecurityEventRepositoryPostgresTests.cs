using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Repositories;

/// <summary>
/// <see cref="SecurityEventRepository"/> against PostgreSQL with the production retry strategy,
/// which rejects user-initiated transactions outside an execution-strategy call.
/// Skipped unless <c>KAIMO_TEST_PG</c> is set.
/// </summary>
public sealed class SecurityEventRepositoryPostgresTests : PostgresTestBase
{
    [PostgresFact]
    public async Task AddLockoutRepeatsAsync_IncrementsEveryRowUnderTheRetryStrategy()
    {
        var repo = new SecurityEventRepository(DbFactory);
        LoginAttemptRecord Row(string user) => new()
        {
            AtUtc = DateTime.UtcNow, Channel = SecurityChannel.Smb, Username = user,
            Address = "10.0.0.5", Outcome = LoginOutcome.InvalidCredentials,
        };
        var first = Row("anna");
        var second = Row("lisa");
        await repo.SaveAsync([first, second], []);

        await repo.AddLockoutRepeatsAsync(new Dictionary<long, int> { [first.Id] = 2, [second.Id] = 5 });

        await using var db = NewContext();
        var counts = await db.LoginAttempts.OrderBy(a => a.Username).Select(a => a.RepeatCount).ToListAsync();
        Assert.Equal([3, 6], counts);
    }
}
