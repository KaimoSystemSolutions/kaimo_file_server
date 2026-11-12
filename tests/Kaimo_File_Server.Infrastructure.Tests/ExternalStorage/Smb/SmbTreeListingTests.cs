using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Moq;
using Xunit;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Protocols;
using Kaimo_File_Server.Infrastructure.ExternalStorage.Smb;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage.Smb;

/// <summary>
/// A sync lists an SMB tree with few <c>smbclient</c> calls (<c>recurse ON; ls "\folder\*"; pwd</c>,
/// several folders per call) instead of one helper process per folder. Output that is truncated or
/// not fully understood must never turn into a silently incomplete listing, and folders already
/// listed in a truncated call must not be listed again.
/// </summary>
public sealed class SmbTreeListingTests
{
    private const string Date = "Sat Nov  7 10:00:00 2026";
    // smbclient's pwd output, which ends every folder's ls in a tree call.
    private const string Pwd = "Current directory is \\\\files.example.test\\Team\\";

    private readonly List<string> _commands = [];
    // Commands run through the runner's stop-at-output-limit entry point.
    private readonly List<string> _limitedCommands = [];
    // Output limit each stop-at-limit command ran with (null = the runner's default).
    private readonly Dictionary<string, int?> _limits = new();
    private readonly Dictionary<string, ProtocolCommandResult> _results = new();
    // Per-command delay; commands without an entry wait _delay.
    private readonly Dictionary<string, TimeSpan> _delays = new();
    private readonly IRemoteFileStore _store;
    private TimeSpan _delay;
    private int _inFlight;
    private int _maxInFlight;

    public SmbTreeListingTests()
    {
        var runner = new Mock<IProtocolCommandRunner>();
        runner.Setup(r => r.RunAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns((string _, IReadOnlyList<string> arguments, CancellationToken token, bool _) =>
                ResultAsync(arguments[^1], token));
        runner.Setup(r => r.RunUntilOutputLimitAsync(
                "smbclient", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns((string _, IReadOnlyList<string> arguments, CancellationToken token, int? limit) =>
            {
                lock (_limitedCommands)
                {
                    _limitedCommands.Add(arguments[^1]);
                    _limits[arguments[^1]] = limit;
                }
                return ResultAsync(arguments[^1], token);
            });
        _store = new SmbCommandRemoteFileStore(
            new SmbConnectionSettings("files.example.test"),
            new SmbCredentials("user", "secret", null),
            runner.Object);
    }

    private async Task<ProtocolCommandResult> ResultAsync(string command, CancellationToken token)
    {
        lock (_commands)
        {
            _commands.Add(command);
            _maxInFlight = Math.Max(_maxInFlight, ++_inFlight);
        }
        try
        {
            TimeSpan delay = _delays.GetValueOrDefault(command, _delay);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);
            return _results.TryGetValue(command, out var result) ? result : new ProtocolCommandResult(0, "");
        }
        finally
        {
            lock (_commands)
                _inFlight--;
        }
    }

    private static string Entry(string name, string attributes, long size)
        => $"  {name,-36}{attributes,-2}{size,9}  {Date}";

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>The tree call for these folders (share paths such as <c>\Folder\sub</c>; "" = share root).</summary>
    private static string Tree(params string[] folders)
        => "recurse ON; " + string.Join("; ", folders.Select(folder => $"ls \"{folder}\\*\"; pwd"));

    private static readonly string Summary = "\t\t61255652 blocks of size 1024. 23711420 blocks available";

    [Fact]
    public async Task Tree_IsListedWithOneCall_AndKeyedByRelativeFolder()
    {
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry(".", "D", 0), Entry("..", "D", 0),
            Entry("a.txt", "A", 5), Entry("sub", "D", 0),
            "",
            "\\Folder\\sub",
            Entry(".", "D", 0), Entry("..", "D", 0),
            Entry("my file.txt", "A", 3), Entry("deep", "D", 0),
            "",
            "\\Folder\\sub\\deep",
            Entry(".", "D", 0), Entry("..", "D", 0),
            "",
            Summary, Pwd));

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.NotNull(tree);
        Assert.Single(_commands);
        Assert.Equal(["", "sub", "sub/deep"], tree!.Keys.Order());
        Assert.Equal(["a.txt", "sub"], tree[""].Select(i => i.Name));
        var file = Assert.Single(tree["sub"], i => !i.IsDirectory);
        Assert.Equal("my file.txt", file.Name);
        Assert.Equal("/Team/Folder/sub/my file.txt", file.Path);
        Assert.Equal(3, file.Size);
        Assert.Empty(tree["sub/deep"]);
    }

    [Fact]
    public async Task Tree_AtShareRoot_ListsTheShareRootMask()
    {
        _results[Tree("")] = new(0, Lines(
            Entry("sub", "D", 0), "", "\\sub", Entry("b.txt", "A", 1), "", Summary, Pwd));

        var tree = await _store.TryListTreeAsync("/Team");

        Assert.Equal("/Team/sub/b.txt", Assert.Single(tree!["sub"]).Path);
    }

    [Fact]
    public async Task Tree_UnreadableSubfolder_IsLeftOut_AndTheRestKept()
    {
        // A folder smbclient could not read (fully) shows a header and maybe some entries, then
        // the error. Treating it as complete would make the sync delete or re-upload its content,
        // so it is left out (the sync lists it on its own); the readable rest stays usable.
        // ls then fails and prints no summary, which must not discard the whole tree.
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("denied", "D", 0), Entry("broken", "D", 0), Entry("ok", "D", 0),
            "", "\\Folder\\denied",
            "NT_STATUS_ACCESS_DENIED listing \\Folder\\denied\\*",
            "", "\\Folder\\broken",
            Entry("partial.txt", "A", 1),
            "do_list: [\\Folder\\broken\\*] NT_STATUS_CONNECTION_RESET",
            "", "\\Folder\\ok",
            Entry("x.txt", "A", 1),
            Pwd));

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.NotNull(tree);
        Assert.Single(_commands);
        Assert.Equal(["", "ok"], tree!.Keys.Order());
        Assert.Equal(["denied", "broken", "ok"], tree[""].Select(i => i.Name));
        Assert.Equal("x.txt", Assert.Single(tree["ok"]).Name);
    }

    [Theory]
    // The listed folder itself failed: nothing is known about it.
    [InlineData("NT_STATUS_ACCESS_DENIED listing \\Folder\\*")]
    // An error naming another folder than the block it appears in.
    [InlineData("NT_STATUS_ACCESS_DENIED listing \\Folder\\other\\*")]
    // Anything else it does not understand.
    [InlineData("cd \\Folder\\: NT_STATUS_OBJECT_NAME_NOT_FOUND")]
    [InlineData("something unexpected")]
    public async Task Tree_WithAnUnknownOrRootError_IsNotOffered(string errorLine)
    {
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("sub", "D", 0), errorLine, "", "\\Folder\\sub", Entry("a.txt", "A", 1), "", Summary, Pwd));

        Assert.Null(await _store.TryListTreeAsync("/Team/Folder"));
    }

    [Fact]
    public async Task Tree_FailedExit_IsNotOffered()
    {
        // pwd is the last command, so a helper that ran everything exits with 0.
        _results[Tree("\\Folder")] = new(1, Lines(Entry("a.txt", "A", 1), "", Summary, Pwd));

        Assert.Null(await _store.TryListTreeAsync("/Team/Folder"));
    }

    [Fact]
    public async Task Tree_IsListedThroughTheStopAtOutputLimitRunner_WithItsOwnSmallerLimit()
    {
        // A truncated call loses nothing, so a limit below the runner's general one lets a large
        // tree go parallel early instead of walking it in one session for many minutes.
        _results[Tree("\\Folder")] = new(0, Lines(Entry("a.txt", "A", 1), "", Summary, Pwd));

        await _store.TryListTreeAsync("/Team/Folder");

        Assert.Equal([Tree("\\Folder")], _limitedCommands);
        Assert.InRange(_limits[Tree("\\Folder")] ?? int.MaxValue, 1, ProtocolCommandRunner.DefaultMaximumOutputCharacters - 1);
    }

    [Fact]
    public async Task Tree_Truncated_KeepsCompleteFolders_AndListsOnlyTheRest()
    {
        // smbclient lists breadth first: a folder's block is complete once the next header follows.
        // Here the root and "s1" are complete, "s2" was cut, "s3" and "s1/deep" were not reached.
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("a.txt", "A", 5), Entry("s1", "D", 0), Entry("s2", "D", 0), Entry("s3", "D", 0),
            "", "\\Folder\\s1",
            Entry("b.txt", "A", 2), Entry("deep", "D", 0),
            "", "\\Folder\\s2",
            Entry("cut-short.txt", "A", 1)) + "  partial-li", OutputTruncated: true);
        _results[Tree("\\Folder\\s2")] = new(0, Lines(Entry("c.txt", "A", 3), "", Summary, Pwd));
        _results[Tree("\\Folder\\s3")] = new(0, Lines(Entry("d.txt", "A", 4), "", Summary, Pwd));
        _results[Tree("\\Folder\\s1\\deep")] = new(0, Lines(Entry("e.txt", "A", 5), "", Summary, Pwd));

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.NotNull(tree);
        Assert.Equal(["", "s1", "s1/deep", "s2", "s3"], tree!.Keys.Order());
        Assert.Equal(["c.txt"], tree["s2"].Select(i => i.Name)); // the cut block is not kept
        Assert.Equal(4, _commands.Count);
        Assert.DoesNotContain(_commands, command => command.Contains("ls \"\\Folder\\*\"") && command != Tree("\\Folder"));
        Assert.DoesNotContain(_commands, command => command.Contains("\\Folder\\s1\\*"));
    }

    [Fact]
    public async Task Tree_RestCalls_UseASmallerLimitThanTheFirstCall()
    {
        // A rest call holding one large subtree would otherwise run alone for minutes while the
        // other lanes idle; a smaller limit splits it again sooner. The first call keeps the larger
        // one, so small trees still need a single call.
        _results[Tree("\\Folder")] = new(0, Lines(Entry("s1", "D", 0), "", "\\Folder\\s1"), OutputTruncated: true);
        _results[Tree("\\Folder\\s1")] = new(0, Lines(Entry("f.txt", "A", 1), "", Summary, Pwd));

        await _store.TryListTreeAsync("/Team/Folder");

        Assert.True(_limits[Tree("\\Folder\\s1")] < _limits[Tree("\\Folder")],
            $"rest call limit {_limits[Tree("\\Folder\\s1")]} is not below {_limits[Tree("\\Folder")]}");
    }

    [Fact]
    public async Task Tree_TooLarge_ListsTheRestInParallel()
    {
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("s1", "D", 0), Entry("s2", "D", 0), Entry("s3", "D", 0), Entry("s4", "D", 0),
            "", "\\Folder\\s1"), OutputTruncated: true);
        for (int i = 1; i <= 4; i++)
            _results[Tree($"\\Folder\\s{i}")] = new(0, Lines(Entry($"f{i}.txt", "A", i), "", Summary, Pwd));
        _delay = TimeSpan.FromMilliseconds(80);

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.Equal(["", "s1", "s2", "s3", "s4"], tree!.Keys.Order());
        Assert.Equal("f3.txt", Assert.Single(tree["s3"]).Name);
        Assert.True(_maxInFlight > 1, $"the rest ran one call at a time ({_maxInFlight})");
    }

    [Fact]
    public async Task Tree_ManyRemainingFolders_ShareFourCalls()
    {
        // One helper per folder would pay a connection setup per folder again; the remaining
        // folders are spread over at most four calls, each listing several of them in a row.
        _results[Tree("\\Folder")] = new(0, Lines(
            Enumerable.Range(1, 8).Select(i => Entry($"s{i}", "D", 0)).Append("").Append("\\Folder\\s1").ToArray()),
            OutputTruncated: true);
        for (int i = 1; i <= 8; i += 2)
            _results[Tree($"\\Folder\\s{i}", $"\\Folder\\s{i + 1}")] = new(0, Lines(
                Entry($"f{i}.txt", "A", 1), "", Summary, Pwd,
                Entry($"f{i + 1}.txt", "A", 1), "", Summary, Pwd));

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.NotNull(tree);
        Assert.Equal(5, _commands.Count);
        Assert.Equal(9, tree!.Count);
        Assert.Equal("f6.txt", Assert.Single(tree["s6"]).Name);
    }

    [Fact]
    public async Task Tree_CallCutBeforeLaterFolders_ListsThoseAgain()
    {
        _results[Tree("\\Folder")] = new(0, Lines(
            Enumerable.Range(1, 8).Select(i => Entry($"s{i}", "D", 0)).Append("").Append("\\Folder\\s1").ToArray()),
            OutputTruncated: true);
        // s1 done, s2 cut in its own block: s2 is listed again, not left out.
        _results[Tree("\\Folder\\s1", "\\Folder\\s2")] = new(0, Lines(
            Entry("f1.txt", "A", 1), "", Summary, Pwd, Entry("cut.txt", "A", 1)), OutputTruncated: true);
        _results[Tree("\\Folder\\s2")] = new(0, Lines(Entry("f2.txt", "A", 1), "", Summary, Pwd));
        for (int i = 3; i <= 8; i += 2)
            _results[Tree($"\\Folder\\s{i}", $"\\Folder\\s{i + 1}")] = new(0, Lines(
                Entry($"f{i}.txt", "A", 1), "", Summary, Pwd,
                Entry($"f{i + 1}.txt", "A", 1), "", Summary, Pwd));

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.NotNull(tree);
        Assert.Equal("f1.txt", Assert.Single(tree!["s1"]).Name);
        Assert.Equal("f2.txt", Assert.Single(tree["s2"]).Name);
        Assert.Equal(9, tree.Count);
    }

    [Fact]
    public async Task Tree_RemainingFolderThatFails_IsLeftOut_AndTheRestKept()
    {
        // Like an unreadable subfolder in one call: the sync lists it on its own and reports it.
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("gone", "D", 0), Entry("ok", "D", 0), "", "\\Folder\\gone"), OutputTruncated: true);
        _results[Tree("\\Folder\\gone")] = new(0, Lines(
            "NT_STATUS_OBJECT_NAME_NOT_FOUND listing \\Folder\\gone\\*", Pwd));
        _results[Tree("\\Folder\\ok")] = new(0, Lines(Entry("x.txt", "A", 1), "", Summary, Pwd));

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.NotNull(tree);
        Assert.Equal(["", "ok"], tree!.Keys.Order());
    }

    [Fact]
    public async Task Tree_FinishedCallMissingASubfolder_IsNotOffered()
    {
        // A call that ran to its end lists every subfolder it found; one without a block means the
        // output was not understood (a name the header shows differently, say).
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("sub", "D", 0), Entry("other", "D", 0), "", "\\Folder\\sub", Entry("a.txt", "A", 1), "", Summary, Pwd));

        Assert.Null(await _store.TryListTreeAsync("/Team/Folder"));
    }

    [Fact]
    public async Task Tree_WithAHeaderOutsideTheFolder_IsNotOffered()
    {
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("sub", "D", 0), "", "\\Other\\sub", Entry("x.txt", "A", 1), "", Summary, Pwd));

        Assert.Null(await _store.TryListTreeAsync("/Team/Folder"));
    }

    [Fact]
    public async Task Tree_FolderTooLargeForOneCall_IsListedAloneThenItsSubfolders()
    {
        // Not even the folder's own block fits: it is listed alone (with the larger listing limit).
        _results[Tree("\\Folder")] = new(0, Lines(Entry("partial", "A", 1)), OutputTruncated: true);
        _results["cd \"Folder\"; ls"] = new(0, Lines(Entry("a.txt", "A", 5), Entry("sub", "D", 0)));
        _results[Tree("\\Folder\\sub")] = new(0, Lines(Entry("b.txt", "A", 2), "", Summary, Pwd));

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.Equal(["", "sub"], tree!.Keys.Order());
        Assert.Equal(["a.txt", "sub"], tree[""].Select(i => i.Name));
        Assert.Equal("/Team/Folder/sub/b.txt", Assert.Single(tree["sub"]).Path);
    }

    [Fact]
    public async Task List_TruncatedOutput_Throws()
    {
        // Before: the helper output was cut at 4M characters and the rest silently dropped.
        _results["cd \"Folder\"; ls"] = new(0, Lines(Entry("a.txt", "A", 5)), OutputTruncated: true);

        // Not an IOException: the sync retries those as transient, and every retry would run
        // the same oversized listing again.
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ListAsync("/Team/Folder"));
    }

    [Theory]
    [InlineData("NT_STATUS_OBJECT_NAME_NOT_FOUND", typeof(DirectoryNotFoundException))]
    [InlineData("NT_STATUS_OBJECT_PATH_NOT_FOUND", typeof(DirectoryNotFoundException))]
    [InlineData("NT_STATUS_ACCESS_DENIED", typeof(RemoteStorageAccessDeniedException))]
    [InlineData("NT_STATUS_IO_TIMEOUT", typeof(IOException))]
    public async Task List_FailedCd_ThrowsInsteadOfListingTheShareRoot(string status, Type expected)
    {
        // smbclient runs the next -c command after a failed cd, in the old directory, and exits
        // with that command's code: the share root would be returned as the folder's content.
        _results["cd \"Folder\\gone\"; ls"] = new(0, Lines(
            $"cd \\Folder\\gone\\: {status}", Entry("root-file.txt", "A", 5), "", Summary));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => _store.ListAsync("/Team/Folder/gone"));

        Assert.IsType(expected, exception);
    }

    [Fact]
    public async Task SyncAdapter_ForwardsTreeListingAndConcurrency()
    {
        _results[Tree("")] = new(0, Lines(
            Entry("sub", "D", 0), "", "\\sub", Entry("b.txt", "A", 1), "", Summary, Pwd));
        Core.Services.CloudSync.ICloudConnection adapter = new RemoteFileStoreSyncAdapter("SMB", _store);

        var tree = await adapter.TryListTreeAsync("/Team");

        Assert.Equal(4, adapter.ListConcurrency);
        var file = Assert.Single(tree!["SUB"]); // keys match case-insensitively, like the engine's cache
        Assert.Equal(("b.txt", 1L), (file.Name, file.Size));
    }

    [Theory]
    [InlineData(137)] // killed (SIGKILL, e.g. by the OOM killer)
    [InlineData(139)] // crashed (SIGSEGV)
    [InlineData(0)]
    public async Task Tree_HelperEndedBeforeItsLastPwd_IsNotOffered(int exitCode)
    {
        // The output ends on a line boundary, so the last folder looks complete. Only the closing
        // pwd shows that smbclient finished it; a killed or crashed helper may have stopped in the
        // middle of that folder.
        _results[Tree("\\Folder")] = new(exitCode, Lines(
            Entry("denied", "D", 0), Entry("big", "D", 0),
            "", "\\Folder\\denied",
            "NT_STATUS_ACCESS_DENIED listing \\Folder\\denied\\*",
            "", "\\Folder\\big",
            Entry("first-of-many.txt", "A", 1)));

        Assert.Null(await _store.TryListTreeAsync("/Team/Folder"));
    }

    [Fact]
    public async Task Tree_FinishedWithoutSummaryOrKnownError_IsNotOffered()
    {
        // smbclient prints the disk summary only after the whole listing succeeded.
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("sub", "D", 0), "", "\\Folder\\sub", Entry("a.txt", "A", 1), Pwd));

        Assert.Null(await _store.TryListTreeAsync("/Team/Folder"));
    }

    [Theory]
    [InlineData("  b.txt                               A         1  Sat Nov  7 10:00:00 2026")]
    [InlineData("\\Folder\\late")]
    public async Task Tree_OutputBetweenSummaryAndPwd_IsNotOffered(string line)
    {
        _results[Tree("\\Folder")] = new(0, Lines(Entry("a.txt", "A", 1), "", Summary, line, Pwd));

        Assert.Null(await _store.TryListTreeAsync("/Team/Folder"));
    }

    [Fact]
    public async Task Tree_OutputAfterTheLastPwd_IsNotOffered()
    {
        _results[Tree("\\Folder")] = new(0, Lines(Entry("a.txt", "A", 1), "", Summary, Pwd, Entry("b.txt", "A", 1)));

        Assert.Null(await _store.TryListTreeAsync("/Team/Folder"));
    }

    [Fact]
    public async Task Tree_FolderNameWithTrailingSpace_KeepsItsOwnKey()
    {
        // Samba on Linux allows "x " next to "x". Trimming the header would file the content of
        // "x " under "x" (unreadable here), and the sync would reconcile "x" against it.
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("x", "D", 0),
            "", "\\Folder\\x",
            "NT_STATUS_ACCESS_DENIED listing \\Folder\\x\\*",
            "", "\\Folder\\x ",
            Entry("other.txt", "A", 1),
            Pwd));

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.NotNull(tree);
        Assert.False(tree!.ContainsKey("x"));
        Assert.Equal("other.txt", Assert.Single(tree["x "]).Name);
    }

    [Fact]
    public async Task Tree_NestedTruncatedCalls_ShareOneHelperLimit()
    {
        // Every truncated call splits its rest into up to four calls. Without one limit shared by
        // all of them the helper sessions multiply (4, 16, …), past what e.g. Windows client
        // editions accept.
        _results[Tree("\\Folder")] = new(0, Lines(
            Enumerable.Range(1, 4).Select(i => Entry($"s{i}", "D", 0)).Append("").Append("\\Folder\\s1").ToArray()),
            OutputTruncated: true);
        for (int i = 1; i <= 4; i++)
        {
            _results[Tree($"\\Folder\\s{i}")] = new(0, Lines(
                Enumerable.Range(1, 4).Select(j => Entry($"t{j}", "D", 0)).Append("").Append($"\\Folder\\s{i}\\t1").ToArray()),
                OutputTruncated: true);
            for (int j = 1; j <= 4; j++)
                _results[Tree($"\\Folder\\s{i}\\t{j}")] = new(0, Lines(Entry("f.txt", "A", 1), "", Summary, Pwd));
        }
        _delay = TimeSpan.FromMilliseconds(40);

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.Equal(21, tree!.Count); // the folder, 4 children, 16 grandchildren
        Assert.InRange(_maxInFlight, 2, 4);
    }

    [Fact]
    public async Task Tree_RestNotUnderstood_StopsTheRemainingCalls()
    {
        // The whole tree is discarded then (the sync falls back to its per-folder walk), so
        // listing the other folders would only waste helper runs.
        _results[Tree("\\Folder")] = new(0, Lines(
            Enumerable.Range(1, 8).Select(i => Entry($"s{i}", "D", 0)).Append("").Append("\\Folder\\s1").ToArray()),
            OutputTruncated: true);
        string first = Tree("\\Folder\\s1", "\\Folder\\s2");
        _results[first] = new(0, Lines("something unexpected"));
        _delay = TimeSpan.FromSeconds(5);
        _delays[Tree("\\Folder")] = TimeSpan.Zero;
        _delays[first] = TimeSpan.Zero;
        var started = DateTime.UtcNow;

        Assert.Null(await _store.TryListTreeAsync("/Team/Folder"));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4), "running listings were not stopped");
    }

    [Fact]
    public async Task List_LargeFolder_IsReadUpToTheListingLimit_NotTheGeneralOne()
    {
        // A folder with ~50,000 entries passes the runner's general 4M-character limit and must
        // still list completely; the helper is stopped at the (larger) listing limit, not drained.
        _results["cd \"Folder\"; ls"] = new(0, Lines(Entry("a.txt", "A", 5), "", Summary));

        var items = await _store.ListAsync("/Team/Folder");

        Assert.Equal("a.txt", Assert.Single(items).Name);
        Assert.Contains("cd \"Folder\"; ls", _limitedCommands);
        Assert.True(_limits["cd \"Folder\"; ls"] > ProtocolCommandRunner.DefaultMaximumOutputCharacters);
    }

    [Theory]
    [InlineData("NT_STATUS_ACCESS_DENIED listing \\Folder\\*", typeof(RemoteStorageAccessDeniedException))]
    [InlineData("NT_STATUS_CONNECTION_RESET", typeof(IOException))]
    public async Task List_FailedHelper_Throws(string error, Type expected)
    {
        _results["cd \"Folder\"; ls"] = new(1, "", error);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => _store.ListAsync("/Team/Folder"));

        Assert.IsType(expected, exception);
    }

    [Fact]
    public async Task Tree_Truncated_StopsAtAFolderLinkBackToAnAncestor()
    {
        // "Folder\sub\up" is a symbolic link to "Folder" that the server follows: the loop repeats
        // down to the server's path length limit, so without a guard every level costs a call.
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("a.txt", "A", 5), Entry("sub", "D", 0),
            "", "\\Folder\\sub",
            Entry("b.txt", "A", 2), Entry("up", "D", 0),
            "", "\\Folder\\sub\\up",
            Entry("a.txt", "A", 5), Entry("sub", "D", 0),
            "", "\\Folder\\sub\\up\\sub",
            Entry("b.txt", "A", 2)), OutputTruncated: true);

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.NotNull(tree);
        Assert.Single(_commands);
        // The repeating folder stays in the tree with its listing, so the sync recognizes it too.
        Assert.Equal(["", "sub", "sub/up"], tree!.Keys.Order());
    }

    [Fact]
    public async Task Tree_RemainingFolderWithAnUnlistableName_IsLeftOut()
    {
        // smbclient splits -c commands at ';' even inside quotes, so such a folder cannot be named
        // in a call; it is left out (the sync lists it on its own) instead of breaking the call.
        _results[Tree("\\Folder")] = new(0, Lines(
            Entry("a;b", "D", 0), Entry("ok", "D", 0), "", "\\Folder\\a;b"), OutputTruncated: true);
        _results[Tree("\\Folder\\ok")] = new(0, Lines(Entry("x.txt", "A", 1), "", Summary, Pwd));

        var tree = await _store.TryListTreeAsync("/Team/Folder");

        Assert.NotNull(tree);
        Assert.Equal(["", "ok"], tree!.Keys.Order());
        Assert.DoesNotContain(_commands, command => command.Contains("a;b"));
    }

    [Fact]
    public async Task Tree_AtServerRoot_IsNotOffered()
    {
        Assert.Null(await _store.TryListTreeAsync("/"));
        Assert.Empty(_commands);
    }
}
