namespace Kaimo_File_Server.Core.Security.Authentication;

/// <summary>
/// Authenticates a username/password pair with built-in brute-force
/// protection (see <see cref="ILoginThrottle"/>) and username-enumeration
/// resistance (constant-time verification regardless of whether the user
/// exists). Transport layers (web login, REST) call this instead of touching
/// the password hash directly.
/// </summary>
public interface ILoginService
{
    /// <param name="remoteAddress">
    /// Client address, when the transport knows it. Lockout is then tracked per
    /// (username, address), so failed attempts from one client cannot lock the
    /// account out for everyone else.
    /// </param>
    /// <param name="host">
    /// Host name the server was reached under (request <c>Host</c>), checked against the
    /// access restriction rules together with <paramref name="remoteAddress"/>.
    /// </param>
    Task<LoginResult> AuthenticateAsync(string username, string password, string? remoteAddress = null, string? host = null);
}
