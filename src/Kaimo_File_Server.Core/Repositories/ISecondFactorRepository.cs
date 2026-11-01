using Kaimo_File_Server.Core.Domain.Identity;

namespace Kaimo_File_Server.Core.Repositories;

/// <summary>Storage of the TOTP second factor and the recovery codes of web-login accounts.</summary>
public interface ISecondFactorRepository
{
    Task<UserSecondFactor?> GetAsync(Guid userId);

    /// <summary>Ids of all users with a confirmed second factor.</summary>
    Task<HashSet<Guid>> GetConfirmedUserIdsAsync();

    /// <summary>
    /// Confirms an enrollment in one transaction: stores the secret as confirmed with the step
    /// of the verifying code and replaces all recovery codes. Returns <c>false</c> (and changes
    /// nothing) when the user already has a confirmed factor.
    /// </summary>
    Task<bool> ConfirmAsync(Guid userId, string secretProtected, long verifiedStep,
        IReadOnlyCollection<string> recoveryCodeHashes, DateTime nowUtc);

    /// <summary>
    /// Records <paramref name="step"/> as the last used step, only when it is later than the
    /// stored one (a conditional update, so two parallel logins cannot both use one code).
    /// </summary>
    Task<bool> TryAdvanceStepAsync(Guid userId, long step);

    /// <summary>Marks the unused code with this hash as used; <c>false</c> when there is none.</summary>
    Task<bool> TryUseRecoveryCodeAsync(Guid userId, string codeHash, DateTime nowUtc);

    Task<int> CountUnusedRecoveryCodesAsync(Guid userId);

    /// <summary>Replaces all recovery codes of a user with a confirmed factor.</summary>
    Task ReplaceRecoveryCodesAsync(Guid userId, IReadOnlyCollection<string> codeHashes);

    /// <summary>
    /// Removes the factor and all recovery codes; <c>false</c> (and nothing changed) when there was
    /// none. With <paramref name="rotateSecurityStamp"/> the user's security stamp is renewed in the
    /// same transaction, which ends every web session and client-API access token of the account.
    /// </summary>
    Task<bool> DeleteAsync(Guid userId, bool rotateSecurityStamp = false);
}
