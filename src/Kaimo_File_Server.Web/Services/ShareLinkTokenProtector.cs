using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.AspNetCore.DataProtection;

namespace Kaimo_File_Server.Web.Services;

/// <summary>
/// Keeps share-link tokens out of the database in plain text while links stay displayable:
/// the token is stored encrypted with the web host's Data Protection keys (which do not live
/// in the database), next to the SHA-256 hash used for lookups.
/// </summary>
public sealed class ShareLinkTokenProtector(IDataProtectionProvider provider, ILogger<ShareLinkTokenProtector> logger)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Kaimo.ShareLinks.Token.v1");

    /// <summary>Links whose legacy plain-text token is protected per startup pass.</summary>
    internal const int BackfillBatchSize = 500;

    public string Protect(string token) => _protector.Protect(token);

    /// <summary>
    /// The link's plain token for building its URL: the in-memory token when known, else the
    /// decrypted stored copy, else a not-yet-migrated legacy token. Null when unrecoverable
    /// (e.g. the Data Protection keys were lost).
    /// </summary>
    public string? Reveal(ShareLink link)
    {
        if (!string.IsNullOrEmpty(link.Token))
            return link.Token;

        if (!string.IsNullOrEmpty(link.ProtectedToken))
        {
            try
            {
                return _protector.Unprotect(link.ProtectedToken);
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex, "Share link {LinkId} cannot be decrypted; its URL cannot be shown.", link.Id);
                return null;
            }
        }

        return link.LegacyToken;
    }

    /// <summary>
    /// Replaces plain-text tokens of links created before hashing by their encrypted form
    /// (bounded per call). Links keep working throughout: lookups already use the hash.
    /// </summary>
    public async Task<int> ProtectLegacyTokensAsync(IShareLinkRepository links)
    {
        int protectedCount = 0;
        foreach (var link in await links.ListWithLegacyTokenAsync(BackfillBatchSize))
        {
            if (await links.TryProtectLegacyTokenAsync(link.Id, link.LegacyToken!, Protect(link.LegacyToken!)))
                protectedCount++;
        }
        return protectedCount;
    }
}
