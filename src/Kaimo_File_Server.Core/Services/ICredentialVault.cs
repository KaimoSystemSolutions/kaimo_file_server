using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Services;

/// <summary>
/// Describes the immutable identity of a protected credential payload.
/// The values are included in the encryption purpose so a payload cannot be
/// moved to another connection, provider, or credential kind.
/// </summary>
/// <param name="ConnectionId">Connection that owns the credential.</param>
/// <param name="ProviderId">Stable provider identifier, such as <c>onedrive</c>.</param>
/// <param name="CredentialKind">Provider-defined credential category.</param>
/// <param name="FormatVersion">Version of the serialized plaintext contract.</param>
public sealed record CredentialContext(
    Guid ConnectionId,
    string ProviderId,
    string CredentialKind,
    int FormatVersion)
{
    public const string AuthorizationGrantKind = "authorization-grant";
    public const int CurrentAuthorizationGrantVersion = 1;

    /// <summary>Creates the context used by an external-storage authorization grant.</summary>
    public static CredentialContext ForAuthorizationGrant(Guid connectionId, string providerId)
        => new(connectionId, providerId, AuthorizationGrantKind, CurrentAuthorizationGrantVersion);
}

/// <summary>
/// Protects typed credentials using authenticated, context-bound encryption.
/// Implementations must never persist or log the plaintext value.
/// </summary>
public interface ICredentialVault
{
    string Protect<T>(T credential, CredentialContext context);
    T Unprotect<T>(string protectedValue, CredentialContext context);
}

/// <summary>Convenience operations for the current Cloud Access compatibility model.</summary>
public static class CredentialVaultConnectionExtensions
{
    public static string ProtectConnectionCredentials(
        this ICredentialVault vault,
        StorageConnection connection,
        IReadOnlyDictionary<string, string> credentials)
        => vault.Protect(
            credentials,
            CredentialContext.ForAuthorizationGrant(connection.Id, connection.ProviderId));

    public static Dictionary<string, string> UnprotectConnectionCredentials(
        this ICredentialVault vault,
        StorageConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.EncryptedCredentialPayload))
            throw new InvalidOperationException("The storage connection has no protected credentials.");

        return vault.Unprotect<Dictionary<string, string>>(
            connection.EncryptedCredentialPayload,
            CredentialContext.ForAuthorizationGrant(connection.Id, connection.ProviderId));
    }
}
