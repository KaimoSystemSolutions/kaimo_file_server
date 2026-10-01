using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Web.Controllers.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Security-relevant building blocks of the client search API: input validation,
/// response shaping (no server paths / full text / raw markup) and the filename
/// fallback's containment of untrusted folder prefixes.
/// </summary>
public sealed class SearchApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kaimo-search-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ─────────────────── query validation ───────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("   a   ")]
    [InlineData("ab\ncd")]
    [InlineData("ab\0cd")]
    public void Invalid_queries_are_rejected(string? raw)
    {
        Assert.False(SearchApiController.TryValidateQuery(raw, out _));
    }

    [Fact]
    public void Overlong_query_is_rejected()
    {
        Assert.False(SearchApiController.TryValidateQuery(new string('x', SearchApiController.MaxQueryLength + 1), out _));
    }

    [Fact]
    public void Valid_query_is_trimmed()
    {
        Assert.True(SearchApiController.TryValidateQuery("  report 2026  ", out var q));
        Assert.Equal("report 2026", q);
    }

    // ─────────────────── response shaping ───────────────────

    [Fact]
    public void Hit_dto_never_exposes_server_path_id_or_content()
    {
        var doc = new FileDocument
        {
            Id = "/srv/pool/secret-share/docs/a.txt",
            FileName = "a.txt",
            ShareName = "docs",
            AbsolutePath = "/srv/pool/secret-share/docs/a.txt",
            SharePath = "docs/a.txt",
            Content = "FULL INDEXED TEXT",
            FileType = "txt",
            HighlightSnippet = "x<mark>y</mark>z"
        };

        var json = JsonSerializer.Serialize(SearchHitDto.From(doc, Guid.NewGuid(), "docs"));

        Assert.DoesNotContain("/srv/pool", json);
        Assert.DoesNotContain("FULL INDEXED TEXT", json);
        Assert.DoesNotContain("<mark>", json);
    }

    [Fact]
    public void Snippet_is_returned_as_structured_segments()
    {
        var doc = new FileDocument
        {
            Id = "1", FileName = "a", ShareName = "s", AbsolutePath = "/a", SharePath = "a",
            FileType = "", HighlightSnippet = "a<mark>b</mark>c"
        };

        var segments = SearchHitDto.From(doc, Guid.NewGuid(), "s").Snippet;

        Assert.Equal(
            new[] { new SnippetSegmentDto("a", false), new SnippetSegmentDto("b", true), new SnippetSegmentDto("c", false) },
            segments);
    }

    // ─────────────────── filename fallback containment ───────────────────

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("sub/../../outside")]
    public async Task Filename_search_ignores_traversal_prefixes(string prefix)
    {
        var service = CreateFilenameSearch();

        var hits = await service.SearchAsync("secret", NewUser(), "share", prefix);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Filename_search_finds_matches_under_a_valid_prefix()
    {
        var service = CreateFilenameSearch();

        var hits = await service.SearchAsync("secret", NewUser(), "share", "sub");

        Assert.Equal("secret-inside.txt", Assert.Single(hits).FileName);
    }

    /// <summary>
    /// share/sub/secret-inside.txt is inside the share; outside/secret-outside.txt is a
    /// sibling of the share root that must never be reached. ACL allows everything, so
    /// only the path containment decides what is returned.
    /// </summary>
    private FilenameSearchService CreateFilenameSearch()
    {
        var sharePath = Path.Combine(_root, "share");
        Directory.CreateDirectory(Path.Combine(sharePath, "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "outside"));
        File.WriteAllText(Path.Combine(sharePath, "sub", "secret-inside.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "outside", "secret-outside.txt"), "x");

        var share = new ShareDefinition("share", sharePath, isEnabled: true);
        var shares = new Mock<IShareRepository>();
        shares.Setup(r => r.GetAllEnabledAsync()).ReturnsAsync(new List<ShareDefinition> { share });
        shares.Setup(r => r.GetByNameAsync("share")).ReturnsAsync(share);

        var acl = new Mock<IAclService>();
        acl.Setup(a => a.HasAccessBatchAsync(
                It.IsAny<UserContext>(), It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<(string, bool)>>(), It.IsAny<FilePermission>()))
            .ReturnsAsync((UserContext _, Guid _, IReadOnlyList<(string relativePath, bool)> items, FilePermission _) =>
                items.ToDictionary(i => i.relativePath, _ => true));

        var services = new ServiceCollection()
            .AddSingleton(shares.Object)
            .AddSingleton(acl.Object)
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();

        return new FilenameSearchService(
            scopes,
            new SearchAclFilter(scopes, NullLogger<SearchAclFilter>.Instance),
            NullLogger<FilenameSearchService>.Instance);
    }

    private static UserContext NewUser()
        => new(new User(Guid.NewGuid(), "Alice", "alice", "hash", "nt"), [], [], []);
}
