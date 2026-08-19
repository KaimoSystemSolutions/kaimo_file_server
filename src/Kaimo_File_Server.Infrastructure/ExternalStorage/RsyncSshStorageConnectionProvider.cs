using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services;
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

public sealed class RsyncSshStorageConnectionProvider(
    IRsyncProcessRunner processRunner,
    ICredentialVault? credentialVault = null,
    IRsyncSshSetupService? setupService = null)
    : IStorageConnectionProvider
{
    public string Id => "rsync-ssh";
    public string DisplayName => "rsync / SSH";
    // Native rsync is intentionally sync-only: it does not provide the
    // item-level file contract required by browsing and virtual shares.
    public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.Sync
        | StorageProviderCapabilities.OptimizedSync
        | StorageProviderCapabilities.Read
        | StorageProviderCapabilities.Write;
    public IReadOnlySet<StorageAuthorizationMode> AuthorizationModes { get; }
        = new HashSet<StorageAuthorizationMode> { StorageAuthorizationMode.SshKey };

    public async Task<IStorageSession> OpenSessionAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = ParseAndValidate(connection);
        (settings, string? temporaryKey) = await ResolvePrivateKeyAsync(
            connection, settings, cancellationToken);
        IStorageSession session = new RsyncOptimizedStorageSession(
            connection.Id,
            Capabilities,
            new RsyncSshOptimizedSync(settings, processRunner),
            temporaryKey);
        return session;
    }

    public async Task<StorageConnectionHealthResult> TestAsync(
        StorageConnection connection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = ParseAndValidate(connection);
            (settings, string? temporaryKey) = await ResolvePrivateKeyAsync(
                connection, settings, cancellationToken);
            try
            {
                bool connected = await processRunner.TestSshAsync(settings, cancellationToken);
                return connected
                    ? SmbStorageConnectionProvider.Healthy()
                    : SmbStorageConnectionProvider.Unavailable("ssh_connection_failed");
            }
            finally
            {
                if (temporaryKey is not null)
                    File.Delete(temporaryKey);
            }
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

    public async Task RevokeAsync(StorageConnection connection, CancellationToken cancellationToken = default)
    {
        if (setupService is null)
            return;
        try
        {
            var settings = ProtocolConnectionSettings.Parse<RsyncSshConnectionSettings>(
                connection.SettingsJson, "rsync over SSH");
            await setupService.DeleteManagedKnownHostAsync(settings.KnownHostsSecretReference);
        }
        catch (ProtocolConfigurationException)
        {
            // Revocation is best-effort for an already-invalid connection.
        }
        catch (IOException)
        {
            // Revocation is best-effort after the connection record was deleted.
        }
        catch (UnauthorizedAccessException)
        {
            // Revocation is best-effort after the connection record was deleted.
        }
        catch (ArgumentException)
        {
            // Revocation is best-effort for malformed legacy paths.
        }
    }

    internal static RsyncSshConnectionSettings ParseAndValidate(StorageConnection connection)
        => PinnedSshSettings.ParseAndValidate(connection, "rsync-ssh", requireKnownHosts: true);

    private async Task<(RsyncSshConnectionSettings Settings, string? TemporaryKey)> ResolvePrivateKeyAsync(
        StorageConnection connection,
        RsyncSshConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.PrivateKeySecretReference))
            return (settings, null);
        if (!PinnedSshSettings.TryReadVaultPrivateKey(connection, credentialVault, out byte[] privateKey))
            throw new ProtocolConfigurationException(
                "private_key_missing", "The SSH private key is missing.");
        string temporary = ProtocolTemporaryFile.CreatePath();
        try
        {
            await ProtocolTemporaryFile.WriteRestrictedBytesAsync(
                temporary, privateKey, cancellationToken);
            return (settings with { PrivateKeySecretReference = temporary }, temporary);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}

internal sealed class RsyncOptimizedStorageSession(
    Guid connectionId,
    StorageProviderCapabilities capabilities,
    IOptimizedStorageSync optimizedSync,
    string? temporaryPrivateKey = null)
    : IStorageSession
{
    public Guid ConnectionId { get; } = connectionId;
    public StorageProviderCapabilities Capabilities { get; } = capabilities;
    public IRemoteFileStore? RemoteFiles => null;
    public IOptimizedStorageSync OptimizedSync { get; } = optimizedSync;
    IOptimizedStorageSync? IStorageSession.OptimizedSync => OptimizedSync;
    public ValueTask DisposeAsync()
    {
        if (temporaryPrivateKey is not null)
            File.Delete(temporaryPrivateKey);
        return ValueTask.CompletedTask;
    }
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
        PinnedSshSettings.ValidateRemotePath(request.RemotePath);
        string remotePath = CombineRemote(settings.RemoteRoot, request.RemotePath);
        string remote = $"{settings.Username}@{settings.Host}:{remotePath.TrimEnd('/')}/";
        string local = Path.TrimEndingDirectorySeparator(localPath) + Path.DirectorySeparatorChar;
        string remoteShell = BuildRemoteShell(settings);
        var arguments = BuildTransferArguments(request);
        // Abort a transfer that stalls with no I/O so a hung remote cannot pin
        // the operation open indefinitely; ssh ConnectTimeout only guards setup.
        arguments.Add("--timeout=300");
        arguments.AddRange(["-e", remoteShell]);
        arguments.Add("--");
        AddEndpoints(arguments, request.Direction, local, remote);
        await RunAsync("rsync", arguments, cancellationToken, throwOnFailure: true);
    }

    private static List<string> BuildTransferArguments(OptimizedSyncRequest request)
    {
        var arguments = new List<string> { "--archive", "--protect-args" };
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

    private static List<string> BuildSshArguments(RsyncSshConnectionSettings settings)
    {
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

    private static string BuildRemoteShell(RsyncSshConnectionSettings settings)
    {
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
        bool throwOnFailure)
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
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
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
