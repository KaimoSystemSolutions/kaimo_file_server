using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>
/// A canonical, ETag-style weak validator for a single file/directory, derived only
/// from values a client already holds after a delta enumeration (size + modified
/// time). Because it is reproducible on the device, a client can build an
/// <c>If-Match</c> header from its stored baseline without a metadata round trip.
///
/// The tag is intentionally coarse — it detects "the item changed since I last saw
/// it", which is exactly the precondition the two-way conflict logic needs.
/// </summary>
public static class ItemTag
{
    /// <summary>The quoted tag for the given metadata, e.g. <c>"1234:6382..."</c>.</summary>
    public static string For(FileMetadata metadata)
        => For(metadata.Size, metadata.ModifiedAt);

    /// <summary>The quoted tag for a raw size + modified time.</summary>
    public static string For(long size, DateTime modifiedAt)
        => $"\"{size}:{modifiedAt.ToUniversalTime().Ticks}\"";

    /// <summary>
    /// True when an <c>If-Match</c> header value satisfies the current item.
    /// Accepts <c>*</c> (matches any existing item) and a comma-separated list of
    /// tags; surrounding whitespace and the weak-validator <c>W/</c> prefix are
    /// tolerated. An empty/whitespace header is treated as "no condition" → true.
    /// </summary>
    public static bool Matches(string? ifMatchHeader, FileMetadata current)
    {
        if (string.IsNullOrWhiteSpace(ifMatchHeader))
            return true;

        var expected = For(current);
        foreach (var raw in ifMatchHeader.Split(','))
        {
            var candidate = raw.Trim();
            if (candidate.Length == 0)
                continue;
            if (candidate == "*")
                return true;
            if (candidate.StartsWith("W/", StringComparison.Ordinal))
                candidate = candidate[2..].Trim();
            if (candidate == expected)
                return true;
        }

        return false;
    }
}
