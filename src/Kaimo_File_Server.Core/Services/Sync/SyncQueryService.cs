using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Core.Services.Sync
{
    /// <summary>
    /// Default <see cref="ISyncQueryService"/>: a depth-first walk over
    /// <see cref="IFileService.ListAsync"/>, which already ACL-filters every
    /// directory. Directories the user cannot list are simply skipped, so the
    /// enumeration never reveals anything the caller may not see.
    /// </summary>
    public sealed class SyncQueryService : ISyncQueryService
    {
        // Defensive bound so a pathological tree cannot make one request walk
        // unbounded. The client can enumerate deeper subtrees separately.
        private const int MaxEntries = 200_000;

        private readonly IShareRepository _shares;
        private readonly IFileServiceFactory _fileServiceFactory;
        private readonly IFileChangeCursorRepository _changeCursors;
        private readonly IFileChangeLogRepository _changeLog;

        public SyncQueryService(
            IShareRepository shares,
            IFileServiceFactory fileServiceFactory,
            IFileChangeCursorRepository changeCursors,
            IFileChangeLogRepository changeLog)
        {
            _shares = shares;
            _fileServiceFactory = fileServiceFactory;
            _changeCursors = changeCursors;
            _changeLog = changeLog;
        }

        public async Task<SyncDelta> EnumerateAsync(
            Guid shareId,
            string rootRelativePath,
            UserContext user,
            CancellationToken ct = default)
        {
            var share = await _shares.GetByIdAsync(shareId)
                ?? throw new KeyNotFoundException($"Share '{shareId}' does not exist.");

            string root = ShareRelativePath.Normalize(rootRelativePath);
            var fileService = _fileServiceFactory.CreateForShare(share.Id, share.Path);

            // Capture the change token and the change-log head sequence BEFORE walking, so a change
            // that lands mid-walk is not missed: both are conservative, and the next
            // long-poll / changes?since request reports the change rather than silently skipping it.
            var stateBefore = await _changeCursors.GetShareChangeStateAsync(shareId, root, ct);
            var seqBefore = await _changeLog.GetHeadSeqAsync(shareId, root, ct);

            var entries = new List<SyncEntry>();
            await WalkAsync(fileService, root, user, entries, ct);

            return new SyncDelta(entries, stateBefore.ToToken(), seqBefore);
        }

        private static async Task WalkAsync(
            IFileService fileService,
            string directoryPath,
            UserContext user,
            List<SyncEntry> sink,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            List<FileMetadata> children;
            try
            {
                children = await fileService.ListAsync(directoryPath, user);
            }
            catch (UnauthorizedAccessException)
            {
                // The user may not list this directory — skip it silently.
                return;
            }

            foreach (var item in children)
            {
                if (sink.Count >= MaxEntries)
                    return;

                sink.Add(new SyncEntry(
                    item.Path, item.IsDirectory, item.Size, item.ModifiedAt));

                if (item.IsDirectory)
                    await WalkAsync(fileService, item.Path, user, sink, ct);
            }
        }
    }
}
