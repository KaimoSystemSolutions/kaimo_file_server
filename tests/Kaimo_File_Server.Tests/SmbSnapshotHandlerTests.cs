using Kaimo_File_Server.Smb;
using System.Text;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class SmbSnapshotHandlerTests
{
    // ═══════════════════════════════════════════
    //  FSCTL Response Building
    // ═══════════════════════════════════════════

    [Fact]
    public void BuildEnumerateSnapshotsResponse_EmptyList_ReturnsValidHeader()
    {
        var response = SmbSnapshotHandler.BuildEnumerateSnapshotsResponse(new List<DateTime>());

        // Header: 3x uint32 = 12 bytes minimum
        Assert.True(response.Length >= 12);

        var count = BitConverter.ToUInt32(response, 0);
        var returned = BitConverter.ToUInt32(response, 4);

        Assert.Equal(0u, count);
        Assert.Equal(0u, returned);
    }

    [Fact]
    public void BuildEnumerateSnapshotsResponse_SingleTimestamp_CorrectFormat()
    {
        var timestamps = new List<DateTime>
        {
            new(2026, 5, 10, 14, 30, 0, DateTimeKind.Utc)
        };

        var response = SmbSnapshotHandler.BuildEnumerateSnapshotsResponse(timestamps);

        var count = BitConverter.ToUInt32(response, 0);
        var returned = BitConverter.ToUInt32(response, 4);
        var arraySize = BitConverter.ToUInt32(response, 8);

        Assert.Equal(1u, count);
        Assert.Equal(1u, returned);
        Assert.True(arraySize > 0);

        // Extract the snapshot array (Unicode)
        var arrayBytes = new byte[arraySize];
        Array.Copy(response, 12, arrayBytes, 0, (int)arraySize);
        var arrayString = Encoding.Unicode.GetString(arrayBytes);

        Assert.Contains("@GMT-2026.05.10-14.30.00", arrayString);
    }

    [Fact]
    public void BuildEnumerateSnapshotsResponse_MultipleTimestamps_AllPresent()
    {
        var timestamps = new List<DateTime>
        {
            new(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc),
            new(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc),
            new(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
        };

        var response = SmbSnapshotHandler.BuildEnumerateSnapshotsResponse(timestamps);

        var count = BitConverter.ToUInt32(response, 0);
        Assert.Equal(3u, count);

        var arraySize = BitConverter.ToUInt32(response, 8);
        var arrayBytes = new byte[arraySize];
        Array.Copy(response, 12, arrayBytes, 0, (int)arraySize);
        var arrayString = Encoding.Unicode.GetString(arrayBytes);

        // Timestamps should be in descending order (newest first)
        Assert.Contains("@GMT-2026.05.10-12.00.00", arrayString);
        Assert.Contains("@GMT-2026.05.10-11.00.00", arrayString);
        Assert.Contains("@GMT-2026.05.10-10.00.00", arrayString);

        // Newest should come first
        var pos12 = arrayString.IndexOf("@GMT-2026.05.10-12.00.00");
        var pos10 = arrayString.IndexOf("@GMT-2026.05.10-10.00.00");
        Assert.True(pos12 < pos10, "Newest timestamp should come first");
    }

    [Fact]
    public void BuildEnumerateSnapshotsResponse_NullTerminated()
    {
        var timestamps = new List<DateTime>
        {
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var response = SmbSnapshotHandler.BuildEnumerateSnapshotsResponse(timestamps);

        var arraySize = BitConverter.ToUInt32(response, 8);
        var arrayBytes = new byte[arraySize];
        Array.Copy(response, 12, arrayBytes, 0, (int)arraySize);
        var arrayString = Encoding.Unicode.GetString(arrayBytes);

        // Should end with double null (string null + array null)
        Assert.True(arrayString.EndsWith("\0\0"),
            "Snapshot array must end with double null terminator");
    }

    // ═══════════════════════════════════════════
    //  IsSnapshotPath
    // ═══════════════════════════════════════════

    [Theory]
    [InlineData(@"@GMT-2026.05.10-14.30.00\docs\file.txt", true)]
    [InlineData(@"@GMT-2026.05.10-14.30.00", true)]
    [InlineData(@"docs\file.txt", false)]
    [InlineData(@"", false)]
    [InlineData(@"normal\path\file.txt", false)]
    [InlineData(@"file_with_@GMT-in_name.txt", true)] // contains @GMT-
    public void IsSnapshotPath_DetectsCorrectly(string path, bool expected)
    {
        Assert.Equal(expected, SmbSnapshotHandler.IsSnapshotPath(path));
    }

    // ═══════════════════════════════════════════
    //  ParseSnapshotPath
    // ═══════════════════════════════════════════

    [Fact]
    public void ParseSnapshotPath_FullPath_ParsesCorrectly()
    {
        var result = SmbSnapshotHandler.ParseSnapshotPath(
            @"@GMT-2026.05.10-14.30.45\docs\report.docx");

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 5, 10, 14, 30, 45, DateTimeKind.Utc),
            result.SnapshotTimestamp);
        Assert.Equal(@"docs\report.docx", result.RealPath);
        Assert.Equal("", result.Prefix);
        Assert.Equal("@GMT-2026.05.10-14.30.45", result.GmtToken);
    }

    [Fact]
    public void ParseSnapshotPath_RootOnly_EmptyRealPath()
    {
        var result = SmbSnapshotHandler.ParseSnapshotPath(
            "@GMT-2026.05.10-14.30.45");

        Assert.NotNull(result);
        Assert.Equal("", result.RealPath);
    }

    [Fact]
    public void ParseSnapshotPath_WithTrailingSeparator_EmptyRealPath()
    {
        var result = SmbSnapshotHandler.ParseSnapshotPath(
            @"@GMT-2026.05.10-14.30.45\");

        Assert.NotNull(result);
        Assert.Equal("", result.RealPath);
    }

    [Fact]
    public void ParseSnapshotPath_UnixSeparators_Works()
    {
        var result = SmbSnapshotHandler.ParseSnapshotPath(
            "@GMT-2026.05.10-14.30.45/docs/file.txt");

        Assert.NotNull(result);
        Assert.Equal(@"docs\file.txt", result.RealPath);
    }

    [Fact]
    public void ParseSnapshotPath_WithPrefix_ParsesPrefix()
    {
        var result = SmbSnapshotHandler.ParseSnapshotPath(
            @"share\@GMT-2026.05.10-14.30.45\file.txt");

        Assert.NotNull(result);
        Assert.Equal("share", result.Prefix);
        Assert.Equal("file.txt", result.RealPath);
    }

    [Fact]
    public void ParseSnapshotPath_InvalidToken_ReturnsNull()
    {
        var result = SmbSnapshotHandler.ParseSnapshotPath(
            @"@GMT-INVALID\file.txt");

        Assert.Null(result);
    }

    [Fact]
    public void ParseSnapshotPath_NoGmtToken_ReturnsNull()
    {
        var result = SmbSnapshotHandler.ParseSnapshotPath(
            @"normal\path\file.txt");

        Assert.Null(result);
    }

    [Fact]
    public void ParseSnapshotPath_EmptyString_ReturnsNull()
    {
        Assert.Null(SmbSnapshotHandler.ParseSnapshotPath(""));
    }

    [Fact]
    public void ParseSnapshotPath_DeepNestedPath_ParsesCorrectly()
    {
        var result = SmbSnapshotHandler.ParseSnapshotPath(
            @"@GMT-2026.05.10-14.30.45\a\b\c\d\deep.txt");

        Assert.NotNull(result);
        Assert.Equal(@"a\b\c\d\deep.txt", result.RealPath);
    }

    [Fact]
    public void ParseSnapshotPath_MidnightTimestamp_ParsesCorrectly()
    {
        var result = SmbSnapshotHandler.ParseSnapshotPath(
            @"@GMT-2026.01.01-00.00.00\file.txt");

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            result.SnapshotTimestamp);
    }

    // ═══════════════════════════════════════════
    //  FSCTL Constant
    // ═══════════════════════════════════════════

    [Fact]
    public void FSCTL_SRV_ENUMERATE_SNAPSHOTS_HasCorrectValue()
    {
        Assert.Equal(0x00144064u, SmbSnapshotHandler.FSCTL_SRV_ENUMERATE_SNAPSHOTS);
    }
}