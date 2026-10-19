using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Logging;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Core.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Traceability of the timestamp contract: every write or stamp that applies source times logs
/// one Debug <see cref="LogEvents.FileTimesApplied"/> line with the times actually stored.
/// </summary>
public sealed class FileServiceTimesLogTests
{
    private readonly Mock<IStorageEngine> _storage = new();
    private readonly Mock<IAclService> _acl = new();
    private readonly Mock<ILogger<FileService>> _logger = new();
    private readonly Guid _shareId = Guid.NewGuid();
    private readonly FileService _sut;

    public FileServiceTimesLogTests()
    {
        _acl.Setup(a => a.HasAccessAsync(
                It.IsAny<UserContext>(), _shareId, It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<FilePermission>()))
            .ReturnsAsync(true);
        _storage.Setup(s => s.ToAbsolutePath(It.IsAny<string>())).Returns<string>(p => "/abs/" + p);
        _storage.Setup(s => s.IsDirectoryAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storage.Setup(s => s.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(new FileMetadata());

        _sut = new FileService(
            _storage.Object, _acl.Object, searchService: null, _shareId,
            versionService: null, ownershipService: null, logger: _logger.Object);
    }

    private static UserContext Ctx()
        => new(new User(Guid.NewGuid(), "Test", "test", "hash", "nt"), [], [], []);

    private void VerifyTimesLogged(string expectedText, Times times)
        => _logger.Verify(l => l.Log(
                LogLevel.Debug,
                LogEvents.FileTimesApplied,
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(expectedText)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    [Fact]
    public async Task WriteFileAsync_WithSourceTimes_LogsTheAppliedTimes()
    {
        _storage.Setup(s => s.ExistsAsync("docs/a.txt")).ReturnsAsync(false);
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await _sut.WriteFileAsync("docs/a.txt", Stream.Null, Ctx(),
            new FileTimes(null, modified, null));

        VerifyTimesLogged("docs/a.txt", Times.Once());
    }

    [Fact]
    public async Task WriteFileAsync_WithoutSourceTimes_LogsNothing()
    {
        _storage.Setup(s => s.ExistsAsync("docs/a.txt")).ReturnsAsync(false);

        await _sut.WriteFileAsync("docs/a.txt", Stream.Null, Ctx());

        VerifyTimesLogged("docs/a.txt", Times.Never());
    }

    [Fact]
    public async Task SetTimesAsync_LogsTheStampedTimes()
    {
        _storage.Setup(s => s.ExistsAsync("docs/a.txt")).ReturnsAsync(true);

        await _sut.SetTimesAsync("docs/a.txt", Ctx(),
            new FileTimes(null, null, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        VerifyTimesLogged("docs/a.txt", Times.Once());
    }
}
