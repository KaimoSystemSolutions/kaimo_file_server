using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Web.Services;

/// <summary>Why <see cref="PublicShareAccessPolicy"/> refused an anonymous access.</summary>
public enum PublicShareDenial
{
    None,
    HostNotAllowed,
    DownloadLinksDisabled,
    /// <summary>Unknown/deleted, disabled, outside its window, used up, or not a download link.</summary>
    LinkUnavailable,
    ShareUnavailable,
    CreatorUnavailable,
    InvalidPath,
    OutsideRoot,
}

/// <summary>
/// Outcome of a <see cref="PublicShareAccessPolicy"/> check. When allowed, carries the fresh
/// link, its share, the creator identity the access runs under and the normalized paths.
/// </summary>
public sealed record PublicShareAccess(
    PublicShareDenial Denial,
    ShareLink? Link = null,
    ShareDefinition? Share = null,
    UserContext? Actor = null,
    IReadOnlyList<string>? Paths = null)
{
    public bool Allowed => Denial == PublicShareDenial.None;

    /// <summary>The link itself no longer serves anything (as opposed to a bad path in an otherwise valid request).</summary>
    public bool LinkUnavailable => Denial is not (PublicShareDenial.None or PublicShareDenial.InvalidPath or PublicShareDenial.OutsideRoot);
}
