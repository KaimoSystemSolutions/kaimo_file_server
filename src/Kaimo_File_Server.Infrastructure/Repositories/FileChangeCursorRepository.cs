using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Services.Sync;
using Kaimo_File_Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IFileChangeCursorRepository"/>. Reads the
/// change fingerprint from the persisted <c>file_metadata</c> projection with a
/// pair of cheap aggregates keyed by the indexed <c>(ShareId, Path)</c> columns.
/// </summary>
public sealed class FileChangeCursorRepository : IFileChangeCursorRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public FileChangeCursorRepository(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<ShareChangeState> GetShareChangeStateAsync(
        Guid shareId, string? pathPrefix, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var query = db.FileMetadata.AsNoTracking().Where(f => f.ShareId == shareId);

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            // The subtree root itself plus everything strictly beneath it. The
            // "/" guard prevents a sibling like "docs2" matching prefix "docs".
            string childPrefix = pathPrefix + "/";
            query = query.Where(f => f.Path == pathPrefix || f.Path.StartsWith(childPrefix));
        }

        long count = await query.LongCountAsync(ct);
        if (count == 0)
            return ShareChangeState.Empty;

        DateTime? max = await query.MaxAsync(f => (DateTime?)f.ModifiedAt, ct);
        return new ShareChangeState(max, count);
    }
}
