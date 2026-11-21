using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories.Identity;
using Kaimo_File_Server.Core.Repositories.Shares;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.SmbBridge.Tests.Services;

/// <summary>
/// The Samba share reconciler skips a cycle when ListShares returns byte-identical
/// desired state. The database returns rows in no particular order (it changes
/// after updates), so the reply must be canonically ordered by itself.
/// </summary>
public sealed class ShareGrpcServiceOrderingTests
{
    [Fact]
    public async Task ListShares_OrdersSharesAndAllowedUsersOrdinally()
    {
        var shares = new List<ShareDefinition>
        {
            new("projects", "/data/storage/pool01/projects"),
            new("users", "/data/storage/pool01/users") { IsUserHomes = true },
            new("Archive", "/data/storage/pool01/Archive"),
            new("general", "/data/storage/pool01/general"),
        };
        var users = new[] { NewUser("zoe"), NewUser("anna"), NewUser("Bob"), NewUser("marco") };

        var reply = await CreateSut(shares, users).ListShares(new ListSharesRequest(), null!);

        Assert.Equal(["Archive", "general", "projects", "users"], reply.Shares.Select(s => s.Name));
        Assert.Equal(["Bob", "anna", "marco", "zoe"], reply.Shares.Single(s => s.Name == "users").AllowedUsers);
    }

    [Fact]
    public async Task ListShares_IsIdenticalForAnyRowOrder()
    {
        var shares = new List<ShareDefinition>
        {
            new("b", "/data/storage/pool01/b"),
            new("users", "/data/storage/pool01/users") { IsUserHomes = true },
            new("a", "/data/storage/pool01/a"),
        };
        var users = new[] { NewUser("carl"), NewUser("anna"), NewUser("bert") };

        var first = await CreateSut(shares, users).ListShares(new ListSharesRequest(), null!);
        shares.Reverse();
        var second = await CreateSut(shares, users.Reverse().ToArray())
            .ListShares(new ListSharesRequest(), null!);

        Assert.Equal(first, second);
    }

    private static User NewUser(string name) =>
        new(Guid.NewGuid(), name, name, "hash", "nthash") { HomeDirectoryEnabled = true };

    private static ShareGrpcService CreateSut(List<ShareDefinition> shares, IEnumerable<User> users)
    {
        var shareRepository = new Mock<IShareRepository>();
        shareRepository.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync(() => shares.ToList());
        var userRepository = new Mock<IUserRepository>();
        userRepository.Setup(r => r.GetAllAsync()).ReturnsAsync(() => users.ToList());
        return new ShareGrpcService(
            shareRepository.Object, userRepository.Object,
            Mock.Of<IScopedRoleAssignmentRepository>(), Mock.Of<IRoleRepository>(),
            Mock.Of<IGroupRepository>(), NullLogger<ShareGrpcService>.Instance);
    }
}
