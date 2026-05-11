using Kaimo_File_Server.Core.Domain;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class FileVersionTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var now = DateTime.UtcNow;
        var version = new FileVersion(
            filePath: "docs/report.docx",
            snapshotTimestampUtc: now,
            storagePath: "AB/CD/ABCDEF.bin.gz",
            contentHash: "ABCDEF1234567890",
            size: 1024,
            createdBy: "user-123",
            versionNumber: 3);

        Assert.NotEqual(Guid.Empty, version.Id);
        Assert.Equal("docs/report.docx", version.FilePath);
        Assert.Equal(now, version.SnapshotTimestampUtc);
        Assert.Equal("AB/CD/ABCDEF.bin.gz", version.StoragePath);
        Assert.Equal("ABCDEF1234567890", version.ContentHash);
        Assert.Equal(1024, version.Size);
        Assert.Equal("user-123", version.CreatedBy);
        Assert.Equal(3, version.VersionNumber);
    }

    [Fact]
    public void ToGmtToken_FormatsCorrectly()
    {
        var ts = new DateTime(2026, 5, 10, 14, 30, 45, DateTimeKind.Utc);
        var version = new FileVersion(
            "test.txt", ts, "path", "hash", 100, null, 1);

        Assert.Equal("@GMT-2026.05.10-14.30.45", version.ToGmtToken());
    }

    [Fact]
    public void ToGmtToken_MidnightEdgeCase()
    {
        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var version = new FileVersion(
            "test.txt", ts, "path", "hash", 100, null, 1);

        Assert.Equal("@GMT-2026.01.01-00.00.00", version.ToGmtToken());
    }

    [Fact]
    public void ParseGmtToken_ValidToken_ReturnsUtcDateTime()
    {
        var result = FileVersion.ParseGmtToken("@GMT-2026.05.10-14.30.45");

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 5, 10, 14, 30, 45, DateTimeKind.Utc), result.Value);
        Assert.Equal(DateTimeKind.Utc, result.Value.Kind);
    }

    [Fact]
    public void ParseGmtToken_Midnight_ReturnsCorrect()
    {
        var result = FileVersion.ParseGmtToken("@GMT-2026.01.01-00.00.00");

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.Value);
    }

    [Fact]
    public void ParseGmtToken_Null_ReturnsNull()
    {
        Assert.Null(FileVersion.ParseGmtToken(null!));
    }

    [Fact]
    public void ParseGmtToken_Empty_ReturnsNull()
    {
        Assert.Null(FileVersion.ParseGmtToken(""));
    }

    [Fact]
    public void ParseGmtToken_InvalidPrefix_ReturnsNull()
    {
        Assert.Null(FileVersion.ParseGmtToken("@XYZ-2026.05.10-14.30.45"));
    }

    [Fact]
    public void ParseGmtToken_TooShort_ReturnsNull()
    {
        Assert.Null(FileVersion.ParseGmtToken("@GMT-2026.05"));
    }

    [Fact]
    public void ParseGmtToken_InvalidDate_ReturnsNull()
    {
        Assert.Null(FileVersion.ParseGmtToken("@GMT-2026.13.45-99.99.99"));
    }

    [Fact]
    public void ParseGmtToken_JustPrefix_ReturnsNull()
    {
        Assert.Null(FileVersion.ParseGmtToken("@GMT-"));
    }

    [Fact]
    public void Roundtrip_ToGmtToken_ParseGmtToken()
    {
        var ts = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        var version = new FileVersion(
            "test.txt", ts, "path", "hash", 100, null, 1);

        var token = version.ToGmtToken();
        var parsed = FileVersion.ParseGmtToken(token);

        Assert.NotNull(parsed);
        Assert.Equal(ts, parsed.Value);
    }

    [Fact]
    public void CreatedBy_CanBeNull()
    {
        var version = new FileVersion(
            "test.txt", DateTime.UtcNow, "path", "hash", 0, null, 1);

        Assert.Null(version.CreatedBy);
    }

    [Fact]
    public void VersionNumber_StartsAtOne()
    {
        var version = new FileVersion(
            "test.txt", DateTime.UtcNow, "path", "hash", 0, null, 1);

        Assert.Equal(1, version.VersionNumber);
    }

    [Fact]
    public void MultipleVersions_SameFile_DifferentTimestamps()
    {
        var ts1 = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc);

        var v1 = new FileVersion("file.txt", ts1, "p1", "hash1", 100, null, 1);
        var v2 = new FileVersion("file.txt", ts2, "p2", "hash2", 200, null, 2);

        Assert.NotEqual(v1.Id, v2.Id);
        Assert.NotEqual(v1.ToGmtToken(), v2.ToGmtToken());
        Assert.Equal(v1.FilePath, v2.FilePath);
    }
}