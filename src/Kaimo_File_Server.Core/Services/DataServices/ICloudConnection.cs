namespace Kaimo_File_Server.Infrastructure.Clouds;

public interface ICloudConnection
{
    Task Dispose();
    string getServiceName();
    Task UploadAsync(string path, Stream data);
    Task DownloadAsync(string path, Stream target);
    Task<IReadOnlyList<string>> ListAsync(string path);
    
}