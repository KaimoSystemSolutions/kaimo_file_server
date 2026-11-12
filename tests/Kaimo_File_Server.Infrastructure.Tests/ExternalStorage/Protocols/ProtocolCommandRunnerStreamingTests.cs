using Kaimo_File_Server.Core.Services.ExternalStorage;
using Xunit;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Protocols;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage.Protocols;

/// <summary>
/// A helper's standard output can be streamed into a target instead of buffered, and a failed
/// helper reports what it printed instead of only its exit code.
/// </summary>
public sealed class ProtocolCommandRunnerStreamingTests
{
    private static (string Executable, string[] Arguments) Script(string powershell, string sh)
        => OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-Command", powershell])
            : ("sh", ["-c", sh]);

    private static Task Stream(
        (string Executable, string[] Arguments) script, Stream target, CancellationToken cancellationToken = default)
        => new ProtocolCommandRunner().StreamOutputAsync(script.Executable, script.Arguments, target, cancellationToken);

    [Fact]
    public async Task StreamOutput_SmallBinaryOutput_ArrivesByteExact()
    {
        using var target = new MemoryStream();

        await Stream(Script(
            "$o=[Console]::OpenStandardOutput(); $o.Write([byte[]](255,0,13,10),0,4); $o.Flush()",
            "printf '\\377\\000\\r\\n'"), target);

        Assert.Equal(new byte[] { 255, 0, 13, 10 }, target.ToArray());
    }

    [Fact]
    public async Task StreamOutput_LargeOutput_ArrivesCompletely()
    {
        using var target = new MemoryStream();

        await Stream(Script(
            "$o=[Console]::OpenStandardOutput(); $b=[byte[]](,98 * 307200); $o.Write($b,0,$b.Length); $o.Flush()",
            "head -c 307200 /dev/zero | tr '\\0' b"), target);

        Assert.Equal(307200, target.Length);
        Assert.All(target.ToArray(), value => Assert.Equal(98, value));
    }

    [Fact]
    public async Task StreamOutput_HelperFailsBeforeAnyData_KeepsItsMessageOutOfTheTarget()
    {
        using var target = new MemoryStream();

        var failure = await Assert.ThrowsAsync<RemoteItemRejectedException>(() => Stream(Script(
            "[Console]::Out.WriteLine('NT_STATUS_OBJECT_NAME_NOT_FOUND opening remote file'); exit 1",
            "echo NT_STATUS_OBJECT_NAME_NOT_FOUND opening remote file; exit 1"), target));

        Assert.Contains("NT_STATUS_OBJECT_NAME_NOT_FOUND", failure.Message);
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public async Task StreamOutput_AccessDenied_IsReportedAsDenied()
    {
        using var target = new MemoryStream();

        await Assert.ThrowsAsync<RemoteStorageAccessDeniedException>(() => Stream(Script(
            "[Console]::Out.WriteLine('NT_STATUS_ACCESS_DENIED opening remote file'); exit 1",
            "echo NT_STATUS_ACCESS_DENIED opening remote file; exit 1"), target));
    }

    [Fact]
    public async Task StreamOutput_HelperFailsMidTransfer_ThrowsWithItsErrorOutput()
    {
        // The regression that matters most: a broken transfer must never look like a complete file.
        using var target = new MemoryStream();

        var failure = await Assert.ThrowsAsync<IOException>(() => Stream(Script(
            "$o=[Console]::OpenStandardOutput(); $b=[byte[]](,98 * 307200); $o.Write($b,0,$b.Length); $o.Flush(); "
            + "[Console]::Error.WriteLine('parallel_read returned NT_STATUS_IO_TIMEOUT'); exit 1",
            "head -c 307200 /dev/zero; echo parallel_read returned NT_STATUS_IO_TIMEOUT >&2; exit 1"), target));

        Assert.Contains("NT_STATUS_IO_TIMEOUT", failure.Message);
        Assert.Contains("exit code 1", failure.Message);
    }

    [Fact]
    public async Task StreamOutput_LargeOutputThenFailure_DoesNotReadTheDataAsMessage()
    {
        using var target = new MemoryStream();

        var failure = await Assert.ThrowsAsync<IOException>(() => Stream(Script(
            "$o=[Console]::OpenStandardOutput(); $b=[byte[]](,98 * 307200); $o.Write($b,0,$b.Length); $o.Flush(); exit 1",
            "head -c 307200 /dev/zero | tr '\\0' b; exit 1"), target));

        Assert.Equal("The protocol helper failed with exit code 1.", failure.Message);
    }

    [Fact]
    public async Task StreamOutput_TargetWriteFails_StopsTheHelperAndRethrows()
    {
        // A full pool: the helper must not keep running against a target nobody reads.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await Assert.ThrowsAsync<IOException>(() => Stream(Script(
            "while ($true) { [Console]::Out.Write('a' * 65536) }", "yes"), new FailingStream(), timeout.Token));

        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task StreamOutput_Cancelled_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        using var target = new CancelOnWriteStream(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stream(Script(
            "while ($true) { [Console]::Out.Write('a' * 65536) }", "yes"), target, cancellation.Token));
    }

    [Fact]
    public void ThrowIfFailed_PrefersTheLastErrorLine()
    {
        var failure = Assert.ThrowsAny<IOException>(() => ProtocolCommandRunner.ThrowIfFailed(
            new ProtocolCommandResult(1, "some listing\n", "first warning\nNT_STATUS_DISK_FULL writing\n\n")));

        Assert.Equal("The protocol helper failed with exit code 1: NT_STATUS_DISK_FULL writing", failure.Message);
    }

    [Theory]
    [InlineData("NT_STATUS_OBJECT_NAME_NOT_FOUND opening remote file \\a.bin")]
    [InlineData("NT_STATUS_OBJECT_PATH_NOT_FOUND opening remote file \\x\\a.bin")]
    [InlineData("NT_STATUS_NO_SUCH_FILE listing \\a.bin")]
    [InlineData("NT_STATUS_FILE_IS_A_DIRECTORY opening remote file \\a")]
    [InlineData("NT_STATUS_DISK_FULL writing")]
    public void ThrowIfFailed_DefinitiveStatus_IsMarkedAsRejected(string line)
    {
        Assert.Throws<RemoteItemRejectedException>(() => ProtocolCommandRunner.ThrowIfFailed(
            new ProtocolCommandResult(1, "", line + "\n")));
    }

    [Theory]
    [InlineData("NT_STATUS_IO_TIMEOUT reading")]
    [InlineData("NT_STATUS_SHARING_VIOLATION opening remote file")]
    [InlineData("NT_STATUS_CONNECTION_RESET")]
    public void ThrowIfFailed_PossiblyTemporaryStatus_StaysARetryableIOException(string line)
    {
        Assert.Throws<IOException>(() => ProtocolCommandRunner.ThrowIfFailed(
            new ProtocolCommandResult(1, "", line + "\n")));
    }

    [Fact]
    public void ThrowIfFailed_FallsBackToStandardOutput()
    {
        // smbclient prints most of its command errors on standard output.
        var failure = Assert.Throws<IOException>(() => ProtocolCommandRunner.ThrowIfFailed(
            new ProtocolCommandResult(1, "NT_STATUS_SHARING_VIOLATION opening remote file\n", "  \n")));

        Assert.Equal(
            "The protocol helper failed with exit code 1: NT_STATUS_SHARING_VIOLATION opening remote file",
            failure.Message);
    }

    [Fact]
    public void ThrowIfFailed_WithoutOutput_ReportsTheExitCodeOnly()
    {
        var failure = Assert.Throws<IOException>(() => ProtocolCommandRunner.ThrowIfFailed(
            new ProtocolCommandResult(7, "", "")));

        Assert.Equal("The protocol helper failed with exit code 7.", failure.Message);
    }

    [Fact]
    public void ThrowIfFailed_CapsAnOverlongLine()
    {
        var failure = Assert.Throws<IOException>(() => ProtocolCommandRunner.ThrowIfFailed(
            new ProtocolCommandResult(1, "", new string('x', 5000))));

        Assert.True(failure.Message.Length < 400, $"Message has {failure.Message.Length} characters.");
    }

    [Fact]
    public void ThrowIfFailed_ExitCodeZero_DoesNotThrow()
        => ProtocolCommandRunner.ThrowIfFailed(new ProtocolCommandResult(0, "", "NT_STATUS_WARNING"));

    private class FailingStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("No space left on device");
    }

    private sealed class CancelOnWriteStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
