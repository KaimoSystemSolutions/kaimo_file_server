using System.Formats.Tar;
using System.IO.Compression;

namespace Kaimo_File_Server.Infrastructure.Storage;

// Archive creation (zip / tar.gz) and extraction.
public partial class FileSystemStorage
{
    public async Task ArchiveAsync(List<string> sourcePaths, string targetPath, string format)
    {
        var fullTargetPath = ToAbsolutePath(targetPath);

        if (File.Exists(fullTargetPath))
            throw new IOException($"File already exists: '{targetPath}'");

        await Task.Run(() =>
        {
            switch (format)
            {
                case ".zip":
                    CreateZip(sourcePaths, fullTargetPath);
                    break;
                case ".tar.gz":
                    CreateTarGz(sourcePaths, fullTargetPath);
                    break;
                default:
                    throw new NotSupportedException($"Archive format '{format}' is not supported.");
            }
        });
    }


    private void CreateZip(List<string> sourcePaths, string targetPath)
    {
        using var zip = ZipFile.Open(targetPath, ZipArchiveMode.Create);
    
        foreach (var sourcePath in sourcePaths)
        {
            var fullPath = ToAbsolutePath(sourcePath);
    
            if (Directory.Exists(fullPath))
                AddDirectoryToZip(zip, fullPath, Path.GetFileName(fullPath));
            else if (File.Exists(fullPath))
                zip.CreateEntryFromFile(fullPath, Path.GetFileName(fullPath));
        }
    }
    
    private void AddDirectoryToZip(ZipArchive zip, string dirPath, string entryBase)
    {
        foreach (var file in Directory.GetFiles(dirPath, "*", WithoutLinksRecursive))
        {
            var entryName = Path.Combine(entryBase, Path.GetRelativePath(dirPath, file))
                .Replace('\\', '/');
            zip.CreateEntryFromFile(file, entryName);
        }
    }
    
    private void CreateTarGz(List<string> sourcePaths, string targetPath)
    {
        using var fileStream = File.Create(targetPath);
        using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
        using var tarWriter = new TarWriter(gzipStream);
    
        foreach (var sourcePath in sourcePaths)
        {
            var fullPath = ToAbsolutePath(sourcePath);
    
            if (Directory.Exists(fullPath))
                AddDirectoryToTar(tarWriter, fullPath, Path.GetFileName(fullPath));
            else if (File.Exists(fullPath))
                tarWriter.WriteEntry(fullPath, Path.GetFileName(fullPath));
        }
    }
    
    private void AddDirectoryToTar(TarWriter tarWriter, string dirPath, string entryBase)
    {
        foreach (var file in Directory.GetFiles(dirPath, "*", WithoutLinksRecursive))
        {
            var entryName = Path.Combine(entryBase, Path.GetRelativePath(dirPath, file))
                .Replace('\\', '/');
            tarWriter.WriteEntry(file, entryName);
        }
    }

    public async Task UnzipAsync(string zipPath, string targetPath)
    {
        var fullZipPath = ToAbsolutePath(zipPath);
        var fullTargetPath = ToAbsolutePath(targetPath);

        if (Directory.Exists(fullTargetPath))
            throw new IOException($"Directory already exists: '{targetPath}'");

        await Task.Run(() =>
        {
            ZipFile.ExtractToDirectory(fullZipPath, fullTargetPath);
        });
    }
}
