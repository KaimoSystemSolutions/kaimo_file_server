namespace Kaimo_File_Server.Web.Helpers;

/// <summary>
/// Builds the '/'-separated sub-path part of a /files/... URL. Each segment is escaped so
/// characters with URL meaning ('#', '?', '%') stay part of the folder name; the router
/// decodes them back.
/// </summary>
public static class RouteSubPath
{
    public static string Escape(string subPath)
        => string.Join('/', subPath.Split('/').Select(Uri.EscapeDataString));
}
