using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Web.DynamicHelpers;

/// <summary>
/// Renders a user's stored profile picture (AD <c>thumbnailPhoto</c> equivalent) as an
/// inline <c>data:</c> URI. Kept as bytes-to-data-URI so the image travels with the user
/// object the page already loads — no separate authenticated image endpoint is needed at
/// the small sizes profile photos use.
/// </summary>
public static class UserAvatar
{
    /// <summary>Data URI for the given photo bytes, or <c>null</c> when there is no photo.</summary>
    public static string? DataUri(byte[]? photo, string? contentType)
        => photo is { Length: > 0 }
            ? $"data:{(string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType)};base64,{Convert.ToBase64String(photo)}"
            : null;

    /// <summary>Data URI for a user's photo, or <c>null</c> when the user has none.</summary>
    public static string? DataUri(User? user)
        => user is null ? null : DataUri(user.Photo, user.PhotoContentType);

    /// <summary>
    /// Two-letter fallback shown when a user has no photo. When a surname is present it is
    /// the first letter of the username plus the first letter of the surname; otherwise it
    /// falls back to the initials of the display name (or username).
    /// </summary>
    public static string Initials(User? user)
    {
        if (user is null) return "?";

        var lastName = user.LastName?.Trim();
        if (!string.IsNullOrEmpty(lastName) && !string.IsNullOrWhiteSpace(user.Username))
            return $"{char.ToUpperInvariant(user.Username[0])}{char.ToUpperInvariant(lastName[0])}";

        return InitialsFromText(
            string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name);
    }

    /// <summary>Initials of a free-text name: two words → their initials, one word → its
    /// first two letters, empty → "?".</summary>
    public static string InitialsFromText(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "?";
        var words = source.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])))
        };
    }
}
