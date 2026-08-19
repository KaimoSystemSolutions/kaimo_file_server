using System.Diagnostics;
using System.Text;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public sealed class ProtocolCommandRunner : IProtocolCommandRunner
{
    private const int MaximumOutputCharacters = 4 * 1024 * 1024;

    public async Task<ProtocolCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
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
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new ProtocolConfigurationException("helper_unavailable", "The protocol helper could not be started.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ProtocolConfigurationException(
                "helper_unavailable", "The required protocol helper is unavailable.", exception);
        }

        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        Task<string> stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        string standardOutput = stdout.Result;
        string standardError = stderr.Result;
        if (throwOnFailure && process.ExitCode != 0)
        {
            if (IsAccessDeniedOutput(standardOutput) || IsAccessDeniedOutput(standardError))
                throw new RemoteStorageAccessDeniedException(
                    "The remote storage system denied the requested operation.");
            throw new IOException($"The protocol helper failed with exit code {process.ExitCode}.");
        }
        return new ProtocolCommandResult(process.ExitCode, standardOutput, standardError);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[8192];
        var result = new StringBuilder();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return result.ToString();
            int remaining = MaximumOutputCharacters - result.Length;
            if (remaining > 0)
                result.Append(buffer, 0, Math.Min(read, remaining));
        }
    }

    internal static bool IsAccessDeniedOutput(string? output)
        => output?.Contains("NT_STATUS_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase) == true
           || output?.Contains("NT_STATUS_PRIVILEGE_NOT_HELD", StringComparison.OrdinalIgnoreCase) == true
           || output?.Contains("permission denied", StringComparison.OrdinalIgnoreCase) == true
           || output?.Contains("access denied", StringComparison.OrdinalIgnoreCase) == true;
}
