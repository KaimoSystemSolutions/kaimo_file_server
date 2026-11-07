using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Immutable snapshot of all principals a picker can offer, held by <see cref="PrincipalDirectoryCache"/>.
/// </summary>
public sealed record PrincipalDirectory(
    IReadOnlyList<PrincipalSummary> Users,
    IReadOnlyList<PrincipalSummary> Groups,
    IReadOnlyList<PrincipalSummary> Roles)
{
    public static readonly PrincipalDirectory Empty = new([], [], []);

    /// <summary>True when both snapshots list the same principals with the same names, in the same order.</summary>
    public bool HasSameContentAs(PrincipalDirectory other) =>
        Users.SequenceEqual(other.Users)
        && Groups.SequenceEqual(other.Groups)
        && Roles.SequenceEqual(other.Roles);
}
