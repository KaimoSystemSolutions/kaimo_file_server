using System.Net;
using System.Text;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.ExternalStorage;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Xunit;

namespace Kaimo_File_Server.Tests;

public sealed partial class WebDavStorageProviderTests
{
    [Fact]
    public void ParseMultiStatusResponse_ParsesTypicalNextcloudResponse()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/remote.php/dav/files/user/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:resourcetype><d:collection/></d:resourcetype>
                    <d:getlastmodified>Wed, 19 Aug 2026 10:00:00 GMT</d:getlastmodified>
                  </d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/remote.php/dav/files/user/Documents/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:resourcetype><d:collection/></d:resourcetype>
                    <d:getlastmodified>Thu, 20 Aug 2026 14:30:00 GMT</d:getlastmodified>
                  </d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/remote.php/dav/files/user/report.pdf</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:resourcetype/>
                    <d:getcontentlength>123456</d:getcontentlength>
                    <d:getlastmodified>Fri, 21 Aug 2026 09:15:00 GMT</d:getlastmodified>
                  </d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var store = new WebDavRemoteFileStore(new HttpClient(), "https://cloud.example.com/remote.php/dav/files/user");
        var items = store.ParseMultiStatusResponse(xml, "/");

        Assert.Equal(2, items.Count);

        Assert.Equal("Documents", items[0].Name);
        Assert.Equal("/Documents", items[0].Path);
        Assert.True(items[0].IsDirectory);
        Assert.Null(items[0].Size);

        Assert.Equal("report.pdf", items[1].Name);
        Assert.Equal("/report.pdf", items[1].Path);
        Assert.False(items[1].IsDirectory);
        Assert.Equal(123456L, items[1].Size);
        Assert.True(items[1].ModifiedAtUtc.HasValue);
        Assert.Equal(2026, items[1].ModifiedAtUtc!.Value.Year);
    }

    [Fact]
    public void ParseMultiStatusResponse_SkipsNon200PropStats()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/dav/root/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/root/forbidden.txt</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 403 Forbidden</d:status>
                  <d:prop><d:resourcetype/></d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var store = new WebDavRemoteFileStore(new HttpClient(), "https://example.com/dav/root");
        var items = store.ParseMultiStatusResponse(xml, "/");

        Assert.Empty(items);
    }

    [Fact]
    public void ParseMultiStatusResponse_HandlesUrlEncodedHrefs()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/dav/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/My%20Documents/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var store = new WebDavRemoteFileStore(new HttpClient(), "https://example.com/dav");
        var items = store.ParseMultiStatusResponse(xml, "/");

        Assert.Single(items);
        Assert.Equal("My Documents", items[0].Name);
    }

    [Fact]
    public void ParseMultiStatusResponse_ParsesNestedDirectoryListing()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/dav/team/docs/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/team/docs/notes.txt</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:resourcetype/>
                    <d:getcontentlength>42</d:getcontentlength>
                  </d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var store = new WebDavRemoteFileStore(new HttpClient(), "https://example.com/dav");
        var items = store.ParseMultiStatusResponse(xml, "/team/docs");

        Assert.Single(items);
        Assert.Equal("notes.txt", items[0].Name);
        Assert.Equal("/team/docs/notes.txt", items[0].Path);
        Assert.Equal(42L, items[0].Size);
    }

    [Fact]
    public void ParseMultiStatusResponse_SkipsSelfEntryWhenParentPathHasSpaces()
    {
        // Nextcloud percent-encodes the collection's own href; the parent path
        // carries a literal space. The self-entry must still be filtered so the
        // tree walker does not recurse into "Shared Documents/Shared Documents".
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/dav/Documents/Shared%20Documents/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/Documents/Shared%20Documents/report.txt</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:resourcetype/>
                    <d:getcontentlength>7</d:getcontentlength>
                  </d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var store = new WebDavRemoteFileStore(new HttpClient(), "https://example.com/dav");
        var items = store.ParseMultiStatusResponse(xml, "/Documents/Shared Documents");

        Assert.Single(items);
        Assert.Equal("report.txt", items[0].Name);
    }
}
