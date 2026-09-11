using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// Pure, database-free parsing of the WebDAV URL space:
///
/// <code>
/// /dav/                          the root collection (share list)
/// /dav/{shareName}/              a share root
/// /dav/{shareName}/sub/file.txt  an item inside the share
/// </code>
///
/// Shares are addressed by <b>name</b> so a mapped drive reads like its SMB
/// counterpart. Path segments beneath the share are validated through
/// <see cref="ShareRelativePath.TryNormalizeStrict"/>, so traversal never reaches
/// the storage layer. Kept free of I/O so the mapping is unit-testable in isolation.
/// </summary>
public static class WebDavPathResolver
{
    /// <summary>The route prefix the WebDAV service is mounted on.</summary>
    public const string Prefix = "/dav";

    /// <summary>Outcome of resolving the <c>Destination</c> header of a MOVE/COPY.</summary>
    public enum DestinationOutcome
    {
        Ok,
        /// <summary>The header was missing or not a usable URL/path.</summary>
        Invalid,
        /// <summary>The header pointed at a different host — a cross-server MOVE/COPY (502).</summary>
        ForeignHost,
    }

    /// <summary>
    /// Splits the decoded request path (e.g. <c>/dav/Projekte/sub/file.txt</c>)
    /// into its share name and the normalized share-relative path.
    /// </summary>
    /// <returns>
    /// <c>true</c> and (share, path) for anything at or below a share root;
    /// <c>false</c> for a malformed path. The bare root (<c>/dav</c> or <c>/dav/</c>)
    /// returns <c>true</c> with an empty <paramref name="shareName"/>.
    /// </returns>
    public static bool TrySplit(string? requestPath, out string shareName, out string relativePath)
    {
        shareName = string.Empty;
        relativePath = string.Empty;

        if (requestPath is null)
            return false;

        var trimmed = requestPath.Replace('\\', '/');

        // Strip the /dav prefix (case-insensitive) and any surrounding slashes.
        if (trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[Prefix.Length..];
        trimmed = trimmed.Trim('/');

        if (trimmed.Length == 0)
            return true; // root collection

        var slash = trimmed.IndexOf('/');
        if (slash < 0)
        {
            shareName = trimmed;
            return SambaName.IsValidShareName(shareName);
        }

        shareName = trimmed[..slash];
        if (!SambaName.IsValidShareName(shareName))
            return false;

        // Recycle-bin and versioning paths are legal targets, so allow the
        // internal-namespace guard to pass here; per-item ACLs still apply, and
        // the file service rejects writes into reserved namespaces itself.
        return ShareRelativePath.TryNormalizeStrict(
            trimmed[(slash + 1)..], out relativePath,
            allowRoot: true, allowInternalNamespace: true);
    }

    /// <summary>
    /// Resolves a <c>Destination</c> header against the current request's host.
    /// Accepts both an absolute URL (<c>https://host:8443/dav/...</c>) and an
    /// origin-relative path (<c>/dav/...</c>). A destination on a different host
    /// yields <see cref="DestinationOutcome.ForeignHost"/>.
    /// </summary>
    public static DestinationOutcome TryResolveDestination(
        string? destination, string requestHost,
        out string shareName, out string relativePath)
    {
        shareName = string.Empty;
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(destination))
            return DestinationOutcome.Invalid;

        string path;
        // Only treat it as an absolute URL when it actually carries an http(s) scheme.
        // On Linux, Uri.TryCreate parses a leading-slash path like "/dav/x" as an
        // absolute file:// URI (empty authority), which must NOT be mistaken for a
        // cross-server destination — it is the ordinary origin-relative form.
        if (Uri.TryCreate(destination, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            // Compare host[:port] case-insensitively; a mismatch is a cross-server move.
            if (!string.Equals(absolute.Authority, requestHost, StringComparison.OrdinalIgnoreCase))
                return DestinationOutcome.ForeignHost;
            path = Uri.UnescapeDataString(absolute.AbsolutePath);
        }
        else if (destination.StartsWith('/'))
        {
            // Origin-relative form (some clients send this). No host to compare.
            path = Uri.UnescapeDataString(destination);
        }
        else
        {
            return DestinationOutcome.Invalid;
        }

        return TrySplit(path, out shareName, out relativePath)
            ? DestinationOutcome.Ok
            : DestinationOutcome.Invalid;
    }
}
