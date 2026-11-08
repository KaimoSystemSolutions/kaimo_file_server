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

    /// <summary>
    /// Runs a helper without throwing on a non-zero exit code and stops it as soon as its output
    /// passes the limit (result flagged <see cref="ProtocolCommandResult.OutputTruncated"/>),
    /// instead of letting it finish work whose output is discarded anyway. Only for read-only
    /// commands: a stopped helper may leave its work half done.
    /// </summary>
    /// <param name="maximumOutputCharacters">
    /// Standard output kept before the helper is stopped; <c>null</c> = the runner's general limit.
    /// </param>
    Task<ProtocolCommandResult> RunUntilOutputLimitAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        int? maximumOutputCharacters = null)
        => RunAsync(executable, arguments, cancellationToken, throwOnFailure: false);
}
/// <param name="OutputTruncated">True when the helper wrote more than the runner keeps; the output is incomplete.</param>
public sealed record ProtocolCommandResult(
    int ExitCode, string StandardOutput, string StandardError = "", bool OutputTruncated = false);
