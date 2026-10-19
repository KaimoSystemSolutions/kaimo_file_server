namespace Kaimo_File_Server.Infrastructure.ExternalStorage;

internal static class WebDavPaths
{
    public static string NormalizeStorePath(string? path)
    {
        string value = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (value.Length > 4096 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ProtocolConfigurationException("remote_path_invalid", "The remote path is invalid.");
        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new ProtocolConfigurationException("remote_path_invalid", "The remote path is invalid.");
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    public static string CombineChild(string normalizedStorePath, string childName)
        => normalizedStorePath == "/" ? "/" + childName : normalizedStorePath + "/" + childName;
}
