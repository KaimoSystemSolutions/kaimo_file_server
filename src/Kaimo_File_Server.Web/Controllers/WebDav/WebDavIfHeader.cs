namespace Kaimo_File_Server.Web.Controllers.WebDav;

/// <summary>
/// Minimal RFC 4918 <c>If:</c> header parsing. The full grammar expresses
/// arbitrary state/etag preconditions, but the only precondition WebDAV writes
/// need in this server is "which lock tokens is the client presenting" — so this
/// extracts the (non-negated) state-tokens from the parenthesized condition
/// lists, in both the tagged and untagged forms. ETag conditions in
/// <c>[...]</c> and negated <c>Not &lt;token&gt;</c> entries are ignored for lock
/// evaluation (a client asserting it does <i>not</i> hold a token is not
/// presenting it).
/// </summary>
public static class WebDavIfHeader
{
    /// <summary>Returns every lock state-token the client presents, unwrapped from its angle brackets.</summary>
    public static IReadOnlyList<string> ParseLockTokens(string? header)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(header))
            return tokens;

        int depth = 0;      // inside a (...) condition list?
        bool negated = false;
        int i = 0;
        while (i < header.Length)
        {
            char c = header[i];
            switch (c)
            {
                case '(':
                    depth++;
                    i++;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    negated = false;
                    i++;
                    break;
                case '<':
                {
                    int end = header.IndexOf('>', i + 1);
                    if (end < 0) return tokens; // malformed — stop
                    var inner = header[(i + 1)..end];
                    // Only angle-bracket tokens inside a condition list are lock
                    // state-tokens; those outside (depth 0) are tagged-resource URIs.
                    if (depth > 0 && !negated)
                        tokens.Add(inner);
                    negated = false;
                    i = end + 1;
                    break;
                }
                case '[':
                {
                    // ETag condition — consume and skip.
                    int end = header.IndexOf(']', i + 1);
                    if (end < 0) return tokens;
                    negated = false;
                    i = end + 1;
                    break;
                }
                default:
                    if (char.ToUpperInvariant(c) == 'N' &&
                        i + 3 <= header.Length &&
                        header.AsSpan(i, 3).Equals("Not", StringComparison.OrdinalIgnoreCase))
                    {
                        negated = true;
                        i += 3;
                    }
                    else
                    {
                        i++;
                    }
                    break;
            }
        }

        return tokens;
    }
}
