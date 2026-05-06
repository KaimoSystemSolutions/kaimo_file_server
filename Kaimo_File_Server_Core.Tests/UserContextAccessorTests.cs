using Kaimo_File_Server_Core.Core.Domain;
using Kaimo_File_Server_Core.Core.Domain.Identity;
using Kaimo_File_Server_Core.Smb;
using Xunit;

namespace Kaimo_File_Server_Core.Tests;

public class UserContextAccessorTests
{
    [Fact]
    public void Get_WithoutSet_ReturnsNull()
    {
        var accessor = new UserContextAccessor();
        Assert.Null(accessor.Get());
    }

    [Fact]
    public void SetAndGet_ReturnsSameContext()
    {
        var accessor = new UserContextAccessor();
        var user = new User(Guid.NewGuid(), "Test", "test", "h", "n");
        var ctx = new UserContext(user, [], [], []);

        accessor.Set(ctx);

        Assert.Same(ctx, accessor.Get());
    }

    [Fact]
    public async Task AsyncLocal_IsolatedBetweenTasks()
    {
        var accessor = new UserContextAccessor();

        var user1 = new User(Guid.NewGuid(), "User1", "user1", "h", "n");
        var ctx1 = new UserContext(user1, [], [], []);

        var user2 = new User(Guid.NewGuid(), "User2", "user2", "h", "n");
        var ctx2 = new UserContext(user2, [], [], []);

        UserContext? seenInTask1 = null;
        UserContext? seenInTask2 = null;

        var t1 = Task.Run(() =>
        {
            accessor.Set(ctx1);
            Thread.Sleep(50); // Sicherstellen, dass Task 2 auch gesetzt hat
            seenInTask1 = accessor.Get();
        });

        var t2 = Task.Run(() =>
        {
            accessor.Set(ctx2);
            Thread.Sleep(50);
            seenInTask2 = accessor.Get();
        });

        await Task.WhenAll(t1, t2);

        // Jeder Task sieht seinen eigenen Kontext (AsyncLocal-Isolation)
        Assert.Same(ctx1, seenInTask1);
        Assert.Same(ctx2, seenInTask2);
    }
}
