using Grpc.Core;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC facade for SMB protocol/security settings (Phase 4). Reads the values
/// maintained via web UI through <see cref="ISmbConfigStore"/> (fresh, no cache)
/// and provides them to the config sync in the Samba container, which mirrors them
/// via <c>net conf setparm global</c> into Samba's registry. That corresponds to
/// what <c>SmbServer.LoadProtocolSettings()</c> fed into the old .NET SMB server
/// on (re)start.
///
/// The dialect enum is mapped to Samba's tokens here so the shell remains
/// Samba-agnostic. WS-Discovery/Audit Log are not transported — they have no global
/// smb.conf parameters in Samba (separate mechanisms).
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
        var s = await _config.GetProtocolSettingsAsync(); // already normalized (Min <= Max)
        bool enabled = await _config.IsSmbEnabledAsync();

        var reply = new ProtocolSettingsReply
        {
            MinProtocol = ToSambaDialect(s.MinVersion),
            MaxProtocol = ToSambaDialect(s.MaxVersion),
            RequireSigning = s.RequireSigning,
            RequireEncryption = s.RequireEncryption,
            Enabled = enabled,
        };

        _logger.LogInformation(
            "GetProtocolSettings -> min={Min} max={Max} signing={Sign} encrypt={Enc} enabled={Enabled}",
            reply.MinProtocol, reply.MaxProtocol, reply.RequireSigning, reply.RequireEncryption, reply.Enabled);
        return reply;
    }

    /// <summary>Maps the transport-agnostic core dialect enum to Samba's tokens.</summary>
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
