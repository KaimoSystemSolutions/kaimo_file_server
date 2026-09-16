using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Guards the demo read-only ACL wrapper: pure reads must pass through to the real
/// service, anything carrying a write bit must be denied without touching it, and the
/// ACL-record mutations must fail loudly.
/// </summary>
public sealed class ReadOnlyDemoAclServiceTests
{
    private readonly Mock<IAclService> _inner = new();
    private readonly ReadOnlyDemoAclService _sut;
    private readonly UserContext _user;
    private readonly Guid _share = Guid.NewGuid();

    public ReadOnlyDemoAclServiceTests()
    {
        _sut = new ReadOnlyDemoAclService(_inner.Object);
        _user = new UserContext(new User(Guid.NewGuid(), "Demo", "demo", "hash", "nt"), [], [], []);
    }

    [Theory]
    [InlineData(FilePermission.ListReadData)]
    [InlineData(FilePermission.ReadAll)]
    [InlineData(FilePermission.TraverseExecute | FilePermission.ReadAttributes)]
    public async Task Read_permissions_pass_through(FilePermission read)
    {
        _inner.Setup(a => a.HasAccessAsync(_user, _share, "f.txt", false, read))
              .ReturnsAsync(true);

        Assert.True(await _sut.HasAccessAsync(_user, _share, "f.txt", false, read));
    }

    [Theory]
    [InlineData(FilePermission.CreateWriteData)]
    [InlineData(FilePermission.Delete)]
    [InlineData(FilePermission.ChangePermissions)]
    [InlineData(FilePermission.ReadAll | FilePermission.CreateWriteData)] // read+write => still denied
    public async Task Write_permissions_are_denied_without_hitting_inner(FilePermission write)
    {
        Assert.False(await _sut.HasAccessAsync(_user, _share, "f.txt", false, write));
        _inner.Verify(a => a.HasAccessAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<FilePermission>()), Times.Never);
    }

    [Fact]
    public async Task Batch_write_denies_every_item()
    {
        var items = new[] { ("a", false), ("b", true) };
        var result = await _sut.HasAccessBatchAsync(_user, _share, items, FilePermission.CreateWriteData);

        Assert.All(result.Values, Assert.False);
        _inner.Verify(a => a.HasAccessBatchAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(),
            It.IsAny<IReadOnlyList<(string, bool)>>(), It.IsAny<FilePermission>()), Times.Never);
    }

    [Fact]
    public async Task Acl_record_mutations_throw()
    {
        await Assert.ThrowsAsync<ReadOnlyDemoException>(() => _sut.RenameAclPathAsync(_share, "a", "b"));
        await Assert.ThrowsAsync<ReadOnlyDemoException>(() => _sut.DeleteAclAsync(_share, "a"));
        await Assert.ThrowsAsync<ReadOnlyDemoException>(() => _sut.DeleteShareMetadataAsync(_share));
    }

    /// <summary>
    /// The repository's DeleteFileMetadataPaths is now a bulk ExecuteDelete that bypasses
    /// the ReadOnlyDemoSaveInterceptor. That is only safe because the demo ACL wrapper
    /// rejects the delete BEFORE it can reach the real service (and thus the repository):
    /// the inner service is never invoked, so the ExecuteDelete never runs in demo mode.
    /// </summary>
    [Fact]
    public async Task DeleteFileMetadataPaths_InDemoMode_IsRejected()
    {
        await Assert.ThrowsAsync<ReadOnlyDemoException>(() => _sut.DeleteAclAsync(_share, "folder"));
        await Assert.ThrowsAsync<ReadOnlyDemoException>(() => _sut.DeleteShareMetadataAsync(_share));

        _inner.Verify(a => a.DeleteAclAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _inner.Verify(a => a.DeleteShareMetadataAsync(It.IsAny<Guid>()), Times.Never);
    }
}
