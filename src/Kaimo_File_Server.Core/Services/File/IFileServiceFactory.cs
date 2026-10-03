using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Services.File
{
    public interface IFileServiceFactory
    {
        /// <summary>
        /// Creates the file service for <paramref name="share"/>. Takes the whole definition so
        /// share-specific behavior such as the per-home recycle bin of the home-folder share
        /// (<see cref="ShareDefinition.RecycleRootDepth"/>) can never be dropped by a caller.
        /// </summary>
        IFileService CreateForShare(ShareDefinition share);
    }
}
