using System.Net;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.ExternalStorage;

/// <summary>
/// Creation times over WebDAV: <c>Win32CreationTime</c> (else a trustworthy <c>creationdate</c>) on
/// listing, <c>Win32CreationTime</c> on push.
/// </summary>
public sealed partial class WebDavStorageProviderTests
{
    private static readonly DateTime PushModified = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PushCreated = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static string MultiStatus(string props) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/dav/a.txt</d:href>
            <d:propstat>
              <d:status>HTTP/1.1 200 OK</d:status>
              <d:prop><d:resourcetype/>{props}</d:prop>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public void ParseMultiStatusResponse_ReadsTheCreationDate()
    {
        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/dav");

        var item = Assert.Single(store.ParseMultiStatusResponse(MultiStatus("""
            <d:creationdate>2024-01-02T03:04:05Z</d:creationdate>
            <d:getlastmodified>Mon, 03 Aug 2026 12:00:00 GMT</d:getlastmodified>
            """), "/"));

        Assert.Equal(PushCreated, item.CreatedAtUtc);
    }

    [Fact]
    public void ParseMultiStatusResponse_IgnoresACreationDateAfterTheModifiedTime()
    {
        // Apache mod_dav reports the inode change time as creationdate, which every metadata
        // change (including our own time stamp) moves to "now"; it is no creation time.
        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/dav");

        var item = Assert.Single(store.ParseMultiStatusResponse(MultiStatus("""
            <d:creationdate>2026-09-01T00:00:00Z</d:creationdate>
            <d:getlastmodified>Mon, 03 Aug 2026 12:00:00 GMT</d:getlastmodified>
            """), "/"));

        Assert.Null(item.CreatedAtUtc);
    }

    [Fact]
    public void ParseMultiStatusResponse_IgnoresACreationDateEqualToTheModifiedTime()
    {
        // Apache's ctime right after a plain remote edit: ctime == mtime == the edit time. Taken as
        // creation time, a pull overwrite would replace the correct local creation time with it.
        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/dav");

        var item = Assert.Single(store.ParseMultiStatusResponse(MultiStatus("""
            <d:creationdate>2026-08-03T12:00:00Z</d:creationdate>
            <d:getlastmodified>Mon, 03 Aug 2026 12:00:00 GMT</d:getlastmodified>
            """), "/"));

        Assert.Null(item.CreatedAtUtc);
    }

    [Fact]
    public void ParseMultiStatusResponse_TrustsWin32CreationTime_EvenAfterTheModifiedTime()
    {
        // A file copied on Windows keeps its old mtime and gets the copy time as creation time.
        // Win32CreationTime is set explicitly (Explorer, Kaimo's push, IIS, Kaimo's own server),
        // so unlike creationdate it never stands for an inode change time.
        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/dav");

        var item = Assert.Single(store.ParseMultiStatusResponse(MultiStatus("""
            <z:Win32CreationTime xmlns:z="urn:schemas-microsoft-com:">Tue, 01 Sep 2026 00:00:00 GMT</z:Win32CreationTime>
            <d:creationdate>2026-09-01T00:00:00Z</d:creationdate>
            <d:getlastmodified>Mon, 03 Aug 2026 12:00:00 GMT</d:getlastmodified>
            """), "/"));

        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), item.CreatedAtUtc);
    }

    [Fact]
    public void ParseMultiStatusResponse_PrefersWin32CreationTimeOverTheCreationDate()
    {
        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/dav");

        var item = Assert.Single(store.ParseMultiStatusResponse(MultiStatus($"""
            <z:Win32CreationTime xmlns:z="urn:schemas-microsoft-com:">{PushCreated:R}</z:Win32CreationTime>
            <d:creationdate>2025-05-05T05:05:05Z</d:creationdate>
            <d:getlastmodified>Mon, 03 Aug 2026 12:00:00 GMT</d:getlastmodified>
            """), "/"));

        Assert.Equal(PushCreated, item.CreatedAtUtc);
    }

    [Fact]
    public async Task ListAsync_RequestsWin32CreationTime()
    {
        string? propfind = null;
        var handler = new RecordingHandler(async request =>
        {
            propfind = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("""<d:multistatus xmlns:d="DAV:"/>""")
            };
        });
        var store = new WebDavRemoteFileStore(new HttpClient(handler), "https://cloud.example.com/dav");

        await store.ListAsync("/docs");

        Assert.Contains("Win32CreationTime", propfind);
        Assert.Contains("urn:schemas-microsoft-com:", propfind);
    }

    [Fact]
    public void ParseMultiStatusResponse_WithoutCreationDate_ReportsNone()
    {
        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/dav");

        var item = Assert.Single(store.ParseMultiStatusResponse(MultiStatus(
            "<d:getlastmodified>Mon, 03 Aug 2026 12:00:00 GMT</d:getlastmodified>"), "/"));

        Assert.Null(item.CreatedAtUtc);
    }

    [Fact]
    public async Task TrySetTimesAsync_SendsBothTimesInOneProppatch()
    {
        var bodies = new List<(string Uri, string Body)>();
        var store = ProppatchRecordingStore(bodies);

        await store.TrySetTimesAsync("/docs/a.txt", PushModified, PushCreated);

        // One request: one round trip and, on a Kaimo target, one feed entry per pushed item.
        var request = Assert.Single(bodies);
        Assert.Contains($"<z:Win32LastModifiedTime>{PushModified:R}</z:Win32LastModifiedTime>", request.Body);
        Assert.Contains($"<z:Win32CreationTime>{PushCreated:R}</z:Win32CreationTime>", request.Body);
        Assert.Equal("https://cloud.example.com/dav/docs/a.txt", request.Uri);
    }

    [Fact]
    public async Task TrySetTimesAsync_WithoutCreationTime_SendsOnlyTheModifiedTime()
    {
        var bodies = new List<(string Uri, string Body)>();
        var store = ProppatchRecordingStore(bodies);

        await store.TrySetTimesAsync("/docs/a.txt", PushModified, null);

        string body = Assert.Single(bodies).Body;
        Assert.Contains("Win32LastModifiedTime", body);
        Assert.DoesNotContain("Win32CreationTime", body);
    }

    [Fact]
    public async Task TrySetTimesAsync_CreationTimeRefusedInMultiStatus_RetriesWithOnlyTheModifiedTime()
    {
        // PROPPATCH is atomic: a server refusing the creation time answers 207 with 403 for it and
        // 424 for the modification time, which the two-way sync needs; the retry sets it alone.
        var bodies = new List<(string Uri, string Body)>();
        var store = ProppatchRecordingStore(bodies, refuseCreation: RefusalKind.MultiStatus);

        await store.TrySetTimesAsync("/docs/a.txt", PushModified, PushCreated);

        Assert.Equal(2, bodies.Count);
        Assert.Contains("Win32LastModifiedTime", bodies[1].Body);
        Assert.DoesNotContain("Win32CreationTime", bodies[1].Body);
    }

    [Fact]
    public async Task TrySetTimesAsync_CombinedRequestRejected_RetriesWithOnlyTheModifiedTime()
    {
        var bodies = new List<(string Uri, string Body)>();
        var store = ProppatchRecordingStore(bodies, refuseCreation: RefusalKind.StatusCode);

        await store.TrySetTimesAsync("/docs/a.txt", PushModified, PushCreated);

        Assert.Equal(2, bodies.Count);
        Assert.DoesNotContain("Win32CreationTime", bodies[1].Body);
    }

    [Fact]
    public async Task TrySetTimesAsync_ModifiedTimeRefused_Throws()
    {
        // The caller treats the stamp as best-effort, but it must still learn of the failure.
        var bodies = new List<(string Uri, string Body)>();
        var store = ProppatchRecordingStore(bodies, refuseAll: true);

        await Assert.ThrowsAnyAsync<Exception>(
            () => store.TrySetTimesAsync("/docs/a.txt", PushModified, PushCreated));
    }

    [Fact]
    public async Task TrySetDirectoryTimesAsync_TargetsTheCollectionUri()
    {
        var bodies = new List<(string Uri, string Body)>();
        var store = ProppatchRecordingStore(bodies);

        await store.TrySetDirectoryTimesAsync("/docs", PushModified, PushCreated);

        var request = Assert.Single(bodies);
        Assert.Equal("https://cloud.example.com/dav/docs/", request.Uri);
        Assert.Contains("Win32CreationTime", request.Body);
    }

    private enum RefusalKind { None, MultiStatus, StatusCode }

    private static string ProppatchMultiStatus(params (string Property, string Status)[] properties) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <d:multistatus xmlns:d="DAV:" xmlns:z="urn:schemas-microsoft-com:">
          <d:response>
            <d:href>/dav/docs/a.txt</d:href>
            {string.Concat(properties.Select(p =>
                $"<d:propstat><d:prop><z:{p.Property}/></d:prop><d:status>HTTP/1.1 {p.Status}</d:status></d:propstat>"))}
          </d:response>
        </d:multistatus>
        """;

    private static IRemoteFileStore ProppatchRecordingStore(
        List<(string Uri, string Body)> bodies,
        RefusalKind refuseCreation = RefusalKind.None,
        bool refuseAll = false)
    {
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal("PROPPATCH", request.Method.Method);
            string body = await request.Content!.ReadAsStringAsync();
            bodies.Add((request.RequestUri!.AbsoluteUri, body));
            if (refuseAll)
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            bool withCreation = body.Contains("Win32CreationTime");
            if (withCreation && refuseCreation == RefusalKind.StatusCode)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            string xml = withCreation && refuseCreation == RefusalKind.MultiStatus
                ? ProppatchMultiStatus(("Win32LastModifiedTime", "424 Failed Dependency"),
                    ("Win32CreationTime", "403 Forbidden"))
                : withCreation
                    ? ProppatchMultiStatus(("Win32LastModifiedTime", "200 OK"), ("Win32CreationTime", "200 OK"))
                    : ProppatchMultiStatus(("Win32LastModifiedTime", "200 OK"));
            return new HttpResponseMessage(HttpStatusCode.MultiStatus) { Content = new StringContent(xml) };
        });
        return new WebDavRemoteFileStore(new HttpClient(handler), "https://cloud.example.com/dav");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
