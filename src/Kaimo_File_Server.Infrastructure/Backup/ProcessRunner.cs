using System.Diagnostics;
using System.Text;

namespace Kaimo_File_Server.Infrastructure.Backup;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="Process"/>. Streams
/// stdout/stderr concurrently so a chatty child process cannot dead-lock on a
/// full pipe buffer, and kills the process tree if cancellation is requested.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // Variables a scrubbed child still needs to find its tools, home and locale.
    private static readonly string[] PreservedVariables = ["PATH", "HOME", "LANG", "LC_ALL", "TZ", "TMPDIR"];
    private const int StderrTailCharacters = 16 * 1024;

    public async Task<ProcessResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onStdoutLine,
        bool clearEnvironment,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringBuilder();
        return await RunCoreAsync(fileName, arguments, environment, clearEnvironment, process => Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (onStdoutLine is not null) onStdoutLine(line);
                else stdout.AppendLine(line);
            }
        }, CancellationToken.None), () => stdout.ToString(), cancellationToken);
    }

    public Task<ProcessResult> RunToStreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        Stream stdoutTarget,
        bool clearEnvironment,
        CancellationToken cancellationToken = default)
        // A failing target (client disconnected) surfaces from the copy task; cancellation kills the process.
        => RunCoreAsync(fileName, arguments, environment, clearEnvironment,
            process => process.StandardOutput.BaseStream.CopyToAsync(stdoutTarget, cancellationToken),
            () => string.Empty, cancellationToken);

    private static async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        bool clearEnvironment,
        Func<Process, Task> readStdout,
        Func<string> stdoutText,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (clearEnvironment)
        {
            var keep = PreservedVariables
                .Select(name => (name, value: Environment.GetEnvironmentVariable(name)))
                .Where(v => v.value is not null)
                .ToList();
            startInfo.Environment.Clear();
            foreach (var (name, value) in keep)
                startInfo.Environment[name] = value;
        }
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTail = new StringBuilder();
        var stdoutTask = readStdout(process);
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
            {
                stderrTail.AppendLine(line);
                if (stderrTail.Length > StderrTailCharacters * 2)
                    stderrTail.Remove(0, stderrTail.Length - StderrTailCharacters);
            }
        }, CancellationToken.None);

        try
        {
            var exited = process.WaitForExitAsync(cancellationToken);
            // A failing stdout consumer (e.g. the download client went away) must not leave the
            // process blocked on a full pipe until it is cancelled.
            if (await Task.WhenAny(exited, stdoutTask) == stdoutTask && stdoutTask.IsFaulted)
                await stdoutTask;
            await exited;
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (Exception)
        {
            TryKill(process);
            throw;
        }

        var tail = stderrTail.ToString();
        if (tail.Length > StderrTailCharacters)
            tail = tail[^StderrTailCharacters..];
        return new ProcessResult(process.ExitCode, stdoutText(), tail);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup; the process may already have exited.
        }
    }
}
