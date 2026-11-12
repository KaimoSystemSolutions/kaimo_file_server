using Grpc.Core;
using Kaimo_File_Server.Core.Security.Demo;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC facade for SMB login events (AP8d). The Samba container's log forwarder spools the
/// auth_json_audit events and <c>kaimo_authreport</c> sends them here in batches; a failed call
/// keeps the batch in the spool for the next attempt. The events only feed the security
/// overview and notifications; Samba enforces the lockout itself.
/// </summary>
public sealed class SecurityEventGrpcService(
    SmbAuthenticationRecorder recorder,
    ILogger<SecurityEventGrpcService> logger) : SecurityEventService.SecurityEventServiceBase
{
    /// <summary>Upper bound of events per call, matching the sender's batch size.</summary>
    public const int MaxEventsPerCall = 1000;

    public override async Task<ReportAuthenticationReply> ReportAuthentication(
        ReportAuthenticationRequest request, ServerCallContext context)
    {
        if (request.Events.Count > MaxEventsPerCall)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"At most {MaxEventsPerCall} events per call."));

        var batch = request.Events
            .Select(e => new SmbAuthenticationEvent(
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(e.AtUnixMs, 0, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())).UtcDateTime,
                e.Username, string.IsNullOrEmpty(e.Address) ? null : e.Address, e.Status))
            .ToList();
        try
        {
            int recorded = await recorder.RecordAsync(batch, context?.CancellationToken ?? CancellationToken.None);
            logger.LogDebug("ReportAuthentication -> {Recorded} of {Count} SMB login events recorded", recorded, batch.Count);
        }
        catch (ReadOnlyDemoException)
        {
            // The read-only demo stores nothing; acknowledging keeps the sender from retrying forever.
        }
        return new ReportAuthenticationReply { Accepted = (uint)batch.Count };
    }
}
