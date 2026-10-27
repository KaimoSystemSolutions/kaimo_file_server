using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class ScopedFileVersionServiceTests
{
    [Fact]
    public async Task IsNewestVersionAsync_IsForwardedToTheScopedService()
    {
        // Without forwarding, the interface default (false) would make every restore with a
        // skipped snapshot ask for consent again.
        var shareId = Guid.NewGuid();
        var inner = new Mock<IFileVersionService>();
        inner.Setup(v => v.IsNewestVersionAsync(shareId, "a.txt", It.IsAny<Stream>())).ReturnsAsync(true);
        var provider = new ServiceCollection().AddScoped(_ => inner.Object).BuildServiceProvider();

        var result = await new ScopedFileVersionService(provider)
            .IsNewestVersionAsync(shareId, "a.txt", new MemoryStream([1]));

        Assert.True(result);
    }
}
