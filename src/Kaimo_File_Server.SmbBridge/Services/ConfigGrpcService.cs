using Grpc.Core;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC-Fassade für die SMB-Protokoll-/Sicherheits-Settings (Phase 4). Liest die
/// per Web-UI gepflegten Werte über <see cref="ISmbConfigStore"/> (frisch, ohne
/// Cache) und liefert sie dem Config-Sync im Samba-Container, der sie via
/// <c>net conf setparm global</c> in Sambas Registry spiegelt. Das entspricht dem,
/// was <c>SmbServer.LoadProtocolSettings()</c> beim (Re)Start in den alten
/// .NET-SMB-Server einspeiste.
///
/// Das Dialekt-Enum wird hier auf Sambas Token abgebildet, damit die Shell
/// samba-agnostisch bleibt. WS-Discovery/Audit-Log werden nicht übertragen — sie
/// haben in Samba keine globalen smb.conf-Parameter (separate Mechanismen).
/// </summary>
public sealed class ConfigGrpcService : ConfigService.ConfigServiceBase
{
    private readonly ISmbConfigStore _config;
    private readonly ILogger<ConfigGrpcService> _logger;

    public ConfigGrpcService(ISmbConfigStore config, ILogger<ConfigGrpcService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public override async Task<ProtocolSettingsReply> GetProtocolSettings(
        GetProtocolSettingsRequest request, ServerCallContext context)
    {
        var s = await _config.GetProtocolSettingsAsync(); // bereits normalisiert (Min <= Max)

        var reply = new ProtocolSettingsReply
        {
            MinProtocol = ToSambaDialect(s.MinVersion),
            MaxProtocol = ToSambaDialect(s.MaxVersion),
            RequireSigning = s.RequireSigning,
            RequireEncryption = s.RequireEncryption,
        };

        _logger.LogInformation(
            "GetProtocolSettings -> min={Min} max={Max} signing={Sign} encrypt={Enc}",
            reply.MinProtocol, reply.MaxProtocol, reply.RequireSigning, reply.RequireEncryption);
        return reply;
    }

    /// <summary>Bildet das transport-agnostische Core-Dialekt-Enum auf Sambas Token ab.</summary>
    private static string ToSambaDialect(SmbProtocolVersion v) => v switch
    {
        SmbProtocolVersion.Smb202 => "SMB2_02",
        SmbProtocolVersion.Smb210 => "SMB2_10",
        SmbProtocolVersion.Smb300 => "SMB3_00",
        SmbProtocolVersion.Smb302 => "SMB3_02",
        SmbProtocolVersion.Smb311 => "SMB3_11",
        _ => "SMB2_02",
    };
}
