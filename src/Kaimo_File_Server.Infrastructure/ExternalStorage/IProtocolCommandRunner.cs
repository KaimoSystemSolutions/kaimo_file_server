namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

public interface IProtocolCommandRunner
{
    Task<ProtocolCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true);

    /// <summary>Same, with extra environment variables for the helper (e.g. <c>TZ</c>).</summary>
    Task<ProtocolCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken,
        bool throwOnFailure = true);
}
public sealed record ProtocolCommandResult(int ExitCode, string StandardOutput, string StandardError = "");
