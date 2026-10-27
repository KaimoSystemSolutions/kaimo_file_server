namespace Kaimo_File_Server.Core.Services.File;

public partial class FileVersionService
{
    // ------------------ Global versioning policy ------------------

    /// <summary>
    /// The policy rule that excludes this capture, or <c>null</c> when it must be versioned.
    /// Runs before any hashing, so skipped files (large media, lock files) cost nothing.
    /// </summary>
    private static VersionCaptureOutcome? SkipByPolicy(VersioningPolicy policy, string normalizedPath, Stream content)
    {
        if (!policy.Enabled)
            return VersionCaptureOutcome.Disabled;
        if (policy.IsExcluded(normalizedPath))
            return VersionCaptureOutcome.Excluded;
        if (policy.MaxFileSizeBytes > 0 && content.Length > policy.MaxFileSizeBytes)
            return VersionCaptureOutcome.TooLarge;
        return null;
    }

    /// <summary>
    /// Applies the policy's thinning, age and count rules to one file's history (see
    /// <see cref="VersionRetentionPlanner"/>) and reclaims the blobs no version needs any more.
    /// </summary>
    private async Task<int> ApplyPolicyRetentionAsync(Guid shareId, string normalizedPath, VersioningPolicy policy)
    {
        var versions = await _versionRepo.GetVersionsAsync(shareId, normalizedPath);
        var doomed = VersionRetentionPlanner.SelectForDeletion(
            versions, policy, _timeProvider.GetUtcNow().UtcDateTime);
        return await DeleteVersionsAsync(doomed.Select(v => v.Id).ToList());
    }

    /// <summary>
    /// Deletes version rows by id and reclaims their blobs. Used by the retention sweep,
    /// which plans deletions across many files at once.
    /// </summary>
    public async Task<int> DeleteVersionsAsync(IReadOnlyCollection<Guid> versionIds)
    {
        if (versionIds.Count == 0) return 0;
        var removed = await _versionRepo.DeleteByIdsAsync(versionIds);
        await DeleteUnreferencedBlobsAsync(removed);
        return removed.Count;
    }
}
