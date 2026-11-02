using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Xunit;

namespace Kaimo_File_Server.Tests;

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
}
