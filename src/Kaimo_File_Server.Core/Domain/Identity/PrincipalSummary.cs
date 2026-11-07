namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// Slim, immutable view of a user, group or role for pickers and name lookups.
    /// Carries only what a list needs, so loading every principal never materializes
    /// photos or credential hashes. Value equality lets caches detect changes cheaply.
    /// </summary>
    /// <param name="Id">The principal's id.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Username">Login name; users only.</param>
    /// <param name="Email">E-mail address; users only, shown as a picker hint.</param>
    public sealed record PrincipalSummary(Guid Id, string Name, string? Username = null, string? Email = null);
}
