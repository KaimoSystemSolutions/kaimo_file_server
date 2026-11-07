using Kaimo_File_Server.Web.Components.Layout;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.Layout;

/// <summary>
/// The main layout subscribes to circuit-wide events (navigation, authentication state,
/// uploads). Blazor only calls <c>Dispose</c> on components that implement
/// <see cref="IDisposable"/>; without it those handlers outlive the layout. After a logout the
/// orphaned mandatory-password-change handler kept redirecting /login to /users, which bounced
/// back to /login — an endless navigation loop that made the page flicker and crash the browser.
/// </summary>
public class MainLayoutDisposalTests
{
    [Fact]
    public void MainLayout_IsDisposable_SoItsEventHandlersAreReleased()
        => Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(MainLayout)));
}
