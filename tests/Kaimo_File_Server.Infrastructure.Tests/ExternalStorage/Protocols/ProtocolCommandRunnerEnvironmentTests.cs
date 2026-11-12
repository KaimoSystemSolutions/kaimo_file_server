using Xunit;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Protocols;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage.Protocols;

/// <summary>The runner passes extra environment variables (e.g. <c>TZ</c> for smbclient) to the helper.</summary>
public sealed class ProtocolCommandRunnerEnvironmentTests
{
    [Fact]
    public async Task RunAsync_WithEnvironment_SetsTheVariablesForTheHelper()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var result = await new ProtocolCommandRunner().RunAsync(
            "sh", ["-c", "printf %s \"$TZ\""],
            new Dictionary<string, string> { ["TZ"] = "UTC0" },
            CancellationToken.None);

        Assert.Equal("UTC0", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_OutputBeyondTheLimit_IsFlaggedAsTruncated()
    {
        // 5M characters, over the 4M the runner keeps.
        var (executable, arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "[Console]::Out.Write('a' * 5000000)" })
            : ("sh", new[] { "-c", "head -c 5000000 /dev/zero | tr '\\0' a" });

        var large = await new ProtocolCommandRunner().RunAsync(executable, arguments, CancellationToken.None);
        var small = await new ProtocolCommandRunner().RunAsync(
            OperatingSystem.IsWindows() ? "cmd" : "sh",
            OperatingSystem.IsWindows() ? ["/c", "echo ok"] : ["-c", "echo ok"],
            CancellationToken.None);

        Assert.True(large.OutputTruncated);
        Assert.Equal(4 * 1024 * 1024, large.StandardOutput.Length);
        Assert.False(small.OutputTruncated);
    }

    [Fact]
    public async Task RunUntilOutputLimit_EndlessOutput_StopsTheHelperAtTheLimit()
    {
        // A helper that never stops writing: draining it would hang until the timeout.
        var (executable, arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "while ($true) { [Console]::Out.Write('a' * 65536) }" })
            : ("sh", new[] { "-c", "yes" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new ProtocolCommandRunner().RunUntilOutputLimitAsync(executable, arguments, timeout.Token);

        Assert.True(result.OutputTruncated);
        Assert.Equal(ProtocolCommandRunner.DefaultMaximumOutputCharacters, result.StandardOutput.Length);
    }

    [Fact]
    public async Task RunUntilOutputLimit_WithItsOwnLimit_StopsThere()
    {
        // Directory listings run with a larger limit than the general one; any limit is honored.
        var (executable, arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "while ($true) { [Console]::Out.Write('a' * 65536) }" })
            : ("sh", new[] { "-c", "yes" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new ProtocolCommandRunner().RunUntilOutputLimitAsync(
            executable, arguments, timeout.Token, maximumOutputCharacters: 100_000);

        Assert.True(result.OutputTruncated);
        Assert.Equal(100_000, result.StandardOutput.Length);
    }

    [Fact]
    public async Task RunUntilOutputLimit_FailingHelper_ReturnsItsExitCodeWithoutThrowing()
    {
        var (executable, arguments) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "echo partial& exit 3" })
            : ("sh", new[] { "-c", "echo partial; exit 3" });

        var result = await new ProtocolCommandRunner().RunUntilOutputLimitAsync(
            executable, arguments, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("partial", result.StandardOutput.Trim());
        Assert.False(result.OutputTruncated);
    }
}
