using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Core.Services.File;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Infrastructure.Clouds;

public class GoogleDriveConnection : ICloudConnection
{
    private readonly DriveService _service;
    private readonly string _refreshToken;

    public DriveService Service => _service;

    public GoogleDriveConnection(Guid shareId, Dictionary<string, string> data, IConfiguration configuration)
    {
        if (!data.TryGetValue("refreshToken", out var refreshToken))
            throw new InvalidOperationException("Google Drive refresh token is missing");

        if (!data.TryGetValue("scope", out var scopeString))
            throw new InvalidOperationException("Google Drive scope is missing");

        _refreshToken = refreshToken;

        var scopes = scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var credential = new UserCredential(
            new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = configuration["GoogleOAuth:ClientId"]!,
                        ClientSecret = configuration["GoogleOAuth:ClientSecret"]!
                    },
                    Scopes = scopes
                }),
            // Token store key now identifies share + this specific credential,
            // since a share can hold several independent Google connections
            // is no longer possible for the *same* provider on one share
            // (see note below), but keeping shareId here is still correct
            // and avoids collisions across shares.
            "share-" + shareId,
            new TokenResponse
            {
                RefreshToken = refreshToken
            });

        _service = new DriveService(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Kaimo_File_Server"
            });
    }

    public static async Task RevokeTokenAsync(string refreshToken)
    {
        using var http = new HttpClient();

        var response = await http.PostAsync(
            "https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = refreshToken
            }));

        response.EnsureSuccessStatusCode();
    }

    public async Task Dispose()
    {
        await RevokeTokenAsync(_refreshToken);
        Service.Dispose();
    }

    public string getServiceName() => "Google";

    public async Task<string> GetAccountEmailAsync()
    {
        var request = _service.About.Get();
        request.Fields = "user(emailAddress)";

        var about = await request.ExecuteAsync();

        if (string.IsNullOrEmpty(about.User?.EmailAddress))
            throw new InvalidOperationException("Could not retrieve Google account email.");

        return about.User.EmailAddress;
    }

    public async Task<string> GetProfilePictureUrlAsync()
    {
        var request = _service.About.Get();
        request.Fields = "user(photoLink)";

        var about = await request.ExecuteAsync();

        if (string.IsNullOrEmpty(about.User?.PhotoLink))
            throw new InvalidOperationException("Could not retrieve Google account profile picture.");

        return about.User.PhotoLink;
    }

    public async Task UploadAsync(string path, Stream data, DateTime modifiedTime)
    {
        path = path.Replace('\\', '/').Trim('/');

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            throw new ArgumentException("Invalid path.", nameof(path));

        string fileName = parts[^1];

        string parentId = "root";

        // Create/find all parent folders
        for (int i = 0; i < parts.Length - 1; i++)
        {
            parentId = await GetOrCreateFolderAsync(parts[i], parentId);
        }

        // Check if file already exists
        string? existingFileId = await FindFileIdAsync(
            fileName,
            parentId);

        var file = new Google.Apis.Drive.v3.Data.File
        {
            Name = fileName
        };

        file.ModifiedTimeDateTimeOffset = modifiedTime;

        if (existingFileId != null)
        {
            // Overwrite existing file
            var request = _service.Files.Update(
                file,
                existingFileId,
                data,
                "application/octet-stream");

            request.Fields = "id";

            await request.UploadAsync();
        }
        else
        {
            // Create new file
            file.Parents = new[] { parentId };

            var request = _service.Files.Create(
                file,
                data,
                "application/octet-stream");

            request.Fields = "id";

            await request.UploadAsync();
        }
    }

    public async Task DownloadAsync(string path, Stream target)
    {
        var fileId = await FindFileByPathAsync(path);

        if (fileId == null)
            throw new FileNotFoundException(path);

        var request = _service.Files.Get(fileId);

        await request.DownloadAsync(target);
    }
    
    public async Task<IReadOnlyList<CloudItemMeta>> ListAsync(string path)
    {
        string? parentId = await FindFolderByPathAsync(path);

        if (parentId == null)
            return [];

        var request = _service.Files.List();
        request.Q = $"'{parentId}' in parents and trashed = false";
        request.Fields = "files(id,name,mimeType,modifiedTime,size)";

        var result = await request.ExecuteAsync();

        return result.Files.Select(f => new CloudItemMeta(
            IsDirectory: f.MimeType == "application/vnd.google-apps.folder",
            Name: f.Name,
            Path: path.TrimEnd('/') + "/" + f.Name,
            ModifiedAt: f.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue,
            Size: f.Size ?? 0
        )).ToList();
    }

    public async Task<long> GetDirectorySizeAsync(string path)
    {
        long totalSize = 0;

        foreach (var item in await ListAsync(path))
            if (item.IsDirectory)
                totalSize += await GetDirectorySizeAsync(item.Path);
            else
                totalSize += item.Size;
        
        return totalSize;
    }
    
    private async Task<string> GetOrCreateFolderAsync(string name, string parentId)
    {
        var list = _service.Files.List();
        list.Q =
            $"mimeType='application/vnd.google-apps.folder' and " +
            $"name='{name.Replace("'", "\\'")}' and " +
            $"'{parentId}' in parents and trashed=false";

        list.Fields = "files(id)";

        var existing = await list.ExecuteAsync();

        if (existing.Files.Count > 0)
            return existing.Files[0].Id;

        var folder = new Google.Apis.Drive.v3.Data.File
        {
            Name = name,
            MimeType = "application/vnd.google-apps.folder",
            Parents = new[] { parentId }
        };

        var create = _service.Files.Create(folder);
        create.Fields = "id";

        var created = await create.ExecuteAsync();

        return created.Id;
    }
    
    public async Task CreateDirectoryAsync(string path)
    {
        path = path.Replace('\\', '/').Trim('/');

        if (string.IsNullOrEmpty(path))
            return;

        string parentId = "root";

        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            parentId = await GetOrCreateFolderAsync(part, parentId);
        }
    }
    

    private async Task<string?> FindFolderByPathAsync(string path)
    {
        path = path.Replace('\\', '/').Trim('/');

        if (string.IsNullOrEmpty(path))
            return "root";

        string parent = "root";

        foreach (var part in path.Split('/'))
        {
            var list = _service.Files.List();

            list.Q =
                $"mimeType='application/vnd.google-apps.folder' and " +
                $"name='{part.Replace("'", "\\'")}' and " +
                $"'{parent}' in parents and trashed=false";

            list.Fields = "files(id)";

            var result = await list.ExecuteAsync();

            if (result.Files.Count == 0)
                return null;

            parent = result.Files[0].Id;
        }

        return parent;
    }

    private async Task<string?> FindFileByPathAsync(string path)
    {
        path = path.Replace('\\', '/').Trim('/');

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return null;

        var parent = "root";

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var folder = await FindFolderByPathAsync(string.Join("/", parts.Take(i + 1)));

            if (folder == null)
                return null;

            parent = folder;
        }

        var request = _service.Files.List();

        request.Q =
            $"name='{parts[^1].Replace("'", "\\'")}' and " +
            $"'{parent}' in parents and trashed=false";

        request.Fields = "files(id)";

        var result = await request.ExecuteAsync();

        return result.Files.FirstOrDefault()?.Id;
    }
    
    private async Task<string?> FindFileIdAsync(
        string fileName,
        string parentId)
    {
        var request = _service.Files.List();

        request.Q = 
            $"'{parentId}' in parents " +
            $"and name = '{fileName.Replace("'", "\\'")}' " +
            $"and trashed = false";

        request.Fields = "files(id, name)";

        var result = await request.ExecuteAsync();

        return result.Files.FirstOrDefault()?.Id;
    }
}