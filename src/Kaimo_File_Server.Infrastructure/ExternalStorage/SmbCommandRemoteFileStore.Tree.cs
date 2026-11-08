using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

// Whole-tree listing for sync: a few smbclient calls instead of one process per folder.
internal sealed partial class SmbCommandRemoteFileStore
{
    // Listing helpers one store runs at once; each is its own SMB session.
    private const int ParallelListings = 4;

    // Output of the first tree call. A tree too large for it is cut there and its rest spread over up
    // to ParallelListings calls; nothing listed before the cut is lost. Small enough that a large tree
    // goes parallel early on a slow server, large enough that most trees still need one call.
    private const int TreeListingOutputCharacters = 1024 * 1024;

    // Output of each rest call. Smaller, so a call holding one large subtree is split again soon
    // instead of running alone for minutes while the other lanes idle.
    // ponytail: fixed size; split by elapsed time if one helper per 256K proves too many sessions.
    private const int RestListingOutputCharacters = 256 * 1024;

    // All commands of a call travel in one argument, which Linux caps at 128 KiB.
    private const int MaxTreeCommandCharacters = 64 * 1024;

    // What pwd prints. It follows every folder's ls in a tree call and marks where that output ends.
    private const string FolderEnd = "Current directory is ";

    // Shared by every call of a tree listing: the rest of each truncated call is split again, which
    // would otherwise multiply the sessions past what e.g. Windows client editions accept (about 20).
    private readonly SemaphoreSlim _treeListingGate = new(ParallelListings, ParallelListings);

    // Fallback per-folder listings each start their own smbclient process; a few may run at once.
    public int ListConcurrency => ParallelListings;

    /// <summary>
    /// Lists a folder tree with <c>recurse ON; ls</c>: one helper process (connect, authenticate,
    /// encrypt) for the whole tree instead of one per folder. A call cut at its output limit keeps
    /// every folder it completed; the folders it did not get to are spread over up to four further
    /// calls, each listing several of them with their subtrees. A subfolder smbclient reports as
    /// unreadable is left out (the sync lists it on its own); output that is not fully understood
    /// yields <c>null</c>, so the sync falls back to its per-folder walk.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<RemoteStorageItem>>?> TryListTreeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string normalized = Normalize(path);
        if (normalized == "/")
            return null; // The server root lists shares, not folders.
        (string share, string root) = Split(normalized);
        var tree = new ConcurrentDictionary<string, IReadOnlyList<RemoteStorageItem>>(StringComparer.OrdinalIgnoreCase);
        // Cancelled as soon as any part of the tree turns out unusable: the whole tree is discarded
        // then, so the calls still waiting or running would only waste helper runs.
        using var discard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return await ListFoldersAsync(new TreeListing(share, root, tree, discard), [""]) ? tree : null;
        }
        catch (OperationCanceledException) when (discard.IsCancellationRequested
                                                  && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists <paramref name="folders"/> (relative to the tree root) with their subtrees in one helper
    /// call, then whatever a cut call did not get to with further calls.
    /// </summary>
    private async Task<bool> ListFoldersAsync(TreeListing listing, IReadOnlyList<string> folders)
    {
        CancellationToken token = listing.Discard.Token;
        List<string>? rest;
        // Held only for this call's own helper runs, never while the rest is listed.
        await _treeListingGate.WaitAsync(token);
        try
        {
            ProtocolCommandResult result = await RunAsync(
                listing.Share, TreeCommand(listing.Root, folders), false, token,
                stopAtOutputLimit: true,
                // Only the first call lists the tree root ("").
                outputLimit: folders[0].Length == 0 ? TreeListingOutputCharacters : RestListingOutputCharacters);
            TreeBatch? batch = TryParseBatch(listing, folders, result);
            if (batch is { Truncated: true, Listed.Count: 0, Unreadable.Count: 0 })
            {
                // Not even the first folder's own entries fit: that folder alone, then its subfolders.
                string folder = JoinPath(listing.Root, folders[0]);
                batch.Listed.Add((folders[0], await ListAsync(JoinPath("/" + listing.Share, folder), token)));
                batch.Retry.Remove(folders[0]);
            }
            rest = batch is null ? null : TryCommit(listing, batch);
            if (rest is null)
            {
                // Cancelled before the gate is released, so no waiting call starts.
                listing.Discard.Cancel();
                return false;
            }
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            listing.Discard.Cancel();
            throw;
        }
        finally
        {
            _treeListingGate.Release();
        }

        if (rest.Count == 0)
            return true;
        bool[] complete = await Task.WhenAll(SplitFolders(listing.Root, rest)
            .Select(next => ListFoldersAsync(listing, next)));
        return complete.All(part => part);
    }

    /// <summary>
    /// Adds a call's folders to the tree and returns the folders still to list: the ones it did not
    /// reach and the subfolders without a listing of their own. <c>null</c> when it contradicts the
    /// tree, or when a call that ran to its end left a subfolder out.
    /// </summary>
    private static List<string>? TryCommit(TreeListing listing, TreeBatch batch)
    {
        foreach (var (key, items) in batch.Listed)
            if (!listing.Tree.TryAdd(key, items))
                return null;

        List<string> rest = [.. batch.Retry];
        foreach (var (key, items) in batch.Listed)
        {
            // A symbolic link back to an ancestor would be listed again on every level down to the
            // server's path limit. Its listing stays in the tree: the sync recognizes it and skips it.
            if (RepeatsAncestor(listing.Tree, key, items))
                continue;
            rest.AddRange(items
                .Where(item => item.IsDirectory && item.Name.IndexOfAny(['"', ';']) < 0)
                .Select(item => JoinPath(key, item.Name))
                .Where(child => !listing.Tree.ContainsKey(child) && !batch.Unreadable.Contains(child)));
        }
        return batch.Truncated || rest.Count == 0 ? rest : null;
    }

    private static bool RepeatsAncestor(
        ConcurrentDictionary<string, IReadOnlyList<RemoteStorageItem>> tree,
        string key,
        IReadOnlyList<RemoteStorageItem> items)
    {
        if (key.Length == 0)
            return false;
        string[] segments = key.Split('/');
        for (int depth = 0; depth < segments.Length; depth++)
            if (tree.TryGetValue(string.Join('/', segments[..depth]), out var ancestor)
                && FolderLoop.Repeats(items, ancestor, item => (item.Name, item.IsDirectory, item.Size, item.ModifiedAtUtc)))
                return true;
        return false;
    }

    /// <summary>Spreads folders over up to <see cref="ParallelListings"/> calls, keeping neighbors together.</summary>
    private static IEnumerable<IReadOnlyList<string>> SplitFolders(string root, List<string> folders)
    {
        int perCall = (folders.Count + ParallelListings - 1) / ParallelListings;
        List<string> call = [];
        int length = 0;
        foreach (string folder in folders)
        {
            int commandLength = FolderCommand(root, folder).Length + 2;
            if (call.Count > 0 && (call.Count == perCall || length + commandLength > MaxTreeCommandCharacters))
            {
                yield return call;
                call = [];
                length = 0;
            }
            call.Add(folder);
            length += commandLength;
        }
        if (call.Count > 0)
            yield return call;
    }

    private static string TreeCommand(string root, IReadOnlyList<string> folders)
        => "recurse ON; " + string.Join("; ", folders.Select(folder => FolderCommand(root, folder)));

    // An absolute mask instead of cd: a failed cd would list the previous directory instead.
    private static string FolderCommand(string root, string folder)
    {
        string path = JoinPath(root, folder);
        return $"ls {QuoteRemote(path.Length == 0 ? "/*" : $"/{path}/*")}; pwd";
    }

    /// <summary>
    /// Splits a tree call's output into folders. Each requested folder's output starts with its own
    /// entries; smbclient walks breadth first and introduces every further folder with its share
    /// path ("\Folder\sub"), so a block is complete once the next header follows. A folder it could
    /// not list (fully) is followed by an error naming it ("NT_STATUS_… listing \Folder\sub\*") and
    /// is left out. Each requested folder ends with the disk summary (only after a fully successful
    /// ls) and the pwd line. Output cut at the limit keeps the completed blocks; the folder being
    /// listed when it was cut is found again as a subfolder without listing, or retried when it was
    /// a requested folder. Any unknown line, or an error for the tree root itself, rejects the call.
    /// </summary>
    private static TreeBatch? TryParseBatch(TreeListing listing, IReadOnlyList<string> folders, ProtocolCommandResult result)
    {
        var batch = new TreeBatch(result.OutputTruncated);
        if (!batch.Truncated && result.ExitCode != 0)
            return null; // pwd runs last: a helper that ran everything exits with 0.
        string output = result.StandardOutput;
        if (batch.Truncated)
            output = output[..(output.LastIndexOf('\n') + 1)]; // The last line may be cut.

        int current = 0;
        var folder = new FolderOutput(listing.Root, folders[0]);
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Trim().Length == 0)
                continue;
            if (current == folders.Count)
                return null; // Nothing may follow the last pwd.
            if (line.StartsWith(FolderEnd, StringComparison.Ordinal))
            {
                if (!folder.SummarySeen && !folder.Blocks.Any(block => block.Unreadable))
                    return null; // ls failed without naming a folder it could not list.
                folder.CommitTo(batch, listing, includeLast: true);
                if (++current < folders.Count)
                    folder = new FolderOutput(listing.Root, folders[current]);
                continue;
            }
            if (!folder.TryAdd(line))
                return null;
        }

        if (current < folders.Count)
        {
            if (!batch.Truncated)
                return null; // Ended before its last pwd: killed or crashed mid-folder.
            // The last block may be cut, unless an error or the summary closed it.
            var last = folder.Blocks[^1];
            bool lastComplete = folder.SummarySeen || last.Unreadable;
            folder.CommitTo(batch, listing, includeLast: lastComplete);
            if (!lastComplete && folder.Blocks.Count == 1)
                batch.Retry.Add(folders[current]);
            batch.Retry.AddRange(folders.Skip(current + 1));
        }
        return batch;
    }

    private static bool IsDiskSummary(string line)
        => line.Contains("blocks of size", StringComparison.Ordinal)
           && line.Contains("blocks available", StringComparison.Ordinal);

    /// <summary>
    /// smbclient's report of a folder it could not list during <c>recurse</c>: a failed directory
    /// read ("NT_STATUS_… listing \a\b\*") or a failed path resolution ("do_list: [\a\b\*] NT_STATUS_…").
    /// </summary>
    [GeneratedRegex(@"^(?:NT_STATUS_\w+ listing (?<path>\\.*?)|do_list: \[(?<path>\\[^\]]*)\] NT_STATUS_\w+)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ListingError();

    private static string JoinPath(string left, string right)
        => left.Length == 0 ? right : right.Length == 0 ? left : $"{left.TrimEnd('/')}/{right}";

    private sealed record TreeListing(
        string Share,
        string Root,
        ConcurrentDictionary<string, IReadOnlyList<RemoteStorageItem>> Tree,
        CancellationTokenSource Discard);

    /// <summary>What one tree call yielded: complete folders, unreadable ones, and folders to list again.</summary>
    private sealed class TreeBatch(bool truncated)
    {
        public bool Truncated { get; } = truncated;
        public List<(string Key, IReadOnlyList<RemoteStorageItem> Items)> Listed { get; } = [];
        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Retry { get; } = [];
    }

    /// <summary>The output of one requested folder's <c>ls</c>: its own block, then one per subfolder.</summary>
    private sealed class FolderOutput
    {
        private readonly string _prefix;
        private readonly string _key;

        public FolderOutput(string root, string key)
        {
            _key = key;
            // Taken verbatim: on Samba (Linux file systems) "x " and "x" are different folders.
            _prefix = "\\" + JoinPath(root, key).Replace('/', '\\');
            Blocks.Add(new Block(key, _prefix));
        }

        public List<Block> Blocks { get; } = [];
        public bool SummarySeen { get; private set; }

        public bool TryAdd(string line)
        {
            // smbclient ends a successful ls with the disk summary; only pwd may follow it.
            if (SummarySeen)
                return false;
            if (IsDiskSummary(line))
                return SummarySeen = true;
            if (line.StartsWith('\\'))
            {
                if (!line.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
                    || (line.Length > _prefix.Length && _prefix.Length > 1 && line[_prefix.Length] != '\\'))
                    return false;
                string nested = line[_prefix.Length..].Trim('\\').Replace('\\', '/');
                Blocks.Add(new Block(JoinPath(_key, nested), line));
                return true;
            }
            if (TryParseDirectoryLine(line, out _, out _, out _, out _))
            {
                Blocks[^1].Lines.AppendLine(line);
                return true;
            }
            Match error = ListingError().Match(line);
            if (!error.Success
                || !string.Equals(
                    error.Groups["path"].Value.TrimEnd('*').TrimEnd('\\'),
                    Blocks[^1].Path.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            // Nothing is known about a tree whose root cannot be listed.
            if (Blocks[^1].Key.Length == 0)
                return false;
            Blocks[^1].Unreadable = true;
            return true;
        }

        public void CommitTo(TreeBatch batch, TreeListing listing, bool includeLast)
        {
            foreach (Block block in includeLast ? Blocks : Blocks.SkipLast(1))
            {
                if (block.Unreadable)
                    batch.Unreadable.Add(block.Key);
                else
                    batch.Listed.Add((block.Key, ParseDirectory(
                        listing.Share, JoinPath(listing.Root, block.Key), block.Lines.ToString())));
            }
        }
    }

    private sealed class Block(string key, string path)
    {
        public string Key { get; } = key;
        public string Path { get; } = path;
        public StringBuilder Lines { get; } = new();
        public bool Unreadable { get; set; }
    }
}
