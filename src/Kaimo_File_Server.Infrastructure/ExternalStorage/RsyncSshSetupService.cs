using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public sealed record RsyncSshKeyMaterial(byte[] PrivateKey, string PublicKey);

public sealed record RsyncSshHostKeyCandidate(
    string Algorithm,
    string FingerprintSha256,
    string KnownHostsLine);

public interface IRsyncSshSetupService
{
    Task<RsyncSshKeyMaterial> ValidatePrivateKeyAsync(
        ReadOnlyMemory<byte> privateKey,
        CancellationToken cancellationToken = default);

    Task<RsyncSshKeyMaterial> GeneratePrivateKeyAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RsyncSshHostKeyCandidate>> DiscoverHostKeysAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default);

    Task<string> PersistKnownHostAsync(
        RsyncSshHostKeyCandidate candidate,
        CancellationToken cancellationToken = default);

    Task DeleteManagedKnownHostAsync(string? path);
}

/// <summary>
/// Supports the interactive SSH setup wizard. Private keys are validated in a
/// restricted temporary file and returned to the caller for immediate vault
/// protection; this service never retains plaintext private-key material.
/// Known-host entries contain only public data and are stored in a managed,
/// application-owned directory with restrictive permissions.
/// </summary>
public sealed class RsyncSshSetupService : IRsyncSshSetupService
{
    private const int MaximumKeyBytes = 1024 * 1024;
    private const int MaximumProcessOutputCharacters = 64 * 1024;
    private readonly string _managedRoot;

    public RsyncSshSetupService(string applicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataPath);
        _managedRoot = Path.GetFullPath(Path.Combine(
            applicationDataPath, ".external-storage", "rsync-ssh"));
        Directory.CreateDirectory(_managedRoot);
        if ((File.GetAttributes(_managedRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The managed SSH setup directory must not be symbolic.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_managedRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async Task<RsyncSshKeyMaterial> ValidatePrivateKeyAsync(
        ReadOnlyMemory<byte> privateKey,
        CancellationToken cancellationToken = default)
    {
        if (privateKey.IsEmpty || privateKey.Length > MaximumKeyBytes
            || privateKey.Span.IndexOf((byte)0) >= 0)
            throw new ProtocolConfigurationException(
                "private_key_invalid", "The uploaded SSH private key is invalid or too large.");

        string temporary = ProtocolTemporaryFile.CreatePath();
        try
        {
            await ProtocolTemporaryFile.WriteRestrictedBytesAsync(
                temporary, privateKey, cancellationToken);
            ProcessResult result = await RunAsync(
                "ssh-keygen", ["-y", "-f", temporary], cancellationToken);
            string publicKey = result.StandardOutput.Trim();
            if (result.ExitCode != 0 || !IsPublicKey(publicKey))
                throw new ProtocolConfigurationException(
                    "private_key_invalid",
                    "The SSH private key is unsupported, invalid, or requires a passphrase.");
            return new RsyncSshKeyMaterial(privateKey.ToArray(), publicKey);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async Task<RsyncSshKeyMaterial> GeneratePrivateKeyAsync(
        CancellationToken cancellationToken = default)
    {
        string temporary = ProtocolTemporaryFile.CreatePath();
        string publicPath = temporary + ".pub";
        try
        {
            ProcessResult result = await RunAsync(
                "ssh-keygen",
                ["-q", "-t", "ed25519", "-N", string.Empty, "-C", "kaimo-rsync", "-f", temporary],
                cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(temporary))
                throw new ProtocolConfigurationException(
                    "key_generation_failed", "The SSH key could not be generated.");
            byte[] privateKey = await File.ReadAllBytesAsync(temporary, cancellationToken);
            try
            {
                return await ValidatePrivateKeyAsync(privateKey, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
        finally
        {
            File.Delete(temporary);
            File.Delete(publicPath);
        }
    }

    public async Task<IReadOnlyList<RsyncSshHostKeyCandidate>> DiscoverHostKeysAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        SmbStorageConnectionProvider.ValidateHost(host);
        SmbStorageConnectionProvider.ValidatePort(port);
        ProcessResult result = await RunAsync(
            "ssh-keyscan",
            ["-T", "5", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), host],
            cancellationToken);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new ProtocolConfigurationException(
                "host_key_discovery_failed", "No SSH host key could be retrieved from this endpoint.");

        string expectedHost = port == 22 ? host : $"[{host}]:{port}";
        var candidates = new List<RsyncSshHostKeyCandidate>();
        foreach (string rawLine in result.StandardOutput.Split(
                     ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith('#'))
                continue;
            string[] fields = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3
                || !string.Equals(fields[0], expectedHost, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                byte[] key = Convert.FromBase64String(fields[2]);
                string fingerprint = "SHA256:"
                    + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
                candidates.Add(new RsyncSshHostKeyCandidate(fields[1], fingerprint, rawLine));
            }
            catch (FormatException)
            {
                throw new ProtocolConfigurationException(
                    "host_key_discovery_failed", "The SSH endpoint returned an invalid host key.");
            }
        }
        return candidates
            .DistinctBy(candidate => candidate.FingerprintSha256, StringComparer.Ordinal)
            .OrderBy(candidate => AlgorithmPreference(candidate.Algorithm))
            .ToArray();
    }

    public async Task<string> PersistKnownHostAsync(
        RsyncSshHostKeyCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateCandidate(candidate);
        string path = Path.Combine(_managedRoot, $"known-hosts-{Guid.NewGuid():N}");
        await ProtocolTemporaryFile.WriteRestrictedTextAsync(
            path, candidate.KnownHostsLine + "\n", cancellationToken);
        return path;
    }

    public Task DeleteManagedKnownHostAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return Task.CompletedTask;
        string fullPath = Path.GetFullPath(path);
        if (string.Equals(Path.GetDirectoryName(fullPath), _managedRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && Path.GetFileName(fullPath).StartsWith("known-hosts-", StringComparison.Ordinal))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    private static void ValidateCandidate(RsyncSshHostKeyCandidate candidate)
    {
        string[] fields = candidate.KnownHostsLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[0].StartsWith('|')
            || !string.Equals(fields[1], candidate.Algorithm, StringComparison.Ordinal))
            throw new ProtocolConfigurationException("host_key_invalid", "The selected SSH host key is invalid.");
        byte[] key;
        try { key = Convert.FromBase64String(fields[2]); }
        catch (FormatException exception)
        {
            throw new ProtocolConfigurationException(
                "host_key_invalid", "The selected SSH host key is invalid.", exception);
        }
        string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(fingerprint),
                Encoding.ASCII.GetBytes(candidate.FingerprintSha256)))
            throw new ProtocolConfigurationException("host_key_invalid", "The selected SSH host key is invalid.");
    }

    private static bool IsPublicKey(string value)
    {
        string[] fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || !fields[0].StartsWith("ssh-", StringComparison.Ordinal))
            return false;
        try { return Convert.FromBase64String(fields[1]).Length > 0; }
        catch (FormatException) { return false; }
    }

    private static int AlgorithmPreference(string algorithm) => algorithm switch
    {
        "ssh-ed25519" => 0,
        "ecdsa-sha2-nistp256" => 1,
        "rsa-sha2-512" => 2,
        "rsa-sha2-256" => 3,
        "ssh-rsa" => 4,
        _ => 10
    };

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new ProtocolConfigurationException(
                    "helper_unavailable", "The required SSH setup helper is unavailable.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ProtocolConfigurationException(
                "helper_unavailable", "The required SSH setup helper is unavailable.", exception);
        }
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        Task<string> stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new ProtocolConfigurationException(
                "helper_timeout", "The SSH setup helper timed out.");
        }
        return new ProcessResult(process.ExitCode, stdout.Result);
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
            int remaining = MaximumProcessOutputCharacters - result.Length;
            if (remaining > 0)
                result.Append(buffer, 0, Math.Min(read, remaining));
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput);
}
