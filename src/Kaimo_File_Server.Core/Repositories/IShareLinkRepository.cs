using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Repositories;

/// <summary>
/// Persistence for anonymous public download links (<see cref="ShareLink"/>).
/// </summary>
public interface IShareLinkRepository
{
    Task<ShareLink> CreateAsync(ShareLink link);
    Task UpdateAsync(ShareLink link);
    Task DeleteAsync(Guid id);
    Task<ShareLink?> GetByIdAsync(Guid id);
    Task<ShareLink?> GetByTokenAsync(string token);

    /// <summary>All links whose target share is in <paramref name="shareIds"/>, newest first.</summary>
    Task<List<ShareLink>> ListForSharesAsync(IEnumerable<Guid> shareIds);

    /// <summary>Every link, newest first (for unrestricted/global admins).</summary>
    Task<List<ShareLink>> ListAllAsync();

    /// <summary>
    /// Atomically records one access against the link identified by <paramref name="token"/>,
    /// but only while it is enabled, within its time window, and below its access-count cap.
    /// Returns the (post-increment) link when access was granted, or null when the link is
    /// missing, inactive, or exhausted. Race-free under concurrent downloads.
    /// </summary>
    Task<ShareLink?> TryConsumeAccessAsync(string token);
}
