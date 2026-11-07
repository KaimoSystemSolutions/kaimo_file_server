using System.Net;
using System.Text.Json;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Clouds;
using Xunit;

namespace Kaimo_File_Server.Infrastructure.Tests.Clouds;

/// <summary>Creation times over Microsoft Graph: <c>fileSystemInfo.createdDateTime</c> both ways.</summary>
public sealed partial class OneDriveConnectionTests
{
    private static readonly DateTime GraphModified = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GraphCreated = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static StubHttpMessageHandler GraphRecorder(
        List<(HttpMethod Method, string Path, string Body)> requests, string listJson = "{}")
        => new(async request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Json(HttpStatusCode.OK, """{"access_token":"access-token","expires_in":3600}""");
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method, Uri.UnescapeDataString(request.RequestUri.PathAndQuery), body));
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, listJson);
            return Json(HttpStatusCode.OK, """{"id":"item-1"}""");
        });

    private static JsonElement FileSystemInfo(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("fileSystemInfo");

    [Fact]
    public async Task UploadAsync_WithCreationTime_PatchesBothFileSystemTimes()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        using var http = new HttpClient(GraphRecorder(requests));
        ICloudConnection connection = new OneDriveConnection(CreateData(), http);

        await connection.UploadAsync("a.txt", new MemoryStream([1]), GraphModified, GraphCreated);

        var patch = Assert.Single(requests, r => r.Method == HttpMethod.Patch);
        var info = FileSystemInfo(patch.Body);
        Assert.Equal(GraphCreated, info.GetProperty("createdDateTime").GetDateTime().ToUniversalTime());
        Assert.Equal(GraphModified, info.GetProperty("lastModifiedDateTime").GetDateTime().ToUniversalTime());
        await connection.Dispose();
    }

    [Fact]
    public async Task UploadAsync_WithoutCreationTime_PatchesOnlyTheModifiedTime()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        using var http = new HttpClient(GraphRecorder(requests));
        var connection = new OneDriveConnection(CreateData(), http);

        await connection.UploadAsync("a.txt", new MemoryStream([1]), GraphModified);

        var info = FileSystemInfo(Assert.Single(requests, r => r.Method == HttpMethod.Patch).Body);
        Assert.False(info.TryGetProperty("createdDateTime", out _));
        await connection.Dispose();
    }

    [Fact]
    public async Task TrySetDirectoryTimesAsync_PatchesTheFolderByPath()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        using var http = new HttpClient(GraphRecorder(requests));
        ICloudConnection connection = new OneDriveConnection(CreateData(), http);

        await connection.TrySetDirectoryTimesAsync("/docs/sub", GraphModified, GraphCreated);

        var patch = Assert.Single(requests);
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.EndsWith("/me/drive/root:/docs/sub", patch.Path);
        var info = FileSystemInfo(patch.Body);
        Assert.Equal(GraphCreated, info.GetProperty("createdDateTime").GetDateTime().ToUniversalTime());
        Assert.Equal(GraphModified, info.GetProperty("lastModifiedDateTime").GetDateTime().ToUniversalTime());
        await connection.Dispose();
    }

    [Fact]
    public async Task ListAsync_ReportsTheFileSystemCreationTime()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        const string listJson = """
            {"value":[
              {"name":"a.txt","size":1,"file":{},"lastModifiedDateTime":"2026-08-03T12:00:00Z",
               "createdDateTime":"2026-08-03T11:00:00Z",
               "fileSystemInfo":{"createdDateTime":"2024-01-02T03:04:05Z"}},
              {"name":"b.txt","size":1,"file":{},"lastModifiedDateTime":"2026-08-03T12:00:00Z",
               "createdDateTime":"2025-05-05T05:05:05Z"}
            ]}
            """;
        using var http = new HttpClient(GraphRecorder(requests, listJson));
        var connection = new OneDriveConnection(CreateData(), http);

        var items = await connection.ListAsync("/");

        Assert.Equal(GraphCreated, items.Single(i => i.Name == "a.txt").CreatedAt);
        Assert.Equal(new DateTime(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc), items.Single(i => i.Name == "b.txt").CreatedAt);
        // Graph returns only the selected properties.
        Assert.Contains("fileSystemInfo", requests.Single().Path, StringComparison.Ordinal);
        await connection.Dispose();
    }

    [Fact]
    public async Task ListAsync_IgnoresAnItemCreationTimeAfterTheModifiedTime()
    {
        // Without fileSystemInfo the item-level createdDateTime is the upload time; a file uploaded
        // with an older modification time would otherwise show the upload as its creation.
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        const string listJson = """
            {"value":[
              {"name":"a.txt","size":1,"file":{},"lastModifiedDateTime":"2026-08-03T12:00:00Z",
               "createdDateTime":"2026-09-01T00:00:00Z"}
            ]}
            """;
        using var http = new HttpClient(GraphRecorder(requests, listJson));
        var connection = new OneDriveConnection(CreateData(), http);

        var item = Assert.Single(await connection.ListAsync("/"));

        Assert.Null(item.CreatedAt);
        await connection.Dispose();
    }

    [Fact]
    public async Task ListAsync_TrustsAFileSystemCreationTimeAfterTheModifiedTime()
    {
        // fileSystemInfo is set by clients (a file copied on Windows keeps its old mtime and gets the
        // copy time as creation time), so it is not clamped.
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        const string listJson = """
            {"value":[
              {"name":"a.txt","size":1,"file":{},"lastModifiedDateTime":"2026-08-03T12:00:00Z",
               "createdDateTime":"2026-09-01T00:00:00Z",
               "fileSystemInfo":{"createdDateTime":"2026-09-01T00:00:00Z"}}
            ]}
            """;
        using var http = new HttpClient(GraphRecorder(requests, listJson));
        var connection = new OneDriveConnection(CreateData(), http);

        var item = Assert.Single(await connection.ListAsync("/"));

        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), item.CreatedAt);
        await connection.Dispose();
    }
}
