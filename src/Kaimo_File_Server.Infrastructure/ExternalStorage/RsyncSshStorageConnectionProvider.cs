using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public interface IRsyncProcessRunner
{
    Task<bool> TestSshAsync(RsyncSshConnectionSettings settings, CancellationToken cancellationToken);
    Task RunRsyncAsync(
        RsyncSshConnectionSettings settings,
        OptimizedSyncRequest request,
        CancellationToken cancellationToken);
}

public sealed partial class RsyncSshStorageConnectionProvider(IRsyncProcessRunner processRunner)
    : IStorageConnectionProvider
{
    public string Id => "rsync-ssh";
    public string DisplayName => "rsync / SSH";
    // Native execution is deliberately not advertised as a generic Sync yet:
    // rsync bypasses the file-service ACL/versioning pipeline. A later isolated
    // helper may opt in after it can preserve those application guarantees.
    public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.OptimizedSync;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.SshKey };

    public Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = ParseAndValidate(connection);
        IStorageSession session = new RsyncSshStorageSession(
            connection.Id,
            new RsyncSshOptimizedSync(settings, processRunner));
        return Task.FromResult(session);
    }

    public async Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = ParseAndValidate(connection);
            bool connected = await processRunner.TestSshAsync(settings, cancellationToken);
            return connected
                ? SmbStorageConnectionProvider.Healthy()
                : SmbStorageConnectionProvider.Unavailable("ssh_connection_failed");
        }
        catch (ProtocolConfigurationException exception)
        {
            return SmbStorageConnectionProvider.Invalid(exception);
        }
        catch (IOException)
        {
            return SmbStorageConnectionProvider.Unavailable("ssh_secret_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return SmbStorageConnectionProvider.Unavailable("ssh_secret_access_denied");
        }
    }

    public Task RevokeAsync(StorageConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    internal static RsyncSshConnectionSettings ParseAndValidate(StorageConnection connection)
    {
        if (!string.Equals(connection.ProviderId, "rsync-ssh", StringComparison.OrdinalIgnoreCase)
            || connection.AuthorizationMode != StorageAuthorizationMode.SshKey)
            throw new ProtocolConfigurationException("connection_mode_invalid", "The connection mode is invalid for rsync over SSH.");
        var settings = ProtocolConnectionSettings.Parse<RsyncSshConnectionSettings>(
            connection.SettingsJson, "rsync over SSH");
        SmbStorageConnectionProvider.ValidateHost(settings.Host);
        if (settings.Port is < 1 or > 65535)
            throw new ProtocolConfigurationException("port_invalid", "The SSH port is invalid.");
        if (string.IsNullOrWhiteSpace(settings.Username) || !UsernamePattern().IsMatch(settings.Username))
            throw new ProtocolConfigurationException("username_invalid", "The SSH username is invalid.");
        ValidateRemotePath(settings.RemoteRoot);
        ValidatePinnedHostKey(settings);
        ValidateSecretFile(settings.PrivateKeySecretReference, privateKey: true);
        ValidateSecretFile(settings.KnownHostsSecretReference, privateKey: false);
        return settings;
    }

    private static void ValidatePinnedHostKey(RsyncSshConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ExpectedHostKeySha256)
            || !settings.ExpectedHostKeySha256.StartsWith("SHA256:", StringComparison.Ordinal)
            || settings.ExpectedHostKeySha256.Length is < 20 or > 100)
            throw new ProtocolConfigurationException("host_key_invalid", "A SHA-256 SSH host-key fingerprint is required.");
        ValidateSecretFile(settings.KnownHostsSecretReference, privateKey: false);
        string expectedHost = settings.Port == 22 ? settings.Host : $"[{settings.Host}]:{settings.Port}";
        bool matchedHost = false;
        foreach (string line in File.ReadLines(settings.KnownHostsSecretReference))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            string[] fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || fields[0].StartsWith('|'))
                continue;
            if (!fields[0].Split(',').Contains(expectedHost, StringComparer.OrdinalIgnoreCase))
                continue;
            matchedHost = true;
            try
            {
                byte[] key = Convert.FromBase64String(fields[2]);
                string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
                if (CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(fingerprint),
                        Encoding.ASCII.GetBytes(settings.ExpectedHostKeySha256)))
                    return;
            }
            catch (FormatException)
            {
                throw new ProtocolConfigurationException("known_hosts_invalid", "The SSH known-hosts file is invalid.");
            }
        }
        throw new ProtocolConfigurationException(
            matchedHost ? "host_key_mismatch" : "host_key_missing",
            "The pinned SSH host identity does not match the known-hosts secret.");
    }

    private static void ValidateSecretFile(string path, bool privateKey)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || path.IndexOfAny(['\r', '\n']) >= 0
            || !File.Exists(path))
            throw new ProtocolConfigurationException("secret_reference_invalid", "The SSH secret reference is invalid.");
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode forbidden = privateKey
                ? UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                  | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute
                : UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            if ((mode & forbidden) != 0)
                throw new ProtocolConfigurationException("secret_permissions_unsafe", "The SSH secret file permissions are unsafe.");
        }
        if (new FileInfo(path).Length is <= 0 or > 1024 * 1024)
            throw new ProtocolConfigurationException("secret_reference_invalid", "The SSH secret file is empty or too large.");
    }

    internal static void ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith('/')
            || path.Length > 4096
            || !RemotePathPattern().IsMatch(path)
            || path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw new ProtocolConfigurationException("remote_path_invalid", "The rsync remote path is invalid.");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex("^/[A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RemotePathPattern();
}

internal sealed class RsyncSshStorageSession(Guid connectionId, IOptimizedStorageSync optimizedSync)
    : IStorageSession
{
    public Guid ConnectionId { get; } = connectionId;
    public StorageProviderCapabilities Capabilities =>
        StorageProviderCapabilities.OptimizedSync;
    public IRemoteFileStore? RemoteFiles => null;
    public IOptimizedStorageSync OptimizedSync { get; } = optimizedSync;
    IOptimizedStorageSync? IStorageSession.OptimizedSync => OptimizedSync;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RsyncSshOptimizedSync(
    RsyncSshConnectionSettings settings,
    IRsyncProcessRunner processRunner) : IOptimizedStorageSync
{
    public Task SynchronizeAsync(
        OptimizedSyncRequest request,
        CancellationToken cancellationToken = default)
        => processRunner.RunRsyncAsync(settings, request, cancellationToken);
}

public sealed class RsyncProcessRunner : IRsyncProcessRunner
{
    private const int MaximumDiagnosticCharacters = 4096;

    public async Task<bool> TestSshAsync(
        RsyncSshConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        var arguments = BuildSshArguments(settings);
        arguments.Add($"{settings.Username}@{settings.Host}");
        arguments.Add("true");
        return (await RunAsync("ssh", arguments, cancellationToken, throwOnFailure: false)) == 0;
    }

    public async Task RunRsyncAsync(
        RsyncSshConnectionSettings settings,
        OptimizedSyncRequest request,
        CancellationToken cancellationToken)
    {
        string localPath = ResolveLocalPath(request.LocalRootPath, request.LocalRelativePath);
        RsyncSshStorageConnectionProvider.ValidateRemotePath(request.RemotePath);
        string remotePath = CombineRemote(settings.RemoteRoot, request.RemotePath);
        string remote = $"{settings.Username}@{settings.Host}:{remotePath.TrimEnd('/')}/";
        string local = Path.TrimEndingDirectorySeparator(localPath) + Path.DirectorySeparatorChar;
        string remoteShell = BuildRemoteShell(settings);
        var arguments = new List<string> { "--archive", "--protect-args", "-e", remoteShell };
        if (request.DeleteExtraneousFiles)
            arguments.Insert(2, "--delete");
        arguments.Add("--");
        if (request.Direction == OptimizedSyncDirection.Pull)
        {
            arguments.Add(remote);
            arguments.Add(local);
        }
        else
        {
            arguments.Add(local);
            arguments.Add(remote);
        }
        await RunAsync("rsync", arguments, cancellationToken, throwOnFailure: true);
    }

    private static List<string> BuildSshArguments(RsyncSshConnectionSettings settings) =>
    [
        "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=yes",
        "-o", $"UserKnownHostsFile={settings.KnownHostsSecretReference}",
        "-o", "PasswordAuthentication=no",
        "-o", "KbdInteractiveAuthentication=no",
        "-i", settings.PrivateKeySecretReference,
        "-p", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
    ];

    private static string BuildRemoteShell(RsyncSshConnectionSettings settings)
    {
        // rsync parses -e as one command string. Secret paths are therefore
        // deliberately restricted to shell-neutral absolute paths.
        if (!ShellNeutralPath(settings.PrivateKeySecretReference)
            || !ShellNeutralPath(settings.KnownHostsSecretReference))
            throw new ProtocolConfigurationException(
                "secret_path_unsafe",
                "SSH secret references used by rsync must contain only shell-neutral path characters.");
        return $"ssh -o BatchMode=yes -o StrictHostKeyChecking=yes "
               + $"-o UserKnownHostsFile={settings.KnownHostsSecretReference} "
               + "-o PasswordAuthentication=no -o KbdInteractiveAuthentication=no "
               + $"-i {settings.PrivateKeySecretReference} -p {settings.Port}";
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
        if (!Directory.Exists(candidate)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            throw new ProtocolConfigurationException("local_path_invalid", "The local sync path is unavailable or symbolic.");
        return candidate;
    }

    private static string CombineRemote(string root, string child)
    {
        string combined = root.TrimEnd('/') + "/" + child.Trim('/');
        RsyncSshStorageConnectionProvider.ValidateRemotePath(combined);
        return combined;
    }

    private static async Task<int> RunAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
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

        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
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
        if (throwOnFailure && process.ExitCode != 0)
        {
            // Do not return helper output because it can contain remote paths or
            // server-controlled text. The bounded text is retained only to make
            // debugger inspection safe and is not attached to the exception.
            _ = stderr.Result[..Math.Min(stderr.Result.Length, MaximumDiagnosticCharacters)];
            throw new IOException($"The rsync helper failed with exit code {process.ExitCode}.");
        }
        return process.ExitCode;
    }
}
