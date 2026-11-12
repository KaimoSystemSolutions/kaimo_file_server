using System.Diagnostics;
using System.Text;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage.Protocols;

public sealed class ProtocolCommandRunner : IProtocolCommandRunner
{
    /// <summary>Output kept per stream unless a call sets its own limit; the rest is dropped.</summary>
    public const int DefaultMaximumOutputCharacters = 4 * 1024 * 1024;

    /// <summary>Longest helper output line quoted in a failure message.</summary>
    private const int MaximumFailureDetailCharacters = 300;

    /// <summary>Streamed output held back until it is too long to be a helper's error message.</summary>
    private const int HeldOutputBytes = 4096;

    /// <summary>smbclient failures that retrying the same item cannot fix.</summary>
    private static readonly string[] DefinitiveStatuses =
    [
        "NT_STATUS_OBJECT_NAME_NOT_FOUND",
        "NT_STATUS_OBJECT_PATH_NOT_FOUND",
        "NT_STATUS_NO_SUCH_FILE",
        "NT_STATUS_FILE_IS_A_DIRECTORY",
        "NT_STATUS_NOT_A_DIRECTORY",
        "NT_STATUS_OBJECT_NAME_INVALID",
        "NT_STATUS_DISK_FULL",
        "NT_STATUS_QUOTA_EXCEEDED"
    ];

    public async Task StreamOutputAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Stream target,
        CancellationToken cancellationToken)
    {
        using Process process = Start(executable, arguments, new Dictionary<string, string>());
        Task<(string Text, bool Truncated)> stderr = ReadBoundedAsync(
            process.StandardError, DefaultMaximumOutputCharacters, stopAtLimit: false, cancellationToken);
        byte[] buffer = new byte[81920];
        using var held = new MemoryStream();
        bool streaming = false;
        try
        {
            Stream output = process.StandardOutput.BaseStream;
            int read;
            while ((read = await output.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (streaming)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    continue;
                }
                held.Write(buffer, 0, read);
                if (held.Length <= HeldOutputBytes)
                    continue;
                await target.WriteAsync(held.GetBuffer().AsMemory(0, (int)held.Length), cancellationToken);
                streaming = true;
            }
            await process.WaitForExitAsync(cancellationToken);
            await stderr;
        }
        catch
        {
            // Cancelled, or the target failed (e.g. a full pool): nobody reads the helper anymore.
            StopProcess(process);
            throw;
        }

        string message = streaming ? string.Empty : Encoding.UTF8.GetString(held.GetBuffer(), 0, (int)held.Length);
        ThrowIfFailed(new ProtocolCommandResult(process.ExitCode, message, stderr.Result.Text));
        if (!streaming)
            await target.WriteAsync(held.GetBuffer().AsMemory(0, (int)held.Length), cancellationToken);
    }

    public Task<ProtocolCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
        => RunAsync(executable, arguments, new Dictionary<string, string>(), cancellationToken, throwOnFailure);

    public Task<ProtocolCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
        => RunCoreAsync(executable, arguments, environment, cancellationToken, throwOnFailure,
            DefaultMaximumOutputCharacters, stopAtOutputLimit: false);

    public Task<ProtocolCommandResult> RunUntilOutputLimitAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        int? maximumOutputCharacters = null)
    {
        if (maximumOutputCharacters is { } limit)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(maximumOutputCharacters));
        return RunCoreAsync(executable, arguments, new Dictionary<string, string>(), cancellationToken,
            throwOnFailure: false, maximumOutputCharacters ?? DefaultMaximumOutputCharacters, stopAtOutputLimit: true);
    }

    private async Task<ProtocolCommandResult> RunCoreAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken,
        bool throwOnFailure,
        int outputLimit,
        bool stopAtOutputLimit)
    {
        using Process process = Start(executable, arguments, environment);

        Task<(string Text, bool Truncated)> stdout =
            ReadBoundedAsync(process.StandardOutput, outputLimit, stopAtOutputLimit, cancellationToken);
        Task<(string Text, bool Truncated)> stderr = ReadBoundedAsync(
            process.StandardError, DefaultMaximumOutputCharacters, stopAtLimit: false, cancellationToken);
        try
        {
            // stdout returns early once past the limit: end the helper instead of waiting for it.
            if (stopAtOutputLimit && (await stdout).Truncated)
                StopProcess(process);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        (string standardOutput, bool outputTruncated) = stdout.Result;
        var result = new ProtocolCommandResult(
            process.ExitCode, standardOutput, stderr.Result.Text, outputTruncated);
        if (throwOnFailure)
            ThrowIfFailed(result);
        return result;
    }

    private static Process Start(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(executable)
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
        foreach ((string name, string value) in environment)
            startInfo.Environment[name] = value;
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new ProtocolConfigurationException("helper_unavailable", "The protocol helper could not be started.");
            return process;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            process.Dispose();
            throw new ProtocolConfigurationException(
                "helper_unavailable", "The required protocol helper is unavailable.", exception);
        }
    }

    /// <summary>
    /// Throws for a helper that exited with a failure code: <see cref="RemoteStorageAccessDeniedException"/>
    /// when its output reports a denied operation, otherwise an <see cref="IOException"/> quoting
    /// the helper's last output line, which usually names the cause (smbclient's NT_STATUS).
    /// </summary>
    internal static void ThrowIfFailed(ProtocolCommandResult result)
    {
        if (result.ExitCode == 0)
            return;
        if (IsAccessDeniedOutput(result.StandardOutput) || IsAccessDeniedOutput(result.StandardError))
            throw new RemoteStorageAccessDeniedException(
                "The remote storage system denied the requested operation.");
        string? detail = LastLine(result.StandardError) ?? LastLine(result.StandardOutput);
        string message = detail is null
            ? $"The protocol helper failed with exit code {result.ExitCode}."
            : $"The protocol helper failed with exit code {result.ExitCode}: {detail}";
        if (detail is not null && DefinitiveStatuses.Any(status => detail.Contains(status, StringComparison.Ordinal)))
            throw new RemoteItemRejectedException(message);
        throw new IOException(message);
    }

    private static string? LastLine(string output)
    {
        string trimmed = output.TrimEnd();
        if (trimmed.Length == 0)
            return null;
        string line = trimmed[(trimmed.LastIndexOf('\n') + 1)..].Trim();
        return line.Length <= MaximumFailureDetailCharacters ? line : line[..MaximumFailureDetailCharacters];
    }

    private static void StopProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited on its own in the meantime.
        }
    }

    /// <summary>
    /// Reads a helper stream up to <paramref name="limit"/> characters and flags anything beyond it:
    /// the rest is drained (so the helper can finish) or, with <paramref name="stopAtLimit"/>, left unread.
    /// </summary>
    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader, int limit, bool stopAtLimit, CancellationToken cancellationToken)
    {
        char[] buffer = new char[8192];
        var result = new StringBuilder();
        bool truncated = false;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return (result.ToString(), truncated);
            int remaining = limit - result.Length;
            truncated |= read > remaining;
            if (remaining > 0)
                result.Append(buffer, 0, Math.Min(read, remaining));
            if (truncated && stopAtLimit)
                return (result.ToString(), true);
        }
    }

    internal static bool IsAccessDeniedOutput(string? output)
        => output?.Contains("NT_STATUS_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase) == true
           || output?.Contains("NT_STATUS_PRIVILEGE_NOT_HELD", StringComparison.OrdinalIgnoreCase) == true
           || output?.Contains("permission denied", StringComparison.OrdinalIgnoreCase) == true
           || output?.Contains("access denied", StringComparison.OrdinalIgnoreCase) == true;
}
