using System.Globalization;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// smbclient keeps pushed times with <c>utimes &lt;name&gt; &lt;create&gt; &lt;access&gt; &lt;write&gt; &lt;change&gt;</c>
/// ("-1" = unchanged). It converts the wall-clock times with mktime on a struct tm whose
/// <c>tm_isdst</c> is 0, so in a time zone on summer time every time would land one hour off;
/// Kaimo therefore runs it with <c>TZ=UTC0</c> and sends UTC times.
/// </summary>
public sealed class SmbTimestampTests
{
    private static readonly DateTime Modified = new(2026, 8, 3, 12, 0, 5, DateTimeKind.Utc);
    private static readonly DateTime Created = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly List<(string Command, IReadOnlyDictionary<string, string> Environment)> _commands = [];
    private readonly IRemoteFileStore _store;

    public SmbTimestampTests()
    {
        var runner = new Mock<IProtocolCommandRunner>();
        runner.Setup(r => r.RunAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback((string _, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
                    CancellationToken _, bool _) =>
                _commands.Add((arguments[arguments.Count - 1], environment)))
            .ReturnsAsync(new ProtocolCommandResult(0, ""));
        _store = new SmbCommandRemoteFileStore(
            new SmbConnectionSettings("files.example.test"),
            new SmbCredentials("user", "secret", null),
            runner.Object);
    }

    private static string Utc(DateTime utc)
        => utc.ToString("yyyy:MM:dd-HH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public async Task TrySetTimesAsync_SetsCreationAccessAndWriteTime()
    {
        await _store.TrySetTimesAsync("/Team/Folder/a.txt", Modified, Created);

        Assert.Equal(
            $"utimes \"Folder\\a.txt\" {Utc(Created)} {Utc(Modified)} {Utc(Modified)} -1",
            Assert.Single(_commands).Command);
    }

    [Fact]
    public async Task TrySetTimesAsync_RunsSmbclientInUtc()
    {
        // A summer date: with the host's local zone, mktime (tm_isdst = 0) would shift it by the DST offset.
        await _store.TrySetTimesAsync("/Team/a.txt", Modified, Created);

        var environment = Assert.Single(_commands).Environment;
        Assert.Equal("UTC0", environment["TZ"]);
    }

    [Fact]
    public async Task TrySetTimesAsync_ConvertsNonUtcInputToUtc()
    {
        var local = Modified.ToLocalTime();

        await _store.TrySetTimesAsync("/Team/a.txt", local, null);

        Assert.Equal(
            $"utimes \"a.txt\" -1 {Utc(Modified)} {Utc(Modified)} -1",
            Assert.Single(_commands).Command);
    }

    [Fact]
    public async Task TrySetModifiedTimeAsync_LeavesTheCreationTimeUnchanged()
    {
        await _store.TrySetModifiedTimeAsync("/Team/a.txt", Modified);

        Assert.Equal(
            $"utimes \"a.txt\" -1 {Utc(Modified)} {Utc(Modified)} -1",
            Assert.Single(_commands).Command);
    }

    [Fact]
    public async Task TrySetDirectoryTimesAsync_StampsTheFolderIncludingItsCreationTime()
    {
        await _store.TrySetDirectoryTimesAsync("/Team/Folder", Modified, Created);

        Assert.Equal(
            $"utimes \"Folder\" {Utc(Created)} {Utc(Modified)} {Utc(Modified)} -1",
            Assert.Single(_commands).Command);
    }
}
