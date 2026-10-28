using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Phase 4 of the scoped-permission audit (F17): an ACL entry is only changed or removed when it
/// belongs to the loaded file, so an entry id of another file (in a share the actor may not
/// manage) can never be touched through an editor that is authorized for this one.
/// </summary>
public partial class AclEditorViewModelAuthorizationTests
{
    private static AccessEntry Entry()
        => new(Guid.NewGuid(), AclEntryType.Allow, FilePermission.ReadAll, AclInheritance.Everything);

    [Fact]
    public async Task DeleteEntryAsync_EntryOfAnotherFile_IsRefused()
    {
        Authorize(true);
        await _sut.LoadAsync("docs", _shareId);

        var ok = await _sut.DeleteEntryAsync(Guid.NewGuid()); // not an entry of "docs"

        Assert.False(ok);
        Assert.Equal(Resources.Web_Error_EntryNotFound, _sut.ErrorMessage);
        _aclRepo.Verify(a => a.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task SaveEditEntryAsync_EntryNoLongerOnTheFile_IsRefused()
    {
        var existing = Entry();
        _aclRepo
            .Setup(a => a.GetByFileMetadataIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<AccessEntry> { existing });
        Authorize(true);
        await _sut.LoadAsync("docs", _shareId);
        _sut.StartEditEntry(existing);
        _sut.NewPermissions = FilePermission.FullControl;

        // Meanwhile the entry was removed from the file (the editor still lists it).
        _aclRepo
            .Setup(a => a.GetByFileMetadataIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<AccessEntry>());

        var ok = await _sut.SaveEditEntryAsync();

        Assert.False(ok);
        Assert.Equal(Resources.Web_Error_EntryNotFound, _sut.ErrorMessage);
        _aclRepo.Verify(a => a.UpdateAsync(It.IsAny<AccessEntry>()), Times.Never);
    }

    [Fact]
    public async Task SaveEditEntryAsync_EntryOfTheFile_IsUpdated()
    {
        var existing = Entry();
        _aclRepo
            .Setup(a => a.GetByFileMetadataIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<AccessEntry> { existing });
        Authorize(true);
        await _sut.LoadAsync("docs", _shareId);
        _sut.StartEditEntry(existing);
        _sut.NewPermissions = FilePermission.FullControl;

        Assert.True(await _sut.SaveEditEntryAsync());
        _aclRepo.Verify(a => a.UpdateAsync(It.Is<AccessEntry>(e =>
            e.Id == existing.Id && e.Permissions == FilePermission.FullControl)), Times.Once);
    }
}
