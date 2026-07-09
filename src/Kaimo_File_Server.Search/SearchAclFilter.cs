using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kaimo_File_Server.Search;

/// <summary>
/// SECURITY: turns a list of raw search hits into the subset the user is actually
/// allowed to read (<see cref="FilePermission.ListReadData"/>). Shared by every
/// search backend (Elasticsearch, filename fallback) so the ACL rule is enforced
/// in exactly one place and can never be bypassed by adding a new backend.
///
/// Hits are grouped per share and evaluated with a single batched ACL query per
/// share. Anything whose share cannot be resolved (or is disabled) is denied —
/// fail closed.
/// </summary>
public sealed class SearchAclFilter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SearchAclFilter> _logger;

    public SearchAclFilter(IServiceScopeFactory scopeFactory, ILogger<SearchAclFilter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<List<FileDocument>> FilterAsync(
        List<FileDocument> docs, UserContext user, CancellationToken ct = default)
    {
        // Fail closed: no user context means no results, ever.
        if (user is null || docs.Count == 0)
            return new List<FileDocument>();

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var shareRepo = sp.GetRequiredService<IShareRepository>();
        var acl = sp.GetRequiredService<IAclService>();

        var allowed = new List<FileDocument>(docs.Count);

        foreach (var group in docs.GroupBy(d => d.ShareName, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var share = await shareRepo.GetByNameAsync(group.Key);
            if (share is null || !share.IsEnabled)
            {
                // Fail closed: can't verify the share → don't reveal its files.
                _logger.LogDebug(
                    "Suchtreffer für nicht auflösbaren/deaktivierten Share '{Share}' verworfen", group.Key);
                continue;
            }

            var groupDocs = group.ToList();

            var items = groupDocs
                .Select(d => (ShareRelativePath.Normalize(d.SharePath), d.IsDirectory))
                .Distinct()
                .ToList();

            var accessMap = await acl.HasAccessBatchAsync(
                user, share.Id, items, FilePermission.ListReadData);

            foreach (var d in groupDocs)
            {
                var norm = ShareRelativePath.Normalize(d.SharePath);
                if (accessMap.TryGetValue(norm, out var ok) && ok)
                    allowed.Add(d);
            }
        }

        return allowed;
    }
}
