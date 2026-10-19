using System.Collections.Immutable;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Search;

public partial class ElasticSearchService
{
    // ══════════════════════════════════════════
    //  Search — ACL is mandatory before any result is returned
    // ══════════════════════════════════════════

    public async Task<List<FileDocument>> SearchAsync(
        string searchText, UserContext user,
        string? shareName = null, string? pathPrefix = null,
        CancellationToken ct = default, bool includeRecycleBin = false)
    {
        // Fail closed: no user context means no results, ever.
        if (user is null)
            return new List<FileDocument>();

        // Page through the ranked hits until enough readable ones are collected, so
        // matches the user may not read cannot crowd out the ones they may.
        var allowed = new List<FileDocument>();
        for (int from = 0; from < SearchLimits.MaxRawScan; from += RawFetchSize)
        {
            var raw = await RawSearchAsync(searchText, shareName, pathPrefix, includeRecycleBin, from, ct);

            // SECURITY: every hit must pass an ACL check before it is exposed.
            if (raw.Count > 0)
                allowed.AddRange(await _aclFilter.FilterAsync(raw, user, ct));

            if (raw.Count < RawFetchSize || allowed.Count >= SearchLimits.MinVisibleHits)
                break;
        }

        return allowed;
    }

    /// <summary>
    /// Executes the raw query. Matches partial words in file names via the n-gram
    /// field and fuzzy whole words in the content. NEVER call this from the UI —
    /// the result is unfiltered and must pass <see cref="FilterByAclAsync"/> first.
    ///
    /// When <paramref name="shareName"/> / <paramref name="pathPrefix"/> are set, the
    /// query is filtered to that share (and folder subtree) via keyword filters.
    /// </summary>
    internal async Task<List<FileDocument>> RawSearchAsync(
        string searchText, string? shareName, string? pathPrefix, bool includeRecycleBin,
        int from, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return new List<FileDocument>();

        // Names and content are indexed in NFC; a query typed on macOS may arrive decomposed.
        searchText = searchText.Normalize();

        var scopeFilters = BuildScopeFilters(shareName, pathPrefix);
        // Recycle-bin documents (any ".RECYCLE_BIN" segment; only the share's/home's own bin
        // is ever indexed) are left out unless requested.
        // ponytail: regexp on the keyword path; add an indexed "inRecycleBin" flag if it gets slow.
        var excludeFilters = includeRecycleBin
            ? new List<Query>()
            : new List<Query>
            {
                new RegexpQuery
                {
                    Field = "sharePath",
                    Value = "(.*/)?[.]" + ShareEntryPolicy.RecycleBinName[1..] + "(/.*)?",
                    CaseInsensitive = true
                }
            };

        var response = await _client.SearchAsync<FileDocument>(s => s
            .Indices(_indexName)
            .From(from)
            .Size(RawFetchSize)
            .Query(q => q
                .Bool(b =>
                {
                    b.Should(
                        // Partial-word matches on the file name (n-gram analyzed).
                        sh => sh.Match(m => m
                            .Field("fileName")
                            .Query(searchText)
                            .Boost(4)
                        ),
                        // Whole words of the file name: single CJK characters are below the
                        // n-gram minimum and only match here.
                        sh => sh.Match(m => m
                            .Field("fileName.std")
                            .Query(searchText)
                            .Boost(4)
                        ),
                        // Substring matches inside the file content (n-gram analyzed).
                        sh => sh.Match(m => m
                            .Field("content")
                            .Query(searchText)
                        ),
                        // Whole-word / fuzzy matches on content for better ranking.
                        sh => sh.Match(m => m
                            .Field("content.std")
                            .Query(searchText)
                            // AUTO:5,8 — no fuzziness below 5 chars. Plain "AUTO"
                            // (=AUTO:3,6) allows 1 edit already at 3 chars, which on
                            // a 3-letter word matches most of the vocabulary and
                            // produces hits with no visible relation to the query.
                            .Fuzziness(new Fuzziness("AUTO:5,8"))
                            .Boost(2)
                        ),
                        // Name tier: a flat bonus far above any BM25 content score, so every
                        // file-name match ranks above every content-only match; the scored
                        // clauses above still order the hits within each tier.
                        sh => sh.ConstantScore(c => c
                            .Filter(f => f.Bool(nb => nb.Should(
                                n => n.Match(m => m.Field("fileName").Query(searchText)),
                                n => n.Match(m => m.Field("fileName.std").Query(searchText)))))
                            .Boost(NameTierBoost)
                        )
                    )
                    .MinimumShouldMatch(1);

                    // Scope to a share / folder subtree without affecting relevance.
                    if (scopeFilters.Count > 0)
                        b.Filter(scopeFilters);
                    if (excludeFilters.Count > 0)
                        b.MustNot(excludeFilters);
                })
            )
            // Folders first, then by relevance.
            .Sort(
                so => so.Field("isDirectory", f => f.Order(SortOrder.Desc)),
                so => so.Score(sc => sc.Order(SortOrder.Desc)))
            // Matches are marked with private-use characters that indexed text never holds, so a
            // literal "<mark>" in a file cannot pose as a highlight (see SearchDocuments.ToMarkup).
            .Highlight(h => h
                .PreTags(ImmutableList.Create(SearchDocuments.HighlightOpen.ToString()))
                .PostTags(ImmutableList.Create(SearchDocuments.HighlightClose.ToString()))
                .Fields(f => f
                    .Add("content", hf => hf.NumberOfFragments(1).FragmentSize(100))
                    .Add("content.std", hf => hf.NumberOfFragments(1).FragmentSize(100))
                    .Add("fileName", hf => hf.NumberOfFragments(1))
                    .Add("fileName.std", hf => hf.NumberOfFragments(1))
                )
            ), ct);

        if (!response.IsValidResponse)
        {
            // The transport may report a cancelled request as an invalid response;
            // surface it as a cancellation so callers can tell a timeout from "no hits".
            ct.ThrowIfCancellationRequested();
            _logger.LogError("Content search failed: {Error}", response.DebugInformation);
            return new List<FileDocument>();
        }

        var results = new List<FileDocument>();
        foreach (var hit in response.Hits)
        {
            var doc = hit.Source!;
            // The file name when it matched, otherwise the content excerpt.
            string? fragment = null;
            foreach (var field in (ReadOnlySpan<string>)["fileName", "fileName.std", "content", "content.std"])
            {
                if (hit.Highlight?.TryGetValue(field, out var fragments) == true && fragments.FirstOrDefault() is { } first)
                {
                    fragment = first;
                    break;
                }
            }
            doc.HighlightSnippet = SearchDocuments.ToMarkup(fragment);
            results.Add(doc);
        }

        return results;
    }

    /// <summary>
    /// Builds the keyword filters that scope a search to one share and, optionally,
    /// one folder subtree. <c>shareName</c>/<c>sharePath</c> are indexed as keyword
    /// fields, so an exact term and a prefix match cleanly. The folder clause keeps
    /// both the folder itself (exact) and everything below it (prefix + separator),
    /// so a sibling like "reports2" is not swept in by "reports".
    /// </summary>
    private static List<Query> BuildScopeFilters(string? shareName, string? pathPrefix)
    {
        var filters = new List<Query>();
        if (string.IsNullOrWhiteSpace(shareName))
            return filters;

        filters.Add(new TermQuery { Field = "shareName", Value = shareName });

        var prefix = (pathPrefix ?? string.Empty).Replace('\\', '/').Trim('/');
        if (prefix.Length > 0)
        {
            filters.Add(new BoolQuery
            {
                Should = new List<Query>
                {
                    new TermQuery { Field = "sharePath", Value = prefix },
                    new PrefixQuery { Field = "sharePath", Value = prefix + "/" }
                },
                MinimumShouldMatch = 1
            });
        }

        return filters;
    }
}
