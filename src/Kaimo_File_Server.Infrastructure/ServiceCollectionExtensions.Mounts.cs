namespace Kaimo_File_Server.Infrastructure;

public static partial class ServiceCollectionExtensions
{
    public static List<string> GetAllActiveMounts()
    {
        List<string> result = [];

        static IEnumerable<string> GetMountedPaths()
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2)
                    yield return UnescapeMountPath(parts[1]); // field 2 = mount point
            }
        }

        result.AddRange(GetMountedPaths());

        return result;
    }

    /// <summary>Decodes the octal escapes the kernel uses in /proc/mounts and mountinfo paths.</summary>
    internal static string UnescapeMountPath(string path) =>
        path.Replace("\\040", " ")
            .Replace("\\011", "\t")
            .Replace("\\012", "\n")
            .Replace("\\134", "\\");

    public static List<string> GetActiveStorageMounts()
    {
        List<string> result = GetAllActiveMounts().Select(path => path).Where(path => path.StartsWith("/data/storage/")).ToList();
        return result;
    }
}
