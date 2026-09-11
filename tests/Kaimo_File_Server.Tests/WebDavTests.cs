using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Kaimo_File_Server.Web.Controllers.WebDav;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pure-unit tests for the WebDAV transport's database-free building blocks:
/// URL/Destination mapping, If-header parsing, the lock table, and the PROPFIND
/// XML shape. Mirrors the style of <see cref="ClientSyncApiTests"/> — no database,
/// no HTTP pipeline.
/// </summary>
public sealed class WebDavTests
{
    // ─────────────────── URL → (share, path) mapping ───────────────────

    [Theory]
    [InlineData("/dav", "", "")]
    [InlineData("/dav/", "", "")]
    [InlineData("/dav/Projekte", "Projekte", "")]
    [InlineData("/dav/Projekte/", "Projekte", "")]
    [InlineData("/dav/Projekte/sub/file.txt", "Projekte", "sub/file.txt")]
    [InlineData("/dav/Projekte/sub/", "Projekte", "sub")]
    public void TrySplit_maps_share_and_path(string requestPath, string expectedShare, string expectedRel)
    {
        Assert.True(WebDavPathResolver.TrySplit(requestPath, out var share, out var rel));
        Assert.Equal(expectedShare, share);
        Assert.Equal(expectedRel, rel);
    }

    [Theory]
    [InlineData("/dav/Projekte/../secret")]   // traversal never reaches storage
    [InlineData("/dav/Projekte/a/../../b")]
    public void TrySplit_rejects_traversal(string requestPath)
    {
        Assert.False(WebDavPathResolver.TrySplit(requestPath, out _, out _));
    }

    [Fact]
    public void TrySplit_allows_recycle_bin_path()
    {
        Assert.True(WebDavPathResolver.TrySplit("/dav/Projekte/.RECYCLE_BIN/old.txt", out var share, out var rel));
        Assert.Equal("Projekte", share);
        Assert.Equal(".RECYCLE_BIN/old.txt", rel);
    }

    // ─────────────────── Destination header parsing ───────────────────

    [Fact]
    public void Destination_absolute_same_host_is_ok()
    {
        var outcome = WebDavPathResolver.TryResolveDestination(
            "https://host:8443/dav/Projekte/sub/new%20name.txt", "host:8443",
            out var share, out var rel);

        Assert.Equal(WebDavPathResolver.DestinationOutcome.Ok, outcome);
        Assert.Equal("Projekte", share);
        Assert.Equal("sub/new name.txt", rel); // percent-decoded
    }

    [Fact]
    public void Destination_origin_relative_is_ok()
    {
        var outcome = WebDavPathResolver.TryResolveDestination(
            "/dav/Projekte/dest.txt", "host:8443", out var share, out var rel);

        Assert.Equal(WebDavPathResolver.DestinationOutcome.Ok, outcome);
        Assert.Equal("Projekte", share);
        Assert.Equal("dest.txt", rel);
    }

    [Fact]
    public void Destination_foreign_host_is_flagged()
    {
        var outcome = WebDavPathResolver.TryResolveDestination(
            "https://evil.example/dav/Projekte/x.txt", "host:8443", out _, out _);

        Assert.Equal(WebDavPathResolver.DestinationOutcome.ForeignHost, outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void Destination_missing_or_garbage_is_invalid(string? header)
    {
        Assert.Equal(
            WebDavPathResolver.DestinationOutcome.Invalid,
            WebDavPathResolver.TryResolveDestination(header, "host:8443", out _, out _));
    }

    // ─────────────────── If: header parsing ───────────────────

    [Fact]
    public void IfHeader_untagged_list_extracts_token()
    {
        var tokens = WebDavIfHeader.ParseLockTokens("(<opaquelocktoken:abc-123>)");
        Assert.Equal(["opaquelocktoken:abc-123"], tokens);
    }

    [Fact]
    public void IfHeader_tagged_list_extracts_token_not_resource_uri()
    {
        var tokens = WebDavIfHeader.ParseLockTokens(
            "<https://host/dav/Projekte/file.txt> (<opaquelocktoken:xyz>)");
        Assert.Equal(["opaquelocktoken:xyz"], tokens);
    }

    [Fact]
    public void IfHeader_ignores_negated_token_and_etag()
    {
        var tokens = WebDavIfHeader.ParseLockTokens("(Not <opaquelocktoken:no> [\"etag\"])");
        Assert.Empty(tokens);
    }

    [Fact]
    public void IfHeader_empty_is_empty()
    {
        Assert.Empty(WebDavIfHeader.ParseLockTokens(""));
        Assert.Empty(WebDavIfHeader.ParseLockTokens(null));
    }

    // ─────────────────── Lock manager ───────────────────

    private static WebDavLockManager NewLockManager(out FakeClock clock)
    {
        clock = new FakeClock(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        return new WebDavLockManager(clock);
    }

    [Fact]
    public void Lock_acquire_then_conflict_on_same_path()
    {
        var mgr = NewLockManager(out _);
        var share = Guid.NewGuid();

        var first = mgr.TryAcquire(share, "a/b.txt", depthInfinity: false, ownerRaw: null, timeoutSeconds: 300);
        Assert.NotNull(first);

        var second = mgr.TryAcquire(share, "a/b.txt", depthInfinity: false, ownerRaw: null, timeoutSeconds: 300);
        Assert.Null(second); // exclusive — a second holder is refused
    }

    [Fact]
    public void Infinity_lock_on_ancestor_blocks_descendant()
    {
        var mgr = NewLockManager(out _);
        var share = Guid.NewGuid();

        Assert.NotNull(mgr.TryAcquire(share, "a", depthInfinity: true, null, 300));
        Assert.Null(mgr.TryAcquire(share, "a/b/c.txt", depthInfinity: false, null, 300));
    }

    [Fact]
    public void Blocked_is_false_when_caller_presents_the_token()
    {
        var mgr = NewLockManager(out _);
        var share = Guid.NewGuid();
        var info = mgr.TryAcquire(share, "a/b.txt", false, null, 300)!;

        Assert.True(mgr.IsBlocked(share, "a/b.txt", []));
        Assert.False(mgr.IsBlocked(share, "a/b.txt", [info.Token]));
    }

    [Fact]
    public void Lock_refresh_requires_matching_token_and_extends_expiry()
    {
        var mgr = NewLockManager(out var clock);
        var share = Guid.NewGuid();
        var info = mgr.TryAcquire(share, "a.txt", false, null, 300)!;

        clock.Now = clock.Now.AddSeconds(200);
        Assert.Null(mgr.Refresh(share, "a.txt", "wrong-token", 300));

        var refreshed = mgr.Refresh(share, "a.txt", info.Token, 300);
        Assert.NotNull(refreshed);
        Assert.Equal(300, mgr.RemainingSeconds(refreshed!));
    }

    [Fact]
    public void Lock_expires_and_frees_the_path()
    {
        var mgr = NewLockManager(out var clock);
        var share = Guid.NewGuid();
        Assert.NotNull(mgr.TryAcquire(share, "a.txt", false, null, 300));

        clock.Now = clock.Now.AddSeconds(301);

        // The expired lock no longer blocks, and the path can be re-acquired.
        Assert.False(mgr.IsBlocked(share, "a.txt", []));
        Assert.NotNull(mgr.TryAcquire(share, "a.txt", false, null, 300));
    }

    [Fact]
    public void Unlock_with_wrong_token_fails()
    {
        var mgr = NewLockManager(out _);
        var share = Guid.NewGuid();
        var info = mgr.TryAcquire(share, "a.txt", false, null, 300)!;

        Assert.False(mgr.Unlock(share, "a.txt", "not-the-token"));
        Assert.True(mgr.Unlock(share, "a.txt", info.Token));
    }

    // ─────────────────── PROPFIND XML shape ───────────────────

    private static readonly XNamespace Dav = "DAV:";

    private static WebDavEntry File(string href) =>
        new(href, "file.txt", IsCollection: false, 42,
            new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
            "\"42:100\"", []);

    private static WebDavEntry Collection(string href) =>
        new(href, "sub", IsCollection: true, 0,
            DateTime.UtcNow, DateTime.UtcNow, null, []);

    [Fact]
    public void PropFind_file_reports_length_type_and_etag()
    {
        var xml = WebDavPropFind.WriteMultiStatus(
            [File("/dav/Projekte/file.txt")],
            new WebDavPropFind.PropRequest(WebDavPropFind.PropMode.AllProp, []));

        var doc = XDocument.Parse(xml);
        var okProps = OkProps(doc, "/dav/Projekte/file.txt");

        Assert.Contains(Dav + "getcontentlength", okProps);
        Assert.Contains(Dav + "getcontenttype", okProps);
        Assert.Contains(Dav + "getetag", okProps);
        // A file's resourcetype element is empty (no <collection/>).
        var resourceType = FindProp(doc, "/dav/Projekte/file.txt", Dav + "resourcetype");
        Assert.False(resourceType!.HasElements);
    }

    [Fact]
    public void PropFind_collection_marks_resourcetype_and_omits_file_only_props()
    {
        var xml = WebDavPropFind.WriteMultiStatus(
            [Collection("/dav/Projekte/sub/")],
            new WebDavPropFind.PropRequest(WebDavPropFind.PropMode.AllProp, []));

        var doc = XDocument.Parse(xml);
        var resourceType = FindProp(doc, "/dav/Projekte/sub/", Dav + "resourcetype");
        Assert.NotNull(resourceType!.Element(Dav + "collection"));

        // getcontentlength/type/etag are file-only — absent from the 200 propstat for a collection.
        var okProps = OkProps(doc, "/dav/Projekte/sub/");
        Assert.DoesNotContain(Dav + "getcontentlength", okProps);
        Assert.DoesNotContain(Dav + "getetag", okProps);
    }

    [Fact]
    public void PropFind_mixed_depth1_lists_container_and_children()
    {
        var xml = WebDavPropFind.WriteMultiStatus(
            [Collection("/dav/Projekte/"), File("/dav/Projekte/file.txt"), Collection("/dav/Projekte/sub/")],
            new WebDavPropFind.PropRequest(WebDavPropFind.PropMode.AllProp, []));

        var hrefs = XDocument.Parse(xml)
            .Descendants(Dav + "response")
            .Select(r => r.Element(Dav + "href")!.Value)
            .ToList();

        Assert.Equal(3, hrefs.Count);
        Assert.Contains("/dav/Projekte/file.txt", hrefs);
    }

    [Fact]
    public void PropFind_unknown_named_property_is_reported_404()
    {
        var unknown = XName.Get("customprop", "http://example.com/ns");
        var xml = WebDavPropFind.WriteMultiStatus(
            [File("/dav/Projekte/file.txt")],
            new WebDavPropFind.PropRequest(WebDavPropFind.PropMode.Named, [Dav + "displayname", unknown]));

        var doc = XDocument.Parse(xml);
        Assert.Contains(Dav + "displayname", OkProps(doc, "/dav/Projekte/file.txt"));

        var notFound = doc.Descendants(Dav + "propstat")
            .First(ps => ps.Element(Dav + "status")!.Value.Contains("404"));
        Assert.NotNull(notFound.Element(Dav + "prop")!.Element(unknown));
    }

    // ── helpers ──

    private static XElement? FindProp(XDocument doc, string href, XName prop)
        => doc.Descendants(Dav + "response")
              .First(r => r.Element(Dav + "href")!.Value == href)
              .Descendants(Dav + "prop").Elements()
              .FirstOrDefault(e => e.Name == prop);

    private static List<XName> OkProps(XDocument doc, string href)
        => doc.Descendants(Dav + "response")
              .First(r => r.Element(Dav + "href")!.Value == href)
              .Elements(Dav + "propstat")
              .Where(ps => ps.Element(Dav + "status")!.Value.Contains("200"))
              .SelectMany(ps => ps.Element(Dav + "prop")!.Elements())
              .Select(e => e.Name)
              .ToList();

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now;
        public FakeClock(DateTimeOffset start) => Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
