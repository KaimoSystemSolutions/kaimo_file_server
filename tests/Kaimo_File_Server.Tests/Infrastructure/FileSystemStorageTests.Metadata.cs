using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Infrastructure.Storage;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class FileSystemStorageTests
{
    // ═══════════════════ Metadata ═══════════════════

    [Fact]
    public async Task GetMetadataAsync_File_ReturnsCorrectInfo()
    {
        File.WriteAllBytes(Path.Combine(_testRoot, "meta.txt"), new byte[512]);

        var meta = await _sut.GetMetadataAsync("meta.txt");
        Assert.Equal("meta.txt", meta.Path);
        Assert.Equal("meta.txt", meta.Name);
        Assert.Equal(512, meta.Size);
        Assert.False(meta.IsDirectory);
    }

    [Fact]
    public async Task GetMetadataAsync_File_CreatedAtIsBirthTimeNotModifiedTime()
    {
        // On Linux, .NET's CreationTimeUtc falls back to min(mtime, ctime), so a back-dated
        // mtime would show up as the creation time. Requires a file system that records btime.
        var fullPath = Path.Combine(_testRoot, "born.txt");
        File.WriteAllBytes(fullPath, [1]);
        var oldMtime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fullPath, oldMtime);

        var meta = await _sut.GetMetadataAsync("born.txt");
        Assert.Equal(oldMtime, meta.ModifiedAt);
        Assert.True(meta.CreatedAt > DateTime.UtcNow.AddMinutes(-5), $"CreatedAt was {meta.CreatedAt:O}");
    }

    [Fact]
    public void DosAttribBlob_CreateTimeIsParsedLikeSamba()
    {
        // Samba 4.x v5 blob: "0x20\0", pad, version 5, level 5, pad,
        // valid_flags ATTRIB|CREATE_TIME, attrib 0x20, create_time NTTIME.
        var v5 = Convert.FromBase64String("MHgyMAAABQAFAAAAEQAAACAAAADSUonYZaHXAQ==");
        Assert.True(FileBirthTime.TryParseDosAttribCreateTime(v5, out var created));
        Assert.Equal(DateTime.FromFileTimeUtc(0x01D7A165D88952D2), created);

        // Legacy hex-only blob carries no create time.
        Assert.False(FileBirthTime.TryParseDosAttribCreateTime("0x20\0"u8, out _));

        // CREATE_TIME bit not set → smbd ignores the value, so must we.
        v5[12] = 0x01;
        Assert.False(FileBirthTime.TryParseDosAttribCreateTime(v5, out _));
    }

    [Fact]
    public void DosAttribBlob_PatchSetsCreateTimeAndKeepsAttributes()
    {
        var v5 = Convert.FromBase64String("MHgyMAAABQAFAAAAEQAAACAAAADSUonYZaHXAQ==");
        v5[12] = 0x01; // ATTRIB only: no create time yet
        v5[16] = 0x22; // ARCHIVE | HIDDEN
        var created = new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc);

        var patched = FileBirthTime.BuildOrPatchDosAttrib(v5, isDirectory: false, created);

        Assert.True(FileBirthTime.TryParseDosAttribCreateTime(patched, out var parsed));
        Assert.Equal(created, parsed);
        Assert.Equal(v5.Length, patched.Length);
        Assert.Equal(0x22, patched[16]);       // attributes untouched
        Assert.Equal(0x11, patched[12]);       // ATTRIB | CREATE_TIME
    }

    [Fact]
    public void DosAttribBlob_PatchesAVersion4Blob()
    {
        // "0x20\0", pad, version 4, level 4, valid_flags ATTRIB, attrib, itime, create_time.
        var v4 = new byte[36];
        "0x20"u8.CopyTo(v4);
        v4[6] = 4; v4[8] = 4; v4[12] = 0x01; v4[16] = 0x20;
        var created = new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var patched = FileBirthTime.BuildOrPatchDosAttrib(v4, isDirectory: false, created);

        Assert.True(FileBirthTime.TryParseDosAttribCreateTime(patched, out var parsed));
        Assert.Equal(created, parsed);
        Assert.Equal(4, patched[6]);
    }

    [Theory]
    [InlineData(false, "0x20")]
    [InlineData(true, "0x10")]
    public void DosAttribBlob_FreshBlobIsVersion5WithDefaultAttributes(bool isDirectory, string hex)
    {
        var created = new DateTime(2015, 6, 7, 8, 9, 10, DateTimeKind.Utc);

        var blob = FileBirthTime.BuildOrPatchDosAttrib(ReadOnlySpan<byte>.Empty, isDirectory, created);

        Assert.True(FileBirthTime.TryParseDosAttribCreateTime(blob, out var parsed));
        Assert.Equal(created, parsed);
        Assert.Equal(hex, System.Text.Encoding.ASCII.GetString(blob, 0, hex.Length));
        Assert.Equal(5, blob[6]);
    }

    [Fact]
    public void DosAttribBlob_LegacyHexBlobKeepsItsAttributes()
    {
        var blob = FileBirthTime.BuildOrPatchDosAttrib("0x22\0"u8, isDirectory: false, DateTime.UtcNow);

        Assert.Equal("0x22", System.Text.Encoding.ASCII.GetString(blob, 0, 4));
        Assert.Equal(0x22, blob[16]);
    }

    [Fact]
    public async Task SetCreatedDateAsync_IsReadBackAsCreatedAt_ForFilesAndDirectories()
    {
        var created = new DateTime(2005, 5, 5, 5, 5, 5, DateTimeKind.Utc);
        File.WriteAllBytes(Path.Combine(_testRoot, "c.txt"), [1]);
        Directory.CreateDirectory(Path.Combine(_testRoot, "cdir"));

        await _sut.SetCreatedDateAsync("c.txt", created);
        await _sut.SetCreatedDateAsync("cdir", created);

        Assert.Equal(created, (await _sut.GetMetadataAsync("c.txt")).CreatedAt);
        Assert.Equal(created, (await _sut.GetMetadataAsync("cdir")).CreatedAt);
        await Assert.ThrowsAsync<IOException>(() => _sut.SetCreatedDateAsync("missing.txt", created));
    }

    [Fact]
    public async Task SetModifiedDateAsync_BackDating_KeepsTheCreationTime_ForFilesAndDirectories()
    {
        // A file system without birth time (NFS, CIFS, Docker Desktop bind mounts) makes Linux report
        // min(mtime, ctime) as the creation time, so a back-dated mtime would move it too — a copy
        // would then look as old as its source instead of new, unlike over SMB.
        await _sut.WriteAsync("new.txt", new MemoryStream([1]));
        Directory.CreateDirectory(Path.Combine(_testRoot, "newdir"));
        var fileCreated = (await _sut.GetMetadataAsync("new.txt")).CreatedAt;
        var dirCreated = (await _sut.GetMetadataAsync("newdir")).CreatedAt;
        var oldMtime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        await _sut.SetModifiedDateAsync("new.txt", oldMtime);
        await _sut.SetModifiedDateAsync("newdir", oldMtime);

        Assert.Equal(oldMtime, (await _sut.GetMetadataAsync("new.txt")).ModifiedAt);
        Assert.Equal(fileCreated, (await _sut.GetMetadataAsync("new.txt")).CreatedAt);
        Assert.Equal(dirCreated, (await _sut.GetMetadataAsync("newdir")).CreatedAt);
    }

    [Fact]
    public async Task WriteAsync_Overwrite_KeepsTheCreationTime()
    {
        var created = new DateTime(2006, 6, 6, 6, 6, 6, DateTimeKind.Utc);
        await _sut.WriteAsync("keep.txt", new MemoryStream([1]));
        await _sut.SetCreatedDateAsync("keep.txt", created);

        await _sut.WriteAsync("keep.txt", new MemoryStream([2, 3]));

        var meta = await _sut.GetMetadataAsync("keep.txt");
        Assert.Equal(2, meta.Size);
        Assert.Equal(created, meta.CreatedAt);
    }

    [Fact]
    public async Task GetMetadataAsync_Directory_ReturnsIsDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, "metadir"));

        var meta = await _sut.GetMetadataAsync("metadir");
        Assert.True(meta.IsDirectory);
    }

    [Fact]
    public async Task GetMetadataAsync_NonExistentPath_ThrowsFileNotFound()
    {
        // A missing item must not be reported as an empty file: callers use the
        // exception as their existence probe (404, create-only uploads, sync).
        await Assert.ThrowsAsync<FileNotFoundException>(() => _sut.GetMetadataAsync("nofile.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _sut.GetMetadataAsync("nodir/nofile.txt"));
    }

    [Fact]
    public async Task GetMetadataAsync_WithoutDbProvider_ReturnsEmptyAcl()
    {
        File.WriteAllBytes(Path.Combine(_testRoot, "noacl.txt"), [1]);
        var meta = await _sut.GetMetadataAsync("noacl.txt");
        Assert.NotNull(meta.Acl);
        Assert.Empty(meta.Acl);
    }
}
