using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Infrastructure.Services;

namespace Kaimo_File_Server.Web.Helpers;

/// <summary>User-facing error text for a failed <see cref="SharePoolMoveService"/> move (shares and home folders).</summary>
public static class PoolMoveMessages
{
    public static string ErrorFor(SharePoolMoveOutcome outcome) => outcome.Result switch
    {
        SharePoolMoveResult.SourceMissing => Resources.Web_Error_SharePathMissing,
        SharePoolMoveResult.DestinationExists => Resources.Web_Error_StoragePoolDestinationExists,
        SharePoolMoveResult.InUse => Resources.Web_Error_ShareInUse,
        SharePoolMoveResult.InsufficientSpace => string.Format(
            Resources.ResourceManager.GetString("Web_Error_StoragePoolInsufficientSpace")
                ?? "Not enough free space in the target storage pool: {0} required, {1} available.",
            FormatHelper.FormatFileSize(outcome.Required),
            FormatHelper.FormatFileSize(outcome.Available)),
        _ => Resources.Web_Error_StoragePoolMoveFailed,
    };
}
