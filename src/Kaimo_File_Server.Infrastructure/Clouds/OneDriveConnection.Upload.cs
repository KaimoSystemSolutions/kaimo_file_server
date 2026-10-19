using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public partial class OneDriveConnection
{
    /// <summary>
    /// Resolves or creates every segment of a remote directory path. Conflict
    /// responses are re-read to handle concurrent folder creation safely.
    /// </summary>
    private async Task<string> EnsureDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var parentId = await GetRootIdAsync(cancellationToken);
        var currentPath = "";

        foreach (var segment in NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentPath = CombinePath(currentPath, segment);
            var existing = await GetItemByPathAsync(currentPath, cancellationToken);
            if (existing is not null)
            {
                if (!existing.Value.IsDirectory)
                    throw new IOException($"A OneDrive file already exists at '{currentPath}'.");
                parentId = existing.Value.Id;
                continue;
            }

            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = segment,
                ["folder"] = new { },
                ["@microsoft.graph.conflictBehavior"] = "fail"
            });
            using var response = await SendGraphAsync(
                HttpMethod.Post,
                $"/me/drive/items/{Uri.EscapeDataString(parentId)}/children",
                new StringContent(body, Encoding.UTF8, "application/json"),
                cancellationToken: cancellationToken);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                existing = await GetItemByPathAsync(currentPath, cancellationToken);
                if (existing is not { IsDirectory: true } concurrentFolder)
                {
                    await EnsureSuccessAsync(response, cancellationToken);
                    throw new IOException($"Unable to create the OneDrive folder '{currentPath}'.");
                }

                parentId = concurrentFolder.Id;
                continue;
            }

            using var json = await ParseSuccessAsync(response, cancellationToken);
            parentId = json.RootElement.GetProperty("id").GetString()
                       ?? throw new InvalidOperationException("Microsoft Graph did not return a folder id.");
        }

        return parentId;
    }

    /// <summary>Uploads a file with Graph's single-request content endpoint.</summary>
    private async Task<string> UploadSmallFileAsync(
        string path,
        Stream data,
        CancellationToken cancellationToken)
    {
        using var content = new StreamContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendGraphAsync(
            HttpMethod.Put,
            $"/me/drive/root:/{EncodePath(path)}:/content",
            content,
            cancellationToken: cancellationToken);
        using var json = await ParseSuccessAsync(response, cancellationToken);
        return json.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Microsoft Graph did not return a file id.");
    }

    /// <summary>
    /// Uploads a seekable stream in Graph-compatible fragments. Upload-session
    /// URLs are preauthenticated and therefore intentionally receive no bearer token.
    /// </summary>
    private async Task<string> UploadLargeFileAsync(
        string path,
        Stream data,
        CancellationToken cancellationToken)
    {
        var sessionBody = JsonSerializer.Serialize(new
        {
            item = new Dictionary<string, object>
            {
                ["@microsoft.graph.conflictBehavior"] = "replace",
                ["name"] = path.Split('/')[^1]
            }
        });
        using var sessionResponse = await SendGraphAsync(
            HttpMethod.Post,
            $"/me/drive/root:/{EncodePath(path)}:/createUploadSession",
            new StringContent(sessionBody, Encoding.UTF8, "application/json"),
            cancellationToken: cancellationToken);
        using var sessionJson = await ParseSuccessAsync(sessionResponse, cancellationToken);
        var uploadUrl = sessionJson.RootElement.GetProperty("uploadUrl").GetString()
                        ?? throw new InvalidOperationException("Microsoft Graph did not return an upload URL.");

        var totalLength = data.Length - data.Position;
        long uploaded = 0;
        var buffer = new byte[UploadChunkSize];

        while (uploaded < totalLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, totalLength - uploaded);
            var read = 0;
            while (read < requested)
            {
                var count = await data.ReadAsync(
                    buffer.AsMemory(read, requested - read),
                    cancellationToken);
                if (count == 0)
                    throw new EndOfStreamException("The upload source ended before its reported length.");
                read += count;
            }

            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentRange = new ContentRangeHeaderValue(
                uploaded,
                uploaded + read - 1,
                totalLength);
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
            // Upload-session URLs are preauthenticated and must not receive the
            // Microsoft Graph Authorization header.
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            uploaded += read;

            if (uploaded == totalLength)
            {
                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(
                    responseStream,
                    cancellationToken: cancellationToken);
                return json.RootElement.GetProperty("id").GetString()
                       ?? throw new InvalidOperationException("Microsoft Graph did not return a file id.");
            }
        }

        throw new InvalidOperationException("The OneDrive upload did not complete.");
    }
}
