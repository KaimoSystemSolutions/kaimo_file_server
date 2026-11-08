using System.Diagnostics;
using System.Text;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.Storage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public interface IRsyncProcessRunner
{
    Task<bool> TestSshAsync(RsyncSshConnectionSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Runs rsync and returns the absolute filesystem paths of the regular files
    /// received into the local tree (pull only), parsed from
    /// <c>--itemize-changes</c> output. Push returns an empty list.
    /// </summary>
    Task<IReadOnlyList<string>> RunRsyncAsync(
        RsyncSshConnectionSettings settings,
        OptimizedSyncRequest request,
        CancellationToken cancellationToken);

    /// <summary>Lists the child folders of a connection-root-relative path with <c>rsync --list-only</c>.</summary>
    Task<IReadOnlyList<RemoteStorageItem>> ListDirectoriesAsync(
        RsyncSshConnectionSettings settings,
        string path,
        CancellationToken cancellationToken);
}
public sealed partial class RsyncProcessRunner : IRsyncProcessRunner
{
    private const int MaximumDiagnosticCharacters = 4096;

    // Longer than ConnectTimeout plus a slow sign-in; only a stalled peer reaches it.
    private static readonly TimeSpan SshTestTimeout = TimeSpan.FromSeconds(60);

    public Task<bool> TestSshAsync(
        RsyncSshConnectionSettings settings,
        CancellationToken cancellationToken)
        => WithTimeoutAsync(async token =>
        {
            var arguments = BuildSshArguments(settings);
            arguments.Add($"{settings.Username}@{settings.Host}");
            arguments.Add("true");
            string stderr = string.Empty;
            int exitCode = await RunAsync(
                "ssh", arguments, token, throwOnFailure: false, askpassPath: settings.AskpassPath, onStderr: text => stderr = text);
            if (exitCode != 0 && SshFailureCode(stderr) is { } code)
                throw new ProtocolConfigurationException(code, "The SSH client reported a sign-in problem.");
            return exitCode == 0;
        }, SshTestTimeout, cancellationToken);

    /// <summary>Runs a probe with an upper time limit; exceeding it counts as a failed probe.</summary>
    internal static async Task<bool> WithTimeoutAsync(
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            return await probe(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Maps the OpenSSH client's stderr to a localizable error code, or null for other failures.
    /// The helper check comes first: a helper that cannot start also ends in a rejected sign-in.
    /// </summary>
    internal static string? SshFailureCode(string stderr)
    {
        if (stderr.Contains("ssh_askpass:", StringComparison.Ordinal))
            return "ssh_askpass_unavailable";
        return stderr.Contains("Permission denied (", StringComparison.Ordinal)
            ? "ssh_authentication_failed"
            : null;
    }

    // ponytail: 100k received paths held in memory per run; a massive first-seed
    // pull beyond this indexes the first 100k and leaves the rest to a manual
    // reindex. Raise or stream to the index if that ever bites.
    private const int MaximumItemizedPaths = 100_000;

    public async Task<IReadOnlyList<string>> RunRsyncAsync(
        RsyncSshConnectionSettings settings,
        OptimizedSyncRequest request,
        CancellationToken cancellationToken)
    {
        string localPath = ResolveLocalPath(request.LocalRootPath, request.LocalRelativePath);
        PinnedSshSettings.ValidateRemotePath(request.RemotePath);
        string remotePath = CombineRemote(settings.RemoteRoot, request.RemotePath);
        string remote = $"{settings.Username}@{settings.Host}:{remotePath.TrimEnd('/')}/";
        string local = Path.TrimEndingDirectorySeparator(localPath) + Path.DirectorySeparatorChar;
        string remoteShell = BuildRemoteShell(settings);
        var arguments = BuildTransferArguments(request);
        // Abort a transfer that stalls with no I/O so a hung remote cannot pin
        // the operation open indefinitely; ssh ConnectTimeout only guards setup.
        arguments.Add("--timeout=300");
        // Emit one itemized line per changed entry so received files can be fed to
        // the search index without a full-share rescan (see ReceivedFileAbsolutePath).
        arguments.Add("--itemize-changes");
        arguments.AddRange(["-e", remoteShell]);
        arguments.Add("--");
        AddEndpoints(arguments, request.Direction, local, remote);

        // Only a pull writes into the local tree; a push changes nothing here.
        var received = request.Direction == OptimizedSyncDirection.Pull ? new List<string>() : null;
        var created = request.Direction == OptimizedSyncDirection.Pull ? new List<string>() : null;
        void CollectReceived(string line)
        {
            if (received is null || created is null)
                return;
            if (received.Count < MaximumItemizedPaths && ReceivedFileAbsolutePath(localPath, line) is { } absolute)
                received.Add(absolute);
            if (created.Count < MaximumItemizedPaths && CreatedEntryAbsolutePath(localPath, line) is { } entry)
                created.Add(entry);
        }

        await RunThenStampCreatedAsync(
            () => RunAsync("rsync", arguments, cancellationToken, throwOnFailure: true, CollectReceived, settings.AskpassPath),
            created);
        return received ?? (IReadOnlyList<string>)[];
    }

    public async Task<IReadOnlyList<RemoteStorageItem>> ListDirectoriesAsync(
        RsyncSshConnectionSettings settings,
        string path,
        CancellationToken cancellationToken)
    {
        var folders = new List<RemoteStorageItem>();
        string stderr = string.Empty;
        int exitCode = await RunAsync("rsync", BuildListArguments(settings, path), cancellationToken, throwOnFailure: false, line =>
        {
            if (ListedDirectoryName(line) is { } name)
                folders.Add(new RemoteStorageItem(name, SftpPaths.CombineChild(path, name), true, null, null));
        }, settings.AskpassPath, text => stderr = text);
        if (exitCode == 0)
            return folders;
        Exception failure = RsyncFailure(exitCode, stderr);
        if (failure is ProtocolConfigurationException { Code: "rsync_read_denied" } refused)
            throw new RemoteFolderNotSyncableException(
                await ListOverSshAsync(settings, path, cancellationToken), refused.Code, refused);
        throw failure;
    }

    // rsync refuses the sender role, but the account may still see folders over SSH, so an
    // administrator can navigate down to a folder rsync may read. Accounts without a shell
    // simply get an empty list: the folder stays unselectable either way.
    private static async Task<IReadOnlyList<RemoteStorageItem>> ListOverSshAsync(
        RsyncSshConnectionSettings settings,
        string path,
        CancellationToken cancellationToken)
    {
        var folders = new List<RemoteStorageItem>();
        await WithTimeoutAsync(async token =>
        {
            await RunAsync("ssh", BuildShellListArguments(settings, path), token, throwOnFailure: false, line =>
            {
                if (ShellListedDirectoryName(line) is { } name)
                    folders.Add(new RemoteStorageItem(name, SftpPaths.CombineChild(path, name), true, null, null));
            }, settings.AskpassPath);
            return true;
        }, SshTestTimeout, cancellationToken);
        return folders.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    // The remote shell parses this command, so the validated path is single-quoted.
    internal static List<string> BuildShellListArguments(RsyncSshConnectionSettings settings, string path)
    {
        string remotePath = CombineRemote(settings.RemoteRoot, path).TrimEnd('/') + "/";
        var arguments = BuildSshArguments(settings);
        arguments.Add($"{settings.Username}@{settings.Host}");
        arguments.Add($"ls -1Ap -- '{remotePath.Replace("'", @"'\''")}'");
        return arguments;
    }

    /// <summary>Maps one <c>ls -p</c> line to a child folder name; only folders end in a slash.</summary>
    internal static string? ShellListedDirectoryName(string line)
        => line.EndsWith('/') && IsPickableFolderName(line[..^1]) ? line[..^1] : null;

    /// <summary>
    /// Builds the failure of an rsync run. The message may hold server-controlled text and
    /// reaches only the log; the UI and notifications show the classified error code.
    /// </summary>
    internal static Exception RsyncFailure(int exitCode, string stderr)
    {
        string message = $"rsync failed with exit code {exitCode}: {stderr.Trim()}";
        // The remote rsync refuses the sender role, which listing and pulling both need.
        return stderr.Contains("module is write only", StringComparison.Ordinal)
            ? new ProtocolConfigurationException("rsync_read_denied", message)
            : new IOException(message);
    }

    // Without --recursive and with a trailing slash rsync lists exactly one level.
    internal static List<string> BuildListArguments(RsyncSshConnectionSettings settings, string path)
    {
        string remotePath = CombineRemote(settings.RemoteRoot, path);
        return
        [
            "--list-only", "--protect-args", "--8-bit-output", "--timeout=60",
            "-e", BuildRemoteShell(settings),
            "--", $"{settings.Username}@{settings.Host}:{remotePath.TrimEnd('/')}/"
        ];
    }

    /// <summary>
    /// Maps one <c>--list-only</c> line to a child folder name, or null for anything else.
    /// Names a sync could not address (rsync-escaped control characters, globs) are not offered.
    /// </summary>
    internal static string? ListedDirectoryName(string line)
    {
        string name = ListedDirectoryPattern().Match(line).Groups[1].Value;
        return IsPickableFolderName(name) ? name : null;
    }

    private static bool IsPickableFolderName(string name)
        => name is not ("" or "." or "..") && !name.Contains('/') && PinnedSshSettings.IsValidRemotePath("/" + name);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^d\S{9,}\s+\S+\s+\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2} (.+)\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ListedDirectoryPattern();

    /// <summary>
    /// Runs rsync, then stamps the creation times of what it created (<paramref name="created"/>,
    /// <c>null</c> for a push). Stamping runs after the run because rsync stamps folder times only
    /// once their content is complete, and also after a failed or cancelled run: the next run no
    /// longer itemizes those entries as new, so this is the only chance to stamp them.
    /// </summary>
    internal static async Task RunThenStampCreatedAsync(Func<Task> run, IReadOnlyCollection<string>? created)
    {
        try
        {
            await run();
        }
        finally
        {
            if (created is not null)
                StampCreationFromModified(created);
        }
    }

    /// <summary>
    /// Maps one <c>--itemize-changes</c> line to the absolute path of a regular
    /// file received into the local tree, or null for anything else. rsync prints
    /// eleven flag characters, a space, then the entry path. A received regular
    /// file starts with <c>&gt;f</c>; directories, attribute-only touches,
    /// deletions (<c>*deleting</c>) and pushed items (<c>&lt;...</c>) are not local
    /// content additions and are skipped.
    /// </summary>
    internal static string? ReceivedFileAbsolutePath(string localRoot, string itemizeLine)
        => itemizeLine.StartsWith(">f", StringComparison.Ordinal)
            ? ItemizedAbsolutePath(localRoot, itemizeLine)
            : null;

    /// <summary>
    /// The files and folders rsync created (<c>&gt;f+++++++++</c>, <c>cd+++++++++</c>): their creation
    /// time is the time of the pull. An updated file (e.g. <c>&gt;f.st......</c>) is skipped: its new
    /// mtime is the latest edit, which must not become its creation time.
    /// </summary>
    // ponytail: rsync replaces an updated file via temp file + rename, so it still shows the pull time
    // as created; keeping the old value would need a dry-run pre-scan of the files about to change.
    internal static string? CreatedEntryAbsolutePath(string localRoot, string itemizeLine)
        => itemizeLine.StartsWith(">f+++++++++", StringComparison.Ordinal)
           || itemizeLine.StartsWith("cd+++++++++", StringComparison.Ordinal)
            ? ItemizedAbsolutePath(localRoot, itemizeLine)
            : null;

    private static string? ItemizedAbsolutePath(string localRoot, string itemizeLine)
    {
        if (itemizeLine.Length < 13)
            return null;
        string relative = itemizeLine[12..].Trim().TrimEnd('/');
        if (relative.Length == 0)
            return null;
        string absolute = Path.GetFullPath(
            Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        // Defense in depth: never surface a path that escaped the transfer root.
        return absolute == localRoot
               || absolute.StartsWith(localRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? absolute
            : null;
    }

    /// <summary>
    /// Linux rsync cannot transfer creation times (<c>--crtimes</c> is macOS-only), so what it created
    /// would show the time of the pull as created. The modification time rsync preserved
    /// (<c>--archive</c>) stands in for it, as for every other pull. Best effort per entry.
    /// </summary>
    // ponytail: capped like the index list (MaximumItemizedPaths); beyond it entries keep the pull time.
    internal static void StampCreationFromModified(IEnumerable<string> absolutePaths)
    {
        foreach (string path in absolutePaths)
        {
            try
            {
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                if (info.Exists)
                    FileBirthTime.TrySetUtc(info, info.LastWriteTimeUtc);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Vanished or unreadable since rsync created it; it keeps the pull time.
            }
        }
    }

    private static List<string> BuildTransferArguments(OptimizedSyncRequest request)
    {
        // --archive implies -l/-D: a remote symlink (e.g. "x -> /data/kaimo-system") or device
        // node would be recreated inside the local share, and the path-containment checks only
        // look at the textual path. Skip symlinks and special files in both directions.
        var arguments = new List<string>
        {
            "--archive", "--protect-args", "--no-links", "--no-devices", "--no-specials"
        };
        if (request.DeleteExtraneousFiles)
            arguments.Add("--delete");
        if (request.MaximumFileSizeBytes is > 0)
            arguments.Add($"--max-size={request.MaximumFileSizeBytes.Value}");
        if (request.MaximumTransferBytesPerSecond is > 0)
            arguments.Add($"--bwlimit={Math.Max(1, request.MaximumTransferBytesPerSecond.Value / 1024)}");
        foreach (string extension in request.ExcludedExtensions ?? [])
        {
            string normalized = extension.Trim().TrimStart('.');
            if (normalized.Length == 0
                || normalized.Length > 32
                || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)
                    && character is not '_' and not '-'))
                throw new ProtocolConfigurationException(
                    "exclude_invalid", "An excluded file extension is invalid.");
            arguments.Add($"--exclude=*.{normalized}");
        }
        return arguments;
    }

    private static void AddEndpoints(
        ICollection<string> arguments,
        OptimizedSyncDirection direction,
        string local,
        string remote)
    {
        if (direction == OptimizedSyncDirection.Pull)
        {
            arguments.Add(remote);
            arguments.Add(local);
        }
        else
        {
            arguments.Add(local);
            arguments.Add(remote);
        }
    }

    /// <summary>Makes ssh read the password from the helper; requires OpenSSH 8.4 or later.</summary>
    internal static void ApplyAskpass(ProcessStartInfo startInfo, string? askpassPath)
    {
        if (askpassPath is null)
            return;
        startInfo.Environment["SSH_ASKPASS"] = askpassPath;
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
    }

    internal static List<string> BuildSshArguments(RsyncSshConnectionSettings settings)
    {
        if (settings.AskpassPath is not null)
            return PasswordSshArguments(settings);
        string privateKey = settings.PrivateKeySecretReference
            ?? throw new ProtocolConfigurationException("private_key_missing", "The SSH private key is missing.");
        return
        [
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=15",
            "-o", "IdentitiesOnly=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", $"UserKnownHostsFile={settings.KnownHostsSecretReference}",
            "-o", "PasswordAuthentication=no",
            "-o", "KbdInteractiveAuthentication=no",
            "-i", privateKey,
            "-p", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ];
    }

    internal static string BuildRemoteShell(RsyncSshConnectionSettings settings)
    {
        if (settings.AskpassPath is not null)
            return BuildPasswordRemoteShell(settings);
        // rsync parses -e as one command string. Secret paths are therefore
        // deliberately restricted to shell-neutral absolute paths.
        if (string.IsNullOrWhiteSpace(settings.PrivateKeySecretReference)
            || !ShellNeutralPath(settings.PrivateKeySecretReference)
            || !ShellNeutralPath(settings.KnownHostsSecretReference))
            throw new ProtocolConfigurationException(
                "secret_path_unsafe",
                "SSH secret references used by rsync must contain only shell-neutral path characters.");
        return $"ssh -o BatchMode=yes -o ConnectTimeout=15 -o IdentitiesOnly=yes "
               + "-o StrictHostKeyChecking=yes "
               + $"-o UserKnownHostsFile={settings.KnownHostsSecretReference} "
               + "-o PasswordAuthentication=no -o KbdInteractiveAuthentication=no "
               + $"-i {settings.PrivateKeySecretReference} -p {settings.Port}";
    }

    /// <summary>
    /// The password-mode variant for rsync and SFTP backups: identical host pinning, but no key
    /// and no batch mode. The password reaches ssh only through an <c>SSH_ASKPASS</c>
    /// helper the caller provides; at most one attempt is made. With public keys off, the
    /// client's defaults leave keyboard-interactive and password. The command contains no
    /// comma: restic splits its <c>-o</c> values at commas.
    /// </summary>
    internal static string BuildPasswordRemoteShell(RsyncSshConnectionSettings settings)
        => "ssh " + string.Join(' ', PasswordSshArguments(settings));

    // Keepalives replace those batch mode implies on Debian and Ubuntu, so a dead peer ends the session.
    private static List<string> PasswordSshArguments(RsyncSshConnectionSettings settings)
    {
        if (!ShellNeutralPath(settings.KnownHostsSecretReference))
            throw new ProtocolConfigurationException(
                "secret_path_unsafe",
                "SSH secret references must contain only shell-neutral path characters.");
        return
        [
            "-o", "ConnectTimeout=15",
            "-o", "ServerAliveInterval=30",
            "-o", "ServerAliveCountMax=4",
            "-o", "StrictHostKeyChecking=yes",
            "-o", $"UserKnownHostsFile={settings.KnownHostsSecretReference}",
            "-o", "PubkeyAuthentication=no",
            "-o", "GSSAPIAuthentication=no",
            "-o", "HostbasedAuthentication=no",
            "-o", "NumberOfPasswordPrompts=1",
            "-p", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ];
    }

    private static bool ShellNeutralPath(string path)
        => path.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '.');

    private static string ResolveLocalPath(string rootPath, string relativePath)
    {
        if (!Path.IsPathFullyQualified(rootPath))
            throw new ProtocolConfigurationException("local_path_invalid", "The local sync root must be absolute.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string relative = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw new ProtocolConfigurationException("local_path_invalid", "The local sync path is invalid.");
        string candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(candidate, root, comparison)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new ProtocolConfigurationException("local_path_invalid", "The local sync path escapes its share root.");
        if (!Directory.Exists(candidate) || ContainsReparsePoint(root, candidate))
            throw new ProtocolConfigurationException("local_path_invalid", "The local sync path is unavailable or symbolic.");
        return candidate;
    }

    private static bool ContainsReparsePoint(string root, string candidate)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return true;
        string relative = Path.GetRelativePath(root, candidate);
        string current = root;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static string CombineRemote(string root, string child)
    {
        string combined = root.TrimEnd('/') + "/" + child.Trim('/');
        PinnedSshSettings.ValidateRemotePath(combined);
        return combined;
    }

    private static async Task<int> RunAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure,
        Action<string>? onStdoutLine = null,
        string? askpassPath = null,
        Action<string>? onStderr = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        ApplyAskpass(startInfo, askpassPath);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The protocol helper could not be started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ProtocolConfigurationException("helper_unavailable", "The required protocol helper is unavailable.", exception);
        }

        Task<string> stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        // When a caller wants the itemized output it must be streamed line by line
        // (a full sync emits far more than the bounded diagnostic buffer holds);
        // otherwise the stream is still drained to keep the child from blocking.
        Task stdout = onStdoutLine is null
            ? ReadBoundedAsync(process.StandardOutput, cancellationToken)
            : ReadLinesAsync(process.StandardOutput, onStdoutLine, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        await Task.WhenAll(stderr, stdout);
        onStderr?.Invoke(stderr.Result);
        if (throwOnFailure && process.ExitCode != 0)
            throw RsyncFailure(process.ExitCode, stderr.Result);
        return process.ExitCode;
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            onLine(line);
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[1024];
        var result = new StringBuilder();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return result.ToString();
            int remaining = MaximumDiagnosticCharacters - result.Length;
            if (remaining > 0)
                result.Append(buffer, 0, Math.Min(read, remaining));
        }
    }
}
