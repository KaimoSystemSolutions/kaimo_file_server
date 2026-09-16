using Kaimo_File_Server.Core.Helpers;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests for <see cref="SafeDirectoryWalk"/> — the cycle/depth-safe walker that closes
/// the symlink-cycle StackOverflow crash vector and the archive exfiltration hole.
///
/// Symlink-dependent tests are guarded by <see cref="TryCreateDirectoryLink"/>: creating
/// a directory symlink needs Developer Mode or admin on Windows, so those tests are inert
/// locally on a stock Windows box but a real assertion on the Linux CI runner. xUnit 2.x
/// has no <c>Assert.Skip</c>, hence the early return.
/// </summary>
public class SafeDirectoryWalkTests : IDisposable
{
    private readonly string _root;

    public SafeDirectoryWalkTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "kaimo_walk_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private List<string> WalkFiles(string root, out WalkResult result, int maxDepth = SafeDirectoryWalk.DefaultMaxDepth)
    {
        var files = new List<string>();
        result = SafeDirectoryWalk.EnumerateFiles(root, f => files.Add(f.FullPath), maxDepth);
        return files;
    }

    [Fact]
    public void EnumerateFiles_SymlinkCycle_TerminatesAndReportsSkip()
    {
        // root/sub/real.txt  +  root/sub/loop -> root  (a cycle)
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "real.txt"), "x");
        if (!TryCreateDirectoryLink(Path.Combine(sub, "loop"), _root))
            return; // symlinks unavailable on this host

        var files = WalkFiles(_root, out var result);

        // The key assertion is simply that this returns (no hang, no StackOverflow).
        Assert.Contains(files, f => f.EndsWith("real.txt", StringComparison.Ordinal));
        Assert.Contains(result.Skips, s => s.Reason == WalkSkipReason.ReparsePoint);
    }

    [Fact]
    public void EnumerateFiles_SymlinkToOutsideRoot_IsNotFollowed()
    {
        // A secret outside the walked root, reachable only via a symlink inside it.
        var outside = Path.Combine(Path.GetTempPath(), "kaimo_walk_outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "top-secret");
            if (!TryCreateDirectoryLink(Path.Combine(_root, "escape"), outside))
                return;

            var files = WalkFiles(_root, out var result);

            Assert.DoesNotContain(files, f => f.EndsWith("secret.txt", StringComparison.Ordinal));
            Assert.Contains(result.Skips, s => s.Reason == WalkSkipReason.ReparsePoint);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void EnumerateFiles_DepthBeyondLimit_ReportsDepthLimitSkip()
    {
        // root/a/b/c/deep.txt with maxDepth=2 -> c (depth 3) is never entered.
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(a, "b");
        var c = Path.Combine(b, "c");
        Directory.CreateDirectory(c);
        File.WriteAllText(Path.Combine(a, "shallow.txt"), "x");
        File.WriteAllText(Path.Combine(c, "deep.txt"), "x");

        var files = WalkFiles(_root, out var result, maxDepth: 2);

        Assert.Contains(files, f => f.EndsWith("shallow.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.EndsWith("deep.txt", StringComparison.Ordinal));
        Assert.Contains(result.Skips, s => s.Reason == WalkSkipReason.DepthLimit);
    }

    [Fact]
    public void EnumerateFiles_InaccessibleSubdirectory_IsSkippedNotThrown()
    {
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "hidden.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "x");

        // Making a directory unreadable is only reliable via Unix mode bits. On Windows
        // this is inert; on the Linux CI runner it is a real assertion.
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            // Confirm it is genuinely inaccessible before asserting the walker copes.
            try { Directory.GetFileSystemEntries(locked); return; }
            catch (UnauthorizedAccessException) { /* good, proceed */ }

            var files = WalkFiles(_root, out var result);

            Assert.Contains(files, f => f.EndsWith("visible.txt", StringComparison.Ordinal));
            Assert.Contains(result.Skips, s => s.Reason == WalkSkipReason.Inaccessible);
        }
        finally
        {
            // Restore so the temp dir can be cleaned up.
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
