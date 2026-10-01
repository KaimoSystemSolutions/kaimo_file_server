namespace Kaimo_File_Server.Search;

/// <summary>
/// Result-size bounds shared by every search backend. Raw hits are ACL-filtered in
/// pages, so a user who may read only a few of the matches (e.g. other users' home
/// folders match too) still gets results instead of an empty first page.
/// </summary>
public static class SearchLimits
{
    /// <summary>Upper bound of raw hits scanned per search, so paging stays cheap.</summary>
    // ponytail: fixed scan cap; very restrictive ACLs over huge match sets can still miss hits
    public const int MaxRawScan = 1000;

    /// <summary>
    /// Paging stops once this many readable hits are collected. Equals the largest
    /// page any caller asks for (client API maximum).
    /// </summary>
    public const int MinVisibleHits = 50;
}
