using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>
/// Search transport for client apps. A thin, hardened layer over the ACL-checked
/// <see cref="ISearchService"/> (Elasticsearch with filename fallback): every hit has
/// already passed <c>SearchAclFilter</c> (ListReadData, fail-closed) before it reaches
/// this controller. Input is validated at this trust boundary, requests are
/// rate-limited per user and time-boxed, and responses never expose server paths
/// or indexed full text.
///
/// The backends build <c>match</c> queries only, so search text cannot inject
/// Elasticsearch query DSL. Never switch them to <c>query_string</c>/<c>simple_query_string</c>.
/// </summary>
[Authorize]
[Route("api/v1/search")]
[EnableRateLimiting(RateLimitPolicy)]
public sealed class SearchApiController : ApiControllerBase
{
    public const string RateLimitPolicy = "api-search";

    internal const int MinQueryLength = 2;
    internal const int MaxQueryLength = 100;
    internal const int DefaultLimit = 25;
    // The backends collect at least this many readable hits before they stop paging.
    internal const int MaxLimit = SearchLimits.MinVisibleHits;

    // Upper bound for one search, so a slow filename walk cannot pin a request.
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);

    private readonly IUserContextFactory _userContextFactory;
    private readonly IShareRepository _shares;
    private readonly ISearchService _search;

    public SearchApiController(
        IUserContextFactory userContextFactory,
        IShareRepository shares,
        ISearchService search)
    {
        _userContextFactory = userContextFactory;
        _shares = shares;
        _search = search;
    }

    /// <summary>Searches file names and content across the shares the caller may read.</summary>
    [HttpPost]
    public async Task<IActionResult> Search([FromBody] SearchRequestDto? request)
    {
        var user = await ResolveUserAsync(_userContextFactory);
        if (user is null) return ApiUnauthorized();

        if (!TryValidateQuery(request?.Q, out var query))
            return ApiBadRequest("invalid_query",
                $"'q' must be {MinQueryLength}-{MaxQueryLength} characters without control characters.");

        // Personal, ACL-filtered results: never cache in shared proxies.
        Response.Headers[HeaderNames.CacheControl] = "no-store";

        var enabledShares = await _shares.GetAllEnabledAsync();

        string? shareName = null;
        string? pathPrefix = null;
        if (request!.ShareId is { } shareId)
        {
            var share = enabledShares.FirstOrDefault(s => s.Id == shareId);
            // An unknown or disabled share answers like a share the caller cannot read
            // (no hits), so share ids cannot be probed for existence.
            if (share is null) return Ok(Array.Empty<SearchHitDto>());
            shareName = share.Name;

            if (!ShareRelativePath.TryNormalizeStrict(request.Path ?? string.Empty, out var normalized))
                return ApiBadRequest("invalid_path", "The path is not a valid share-relative path.");
            pathPrefix = normalized;
        }
        else if (!string.IsNullOrEmpty(request.Path))
        {
            return ApiBadRequest("invalid_path", "'path' requires 'shareId'.");
        }

        int limit = Math.Clamp(request.Limit ?? DefaultLimit, 1, MaxLimit);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        cts.CancelAfter(SearchTimeout);

        List<FileDocument> hits;
        try
        {
            hits = await _search.SearchAsync(query, user, shareName, pathPrefix, cts.Token);
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ApiError("search_timeout", "The search took too long. Narrow it down and try again."));
        }

        // Fail closed: a hit whose share no longer resolves to an enabled share is dropped.
        var sharesByName = enabledShares
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var dtos = new List<SearchHitDto>(limit);
        foreach (var hit in hits)
        {
            if (!sharesByName.TryGetValue(hit.ShareName, out var share))
                continue;

            // Homes are presented as the caller's "user" entry, as in api/v1/browse/shares.
            var displayName = share.IsUserHomes ? HomeFileBrowserViewModel.DisplayName : share.Name;
            dtos.Add(SearchHitDto.From(hit, share.Id, displayName));
            if (dtos.Count == limit)
                break;
        }

        return Ok(dtos);
    }

    /// <summary>
    /// Trims and validates untrusted search text: length within bounds and no control
    /// characters (which also keeps log/ES payloads free of injected line breaks).
    /// </summary>
    internal static bool TryValidateQuery(string? raw, out string query)
    {
        query = raw?.Trim() ?? string.Empty;
        return query.Length is >= MinQueryLength and <= MaxQueryLength
               && !query.Any(char.IsControl);
    }
}
