using Grpc.Core;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC-Fassade für das Share-Provisioning (Phase 4). Liefert die aktuell
/// aktivierten Kaimo-Shares, damit der Samba-Container sie live in seine Registry
/// (<c>net conf</c>) spiegeln kann — der Ersatz für den FileSystemWatcher/
/// <c>SyncFromDb()</c>-Mechanismus aus <c>SmbServer.cs</c>. Dünne Fassade über
/// <see cref="IShareRepository.GetAllEnabledAsync"/>; es wird keine Share-Logik
/// dupliziert (deaktivierte Shares filtert bereits das Repository).
///
/// Sichtbarkeit (ABE): es wird nur das Hidden-Flag transportiert
/// (<see cref="ShareEntry.IsHidden"/> → <c>browseable = no</c>). Der harte
/// Share-Zugriff wird unverändert vom VFS-connect-Hook nach echten Kaimo-ACLs
/// entschieden (<see cref="AuthzGrpcService.AuthorizeConnect"/>).
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
        var defs = await _shares.GetAllEnabledAsync();

        var reply = new ListSharesReply();
        foreach (var def in defs)
        {
            reply.Shares.Add(new ShareEntry
            {
                Name = def.Name,
                Path = def.Path,
                IsHidden = def.IsShareHidden,
            });
        }

        _logger.LogInformation("ListShares -> {Count} aktivierte Shares.", reply.Shares.Count);
        return reply;
    }
}
