namespace Kaimo_File_Server.Web.Components.Search;

/// <summary>
/// Splits a search highlight snippet into plain-text segments. A snippet is raw,
/// unencoded text (file names and file content are user-controlled) in which the
/// search backends mark matches with literal <c>&lt;mark&gt;</c>/<c>&lt;/mark&gt;</c>
/// tags. Rendering the segments as text lets Blazor encode every character, so a
/// snippet can never inject markup or script.
/// </summary>
public static class SearchSnippet
{
    private const string Open = "<mark>";
    private const string Close = "</mark>";

    public readonly record struct Segment(string Text, bool Highlighted);

    /// <summary>
    /// True when the snippet says more than the file name (e.g. a content excerpt). A name-only
    /// match (filename search) just repeats the name and is not worth showing next to it.
    /// </summary>
    public static bool AddsTo(string? snippet, string name)
    {
        var text = string.Concat(Split(snippet).Select(s => s.Text)).Trim();
        return text.Length > 0 && !text.Equals(name, StringComparison.CurrentCultureIgnoreCase);
    }

    public static IEnumerable<Segment> Split(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet))
            yield break;

        int position = 0;
        while (position < snippet.Length)
        {
            int open = snippet.IndexOf(Open, position, StringComparison.Ordinal);
            if (open < 0)
                break;

            int close = snippet.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);
            if (close < 0)
                break;

            if (open > position)
                yield return new Segment(snippet[position..open], false);
            if (close > open + Open.Length)
                yield return new Segment(snippet[(open + Open.Length)..close], true);

            position = close + Close.Length;
        }

        if (position < snippet.Length)
            yield return new Segment(snippet[position..], false);
    }
}
