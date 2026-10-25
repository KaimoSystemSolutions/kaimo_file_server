using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Services;
using Moq;

namespace Kaimo_File_Server.Tests;

/// <summary><see cref="ServiceAccessGate"/> instances for tests that are not about the switches.</summary>
internal static class TestGates
{
    /// <summary>
    /// Every service switched on globally (WebDAV included), so only the user's own
    /// <c>ServiceAccess</c> flags decide. Without <paramref name="users"/> nobody is an
    /// administrator; a supplied repository must answer <c>GetGroupsForUserAsync</c>.
    /// </summary>
    public static ServiceAccessGate AllGloballyOn(IUserRepository? users = null)
    {
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(true);
        if (users is null)
        {
            var mock = new Mock<IUserRepository>();
            mock.Setup(r => r.GetGroupsForUserAsync(It.IsAny<Guid>())).ReturnsAsync([]);
            users = mock.Object;
        }
        return new ServiceAccessGate(config.Object, users);
    }
}
