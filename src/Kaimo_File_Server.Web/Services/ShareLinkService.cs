using System.Security.Cryptography;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kaimo_File_Server.Web.Services;

/// <summary>Fields needed to mint a new public share link.</summary>
public sealed record CreateShareLinkRequest(
    Guid ShareId,
    string RootRelativePath,
    bool IsDirectory,
    string DisplayName,
    string? Password,
    string? BaseAddress,
    DateTime? StartsAtUtc,
    DateTime? ExpiresAtUtc,
    int? MaxAccessCount,
    long? MaxBytesPerSecond,
    // A token reserved up-front so the dialog can show the final URL before the link is
    // persisted. Falls back to a freshly generated one when empty.
    string? Token = null);

/// <summary>
/// Circuit-side coordination for public share links: settings, URL building, creation
/// (with a server-side permission re-check), password verification with brute-force
/// throttling, and issuing the one-time ticket the anonymous download endpoint consumes.
/// </summary>
public sealed class ShareLinkService(
    IShareLinkRepository repository,
    IConfigRepository config,
    IPasswordService passwords,
    ILoginThrottle throttle,
    IManagementAuthService mgmtAuth,
    IUserContextFactory userContexts,
    AuthenticationStateProvider authState,
    PublicDownloadTicketStore tickets)
{
    private static readonly LoginThrottlePolicy PasswordPolicy = new(10, TimeSpan.FromMinutes(15));

    public async Task<ShareLinkSettings> GetSettingsAsync()
    {
        var settings = await config.GetAsync(ShareLinkSettings.ConfigKey, ShareLinkSettings.Default());
        settings.Normalize();
        return settings;
    }

    public async Task SaveSettingsAsync(ShareLinkSettings settings)
    {
        settings.Normalize();
        await config.SetAsync(ShareLinkSettings.ConfigKey, settings);
    }

    /// <summary>Generates a fresh, URL-safe token (not persisted).</summary>
    public string GenerateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Builds the shareable URL for a link, honouring the configured base addresses.</summary>
    public string BuildUrl(ShareLink link, ShareLinkSettings settings)
        => BuildUrl(link.Token, link.BaseAddress, settings);

    /// <summary>Builds a shareable URL from a token + chosen base address (for a live preview).</summary>
    public string BuildUrl(string token, string? chosenBase, ShareLinkSettings settings)
    {
        var baseAddress = settings.ResolveBaseFor(chosenBase);
        return string.IsNullOrEmpty(baseAddress)
            ? $"/shared/{token}"
            : $"{baseAddress}/shared/{token}";
    }

    public Task<ShareLink?> GetByTokenAsync(string token) => repository.GetByTokenAsync(token);

    /// <summary>Sets or clears a link's password in place (does not persist). Empty = clear.</summary>
    public void ApplyPassword(ShareLink link, string? newPassword)
        => link.PasswordHash = string.IsNullOrEmpty(newPassword) ? null : passwords.HashPassword(newPassword);

    /// <summary>
    /// Creates a link after re-checking that the signed-in user may manage share links on the
    /// target share. Returns null when there is no actor or the permission check fails.
    /// </summary>
    public async Task<ShareLink?> CreateAsync(CreateShareLinkRequest request)
    {
        var actor = await ResolveActorAsync();
        if (actor is null) return null;

        var allowed = await mgmtAuth.CanManageShareAsync(
            actor, request.ShareId, ManagementPermission.ManageShareLinks);
        if (!allowed) return null;

        var link = new ShareLink
        {
            Token = string.IsNullOrEmpty(request.Token)
                ? await GenerateUniqueTokenAsync()
                : request.Token,
            ShareId = request.ShareId,
            RootRelativePath = request.RootRelativePath,
            IsDirectory = request.IsDirectory,
            DisplayName = request.DisplayName,
            CreatedByUserId = actor.User.Id,
            PasswordHash = string.IsNullOrEmpty(request.Password)
                ? null
                : passwords.HashPassword(request.Password),
            BaseAddress = request.BaseAddress,
            StartsAtUtc = request.StartsAtUtc,
            ExpiresAtUtc = request.ExpiresAtUtc,
            MaxAccessCount = request.MaxAccessCount is > 0 ? request.MaxAccessCount : null,
            MaxBytesPerSecond = request.MaxBytesPerSecond is > 0 ? request.MaxBytesPerSecond : null,
        };

        return await repository.CreateAsync(link);
    }

    /// <summary>
    /// Verifies a candidate password against the link, rate-limiting wrong guesses per token.
    /// Returns whether the guess was correct and, when locked out, how long to wait.
    /// </summary>
    public (bool Ok, TimeSpan RetryAfter) VerifyPassword(ShareLink link, string? candidate)
    {
        if (link.PasswordHash is null) return (true, TimeSpan.Zero);

        var key = "sharelink:" + link.Token;
        // Reserve the attempt before the (slow) BCrypt verify so parallel guesses
        // cannot all slip past the lockout check.
        var status = throttle.BeginAttempt(key, PasswordPolicy);
        if (status.IsLockedOut) return (false, status.RetryAfter);

        if (!string.IsNullOrEmpty(candidate) && passwords.VerifyPassword(candidate, link.PasswordHash))
        {
            throttle.Reset(key);
            return (true, TimeSpan.Zero);
        }

        var failure = throttle.RegisterFailure(key, PasswordPolicy);
        return (false, failure.RetryAfter);
    }

    /// <summary>
    /// Issues a one-time public download ticket for the given share-relative paths. The caller
    /// must already have validated the link's window and (if set) password on the circuit.
    /// </summary>
    public string IssueTicket(ShareLink link, IReadOnlyList<string> relativePaths, bool zip, string downloadName)
        => tickets.Issue(new PublicDownloadTicket(link.Token, relativePaths.ToArray(), downloadName, zip));

    /// <summary>
    /// The identity anonymous access runs under, or null when the creator no longer
    /// exists or was disabled — a disabled account must not keep serving files.
    /// </summary>
    public async Task<UserContext?> ResolveCreatorAsync(ShareLink link)
    {
        var creator = await userContexts.CreateByUserIdAsync(link.CreatedByUserId);
        return creator is { User.IsEnabled: true } ? creator : null;
    }

    private async Task<UserContext?> ResolveActorAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return await userContexts.CreateByUsernameAsync(username);
    }

    private async Task<string> GenerateUniqueTokenAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            if (await repository.GetByTokenAsync(token) is null)
                return token;
        }
        // Astronomically unlikely; widen the token rather than loop forever.
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }
}
