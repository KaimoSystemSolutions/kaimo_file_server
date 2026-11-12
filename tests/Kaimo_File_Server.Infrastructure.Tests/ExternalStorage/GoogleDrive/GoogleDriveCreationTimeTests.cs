using Xunit;
using Kaimo_File_Server.Infrastructure.ExternalStorage.GoogleDrive;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage.GoogleDrive;

/// <summary>
/// Drive's <c>createdTime</c> is the first-upload time unless the uploader set it, so a value after
/// the modification time is no creation time.
/// </summary>
public sealed class GoogleDriveCreationTimeTests
{
    private static readonly DateTime Modified = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CreationTime_BeforeTheModifiedTime_IsKept()
    {
        var created = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

        Assert.Equal(created.UtcDateTime, GoogleDriveConnection.ParseCreatedAt(created, Modified));
    }

    [Fact]
    public void CreationTime_EqualToTheModifiedTime_IsKept()
    {
        Assert.Equal(Modified, GoogleDriveConnection.ParseCreatedAt(new DateTimeOffset(Modified), Modified));
    }

    [Fact]
    public void CreationTime_AfterTheModifiedTime_IsDropped()
    {
        // An upload of an older file: createdTime is the upload, modifiedTime the original edit.
        var uploaded = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(GoogleDriveConnection.ParseCreatedAt(uploaded, Modified));
    }

    [Fact]
    public void MissingCreationTime_IsNone()
    {
        Assert.Null(GoogleDriveConnection.ParseCreatedAt(null, Modified));
    }
}
