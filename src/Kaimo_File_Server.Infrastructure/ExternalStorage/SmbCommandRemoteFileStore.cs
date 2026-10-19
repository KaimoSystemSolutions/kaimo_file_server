using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kaimo_File_Server.Core.Services.ExternalStorage;

namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

internal sealed record SmbCredentials(string Username, string Password, string? Domain);
internal sealed class SmbCommandRemoteFileStore(
    SmbConnectionSettings settings,
    SmbCredentials credentials,
    IProtocolCommandRunner runner) : IRemoteFileStore
{
    public async Task<IReadOnlyList<RemoteStorageItem>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string normalized = Normalize(path);
        if (normalized == "/")
        {
            ProtocolCommandResult result = await RunAsync(null, null, listShares: true, cancellationToken);
            return ParseShares(result.StandardOutput);
        }
        (string share, string relative) = Split(normalized);
        // smbclient's ls argument is a mask in the current directory, not a
        // directory to enter. Change directory first so nested browsing lists
        // the children instead of merely matching the selected folder itself.
        string command = relative.Length == 0 ? "ls" : $"cd {QuoteRemote(relative)}; ls";
        ProtocolCommandResult listing = await RunAsync(share, command, false, cancellationToken);
        return ParseDirectory(share, relative, listing.StandardOutput);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        (string share, string relative) = SplitFile(path);
        string temporary = ProtocolTemporaryFile.CreatePath();
        try
        {
            await RunAsync(share, $"get {QuoteRemote(relative)} {QuoteLocal(temporary)}", false, cancellationToken);
            return ProtocolTemporaryFile.OpenDeleteOnClose(temporary);
        }
        catch { File.Delete(temporary); throw; }
    }

    public async Task WriteAsync(
        string path,
        Stream content,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        (string share, string relative) = SplitFile(path);
        if (!overwrite)
        {
            int separator = relative.LastIndexOf('/');
            string parent = separator < 0 ? $"/{share}" : $"/{share}/{relative[..separator]}";
            string name = relative[(separator + 1)..];
            if ((await ListAsync(parent, cancellationToken)).Any(item =>
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The remote item already exists.");
        }
        string temporary = ProtocolTemporaryFile.CreatePath();
        try
        {
            await using (var target = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await content.CopyToAsync(target, cancellationToken);
            await RunAsync(share, $"put {QuoteLocal(temporary)} {QuoteRemote(relative)}", false, cancellationToken);
        }
        finally { File.Delete(temporary); }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        (string share, string relative) = SplitFile(path);
        await RunAsync(share, $"mkdir {QuoteRemote(relative)}", false, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        (string share, string relative) = SplitFile(path);
        if (recursive)
        {
            await RunAsync(share, $"deltree {QuoteRemote(relative)}", false, cancellationToken);
            return;
        }
        ProtocolCommandResult deleted = await RunAsync(
            share, $"del {QuoteRemote(relative)}", false, cancellationToken, throwOnFailure: false);
        if (deleted.ExitCode != 0)
            await RunAsync(share, $"rmdir {QuoteRemote(relative)}", false, cancellationToken);
    }

    public async Task MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        (string sourceShare, string source) = SplitFile(sourcePath);
        (string destinationShare, string destination) = SplitFile(destinationPath);
        if (!string.Equals(sourceShare, destinationShare, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("SMB items cannot be moved between shares in one operation.");
        await RunAsync(sourceShare,
            $"rename {QuoteRemote(source)} {QuoteRemote(destination)}", false, cancellationToken);
    }

    private async Task<ProtocolCommandResult> RunAsync(
        string? share,
        string? command,
        bool listShares,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
    {
        string authFile = ProtocolTemporaryFile.CreatePath();
        try
        {
            var auth = new StringBuilder()
                .Append("username = ").AppendLine(credentials.Username)
                .Append("password = ").AppendLine(credentials.Password);
            if (!string.IsNullOrWhiteSpace(credentials.Domain))
                auth.Append("domain = ").AppendLine(credentials.Domain);
            await ProtocolTemporaryFile.WriteRestrictedTextAsync(authFile, auth.ToString(), cancellationToken);
            var arguments = new List<string> { "-g" };
            if (listShares)
                arguments.AddRange(["-L", $"//{settings.Server}"]);
            else
                arguments.Add($"//{settings.Server}/{share}");
            arguments.AddRange(["-A", authFile, "-p", settings.Port.ToString(CultureInfo.InvariantCulture)]);
            string minimumProtocol = settings.MinimumDialect switch
            {
                "3.1.1" => "SMB3_11",
                "3.02" => "SMB3_02",
                _ => "SMB3"
            };
            arguments.AddRange(["--option", $"client min protocol={minimumProtocol}"]);
            arguments.Add("--client-protection=encrypt");
            if (command is not null)
                arguments.AddRange(["-c", command]);
            return await runner.RunAsync("smbclient", arguments, cancellationToken, throwOnFailure);
        }
        finally { File.Delete(authFile); }
    }

    private static IReadOnlyList<RemoteStorageItem> ParseShares(string output)
        => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|'))
            .Where(fields => fields.Length >= 2 && string.Equals(fields[0], "Disk", StringComparison.OrdinalIgnoreCase))
            .Where(fields => !fields[1].EndsWith('$'))
            .Select(fields => new RemoteStorageItem(
                fields[1], $"/{fields[1]}", true, null, null, $"smb-share:{fields[1]}"))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<RemoteStorageItem> ParseDirectory(string share, string parent, string output)
    {
        var result = new List<RemoteStorageItem>();
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseDirectoryLine(
                    line, out string name, out string attributes, out long? size, out DateTime? modifiedAtUtc))
                continue;
            if (name is "." or ".." || name.Length == 0)
                continue;
            bool directory = attributes.Contains('D', StringComparison.OrdinalIgnoreCase);
            string child = string.Join('/', new[] { share, parent, name }.Where(value => value.Length > 0));
            result.Add(new RemoteStorageItem(
                name, $"/{child}", directory, directory ? null : size, modifiedAtUtc));
        }
        return result;
    }

    private static bool TryParseDirectoryLine(
        string line,
        out string name,
        out string attributes,
        out long? size,
        out DateTime? modifiedAtUtc)
    {
        name = string.Empty;
        attributes = string.Empty;
        size = null;
        modifiedAtUtc = null;

        // Some downstream Samba builds expose a pipe-delimited directory
        // format. Accept both known field orders without confusing timestamps
        // with names.
        string[] fields = line.Split('|');
        if (fields.Length >= 4)
        {
            if (IsAttributeField(fields[0]) && TrySize(fields[1], out size))
            {
                attributes = fields[0].Trim();
                name = fields[^1].Trim();
                modifiedAtUtc = ParseDate(fields[2]);
                return name.Length > 0;
            }
            if (IsAttributeField(fields[1]) && TrySize(fields[2], out size))
            {
                name = fields[0].Trim();
                attributes = fields[1].Trim();
                modifiedAtUtc = ParseDate(string.Join('|', fields.Skip(3)));
                return name.Length > 0;
            }
        }

        // Upstream smbclient applies --grepable to server/share discovery but
        // normally keeps the classic fixed-column directory representation.
        // Parse from the attribute/size boundary so names may contain spaces.
        Match match = Regex.Match(
            line,
            @"^\s*(?<name>.+?)\s+(?<attributes>[ADHNRSTV]+)\s+(?<size>\d+)\s+(?<date>.+)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success || !TrySize(match.Groups["size"].Value, out size))
            return false;
        name = match.Groups["name"].Value.Trim();
        attributes = match.Groups["attributes"].Value;
        modifiedAtUtc = ParseDate(match.Groups["date"].Value);
        return name.Length > 0;
    }

    private static bool IsAttributeField(string value)
    {
        string candidate = value.Trim();
        return candidate.Length > 0 && candidate.All(character => "ADHNRSTV".Contains(
            char.ToUpperInvariant(character), StringComparison.Ordinal));
    }

    private static bool TrySize(string value, out long? size)
    {
        bool parsed = long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long result);
        size = parsed ? result : null;
        return parsed;
    }

    private static DateTime? ParseDate(string value)
    {
        string normalized = value.Trim();
        if (DateTime.TryParseExact(
                normalized,
                "ddd MMM d HH:mm:ss yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out DateTime classic))
            return classic.ToUniversalTime();
        return DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out DateTimeOffset parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private static (string Share, string Relative) Split(string path)
    {
        string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new ProtocolConfigurationException("remote_path_invalid", "Select an SMB share first.");
        return (segments[0], string.Join('/', segments.Skip(1)));
    }

    private static (string Share, string Relative) SplitFile(string path)
    {
        var split = Split(Normalize(path));
        if (split.Relative.Length == 0)
            throw new ProtocolConfigurationException("remote_path_invalid", "The SMB share root is not a file path.");
        return split;
    }

    private static string Normalize(string path)
    {
        string normalized = string.IsNullOrWhiteSpace(path) ? "/" : "/" + path.Replace('\\', '/').Trim('/');
        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment is "." or ".." || segment.IndexOfAny(['\r', '\n', '"', ';']) >= 0)
                throw new ProtocolConfigurationException("remote_path_invalid", "The SMB path is invalid.");
        return normalized;
    }

    internal static string QuoteRemote(string value) => $"\"{value.Replace('/', '\\')}\"";
    internal static string QuoteLocal(string value) => $"\"{value}\"";
}
