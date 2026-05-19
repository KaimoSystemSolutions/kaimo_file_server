namespace Kaimo_File_Server.Core.Services
{
    public interface IFileServiceFactory
    {
        IFileService CreateForShare(Guid shareId, string sharePath);
    }
}