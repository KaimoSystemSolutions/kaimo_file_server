namespace Kaimo_File_Server.Infrastructure.Logging;

public sealed class LogArchiveOptions
{
    public const string SectionName = "LogArchive";

    public string RootPath { get; set; } = "logs";
    public string Source { get; set; } = "application";
    public string? Instance { get; set; }
    public int ChannelCapacity { get; set; } = 16_384;
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public int RetentionDays { get; set; } = 14;
    public long MaxBytesPerSource { get; set; } = 250 * 1024 * 1024;

    internal void Normalize()
    {
        RootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(RootPath) ? "logs" : RootPath);
        Source = LogArchivePath.SafeSegment(Source, "application");
        Instance = LogArchivePath.SafeSegment(
            string.IsNullOrWhiteSpace(Instance)
                ? Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName
                : Instance,
            "instance");
        ChannelCapacity = Math.Clamp(ChannelCapacity, 256, 262_144);
        MaxFileBytes = Math.Clamp(MaxFileBytes, 1024 * 1024, 1024L * 1024 * 1024);
        RetentionDays = Math.Clamp(RetentionDays, 1, 365);
        MaxBytesPerSource = Math.Max(MaxBytesPerSource, MaxFileBytes);
    }
}

internal static class LogArchivePath
{
    public static string SafeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var chars = value.Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-').ToArray();
        var result = new string(chars).Trim('-', '.');
        return string.IsNullOrEmpty(result) ? fallback : result[..Math.Min(result.Length, 80)];
    }
}
