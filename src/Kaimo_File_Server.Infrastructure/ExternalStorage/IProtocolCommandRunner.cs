namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public interface IProtocolCommandRunner
{
    Task<ProtocolCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true);
}
public sealed record ProtocolCommandResult(int ExitCode, string StandardOutput, string StandardError = "");
