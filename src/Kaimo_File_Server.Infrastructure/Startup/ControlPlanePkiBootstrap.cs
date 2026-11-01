using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Infrastructure.Startup;

/// <summary>
/// Host startup step that prepares the SMB control-plane mTLS PKI by running the
/// bundled <c>generate-control-plane-certs</c> script (formerly the separate
/// one-shot pki-init container). The script is idempotent and leaves an existing
/// or externally managed PKI untouched. Failures are logged, never thrown: the
/// Host owns the database migration, and only the SMB bridge needs the PKI.
/// </summary>
public static class ControlPlanePkiBootstrap
{
    public const string DefaultGeneratorPath = "/usr/local/bin/generate-control-plane-certs";
    public const string DefaultDirectory = "/data/smb-control-plane";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static bool RunFromConfiguration(ILogger logger, IConfiguration config)
        => Run(
            logger,
            config["ControlPlanePki:GeneratorPath"] ?? DefaultGeneratorPath,
            config["ControlPlanePki:Directory"] ?? DefaultDirectory,
            Timeout);

    /// <summary>
    /// Runs <paramref name="generatorPath"/> with <paramref name="directory"/> as its
    /// only argument. Returns true when the PKI is present afterwards.
    /// </summary>
    public static bool Run(ILogger logger, string generatorPath, string directory, TimeSpan timeout)
    {
        if (!File.Exists(generatorPath))
        {
            logger.LogWarning(
                "SMB control-plane PKI generator {Generator} not found; the PKI in {Directory} is not prepared.",
                generatorPath, directory);
            return false;
        }

        var startInfo = new ProcessStartInfo(generatorPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(directory);

        try
        {
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                logger.LogError(
                    "SMB control-plane PKI generation timed out after {Timeout}; the SMB bridge will not start.",
                    timeout);
                return false;
            }

            string messages = (output.Result + error.Result).Trim();
            if (process.ExitCode == 0)
            {
                logger.LogInformation("SMB control-plane PKI: {Messages}", messages);
                return true;
            }

            logger.LogError(
                "SMB control-plane PKI generation failed (exit code {ExitCode}): {Messages}. "
                + "The SMB bridge will not start. Grant the container user (UID 1654) write access "
                + "to the host directory mounted at {Directory} and its bridge/ and samba/ subfolders "
                + "(Docker creates missing ones as root), e.g. 'chown -R 1654:1654 <host path>'.",
                process.ExitCode, messages, directory);
            return false;
        }
        catch (Win32Exception ex)
        {
            logger.LogError(ex, "SMB control-plane PKI generator {Generator} could not be started.", generatorPath);
            return false;
        }
    }
}
