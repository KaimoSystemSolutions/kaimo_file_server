using Grpc.Core;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC facade for share provisioning (Phase 4). Provides the currently enabled
/// Kaimo shares so the Samba container can mirror them live in its registry
/// (<c>net conf</c>) — the replacement for the FileSystemWatcher/
/// <c>SyncFromDb()</c> mechanism from <c>SmbServer.cs</c>. Thin facade over
/// <see cref="IShareRepository.GetAllEnabledAsync"/>; no share logic is
/// duplicated (the repository already filters disabled shares).
///
/// Visibility (ABE): only the hidden flag is transported
/// (<see cref="ShareEntry.IsHidden"/> → <c>browseable = no</c>). Hard share
/// access is unchanged and decided by the VFS connect hook based on real Kaimo ACLs
/// (<see cref="AuthzGrpcService.AuthorizeConnect"/>).
/// </summary>
public sealed class ShareGrpcService : ShareService.ShareServiceBase
{
    private readonly IShareRepository _shares;
    private readonly ILogger<ShareGrpcService> _logger;

    public ShareGrpcService(IShareRepository shares, ILogger<ShareGrpcService> logger)
    {
        _shares = shares;
        _logger = logger;
    }

    public override async Task<ListSharesReply> ListShares(
        ListSharesRequest request, ServerCallContext context)
    {
        CancellationToken cancellationToken =
            context?.CancellationToken ?? CancellationToken.None;
        var defs = await _shares.GetAllEnabledAsync()
            .WaitAsync(cancellationToken);

        var reply = new ListSharesReply();
        foreach (var def in defs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reply.Shares.Add(new ShareEntry
            {
                Name = def.Name,
                Path = def.Path,
                IsHidden = def.IsShareHidden,
            });
        }

        _logger.LogInformation("ListShares -> {Count} enabled shares.", reply.Shares.Count);
        return reply;
    }
}
