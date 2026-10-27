using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// Matches share-relative paths against the glob patterns of
/// <see cref="VersioningPolicy.ExcludePatterns"/>. <c>*</c> and <c>?</c> never cross a
/// <c>/</c>; <c>**</c> does. Patterns without <c>/</c> are tested against the file name,
/// patterns with <c>/</c> against the whole path (a leading <c>/</c> is ignored).
/// </summary>
public sealed class VersioningExclusionMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private const int CacheLimit = 16;
    private static readonly ConcurrentDictionary<string, VersioningExclusionMatcher> Cache =
        new(StringComparer.Ordinal);

    private readonly Regex? _namePatterns;
    private readonly Regex? _pathPatterns;

    public VersioningExclusionMatcher(IEnumerable<string> patterns)
    {
        var names = new List<string>();
        var paths = new List<string>();
        foreach (var raw in patterns)
        {
            var pattern = raw?.Trim().Replace('\\', '/') ?? "";
            if (pattern.Length == 0) continue;
            if (pattern.Contains('/'))
                paths.Add(ToRegex(pattern.TrimStart('/')));
            else
                names.Add(ToRegex(pattern));
        }

        _namePatterns = Combine(names);
        _pathPatterns = Combine(paths);
    }

    /// <summary>A shared matcher for a pattern list; policies are re-read often but rarely change.</summary>
    public static VersioningExclusionMatcher For(IReadOnlyCollection<string>? patterns)
    {
        var key = string.Join('\n', patterns ?? []);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (Cache.Count >= CacheLimit) Cache.Clear();
        return Cache.GetOrAdd(key, _ => new VersioningExclusionMatcher(patterns ?? []));
    }

    public bool IsExcluded(string shareRelativePath)
    {
        var path = shareRelativePath.Replace('\\', '/').Trim('/');
        if (path.Length == 0) return false;

        var name = path[(path.LastIndexOf('/') + 1)..];
        try
        {
            return _namePatterns?.IsMatch(name) == true || _pathPatterns?.IsMatch(path) == true;
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological pattern must never block a write; version the file instead.
            return false;
        }
    }

    private static Regex? Combine(List<string> parts) => parts.Count == 0
        ? null
        : new Regex("^(?:" + string.Join("|", parts) + ")$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    private static string ToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }
}
