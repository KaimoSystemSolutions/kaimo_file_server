using Google.Protobuf;
using Grpc.Core;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Security;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC facade for the authentication control plane. Decryption and account
/// filtering remain in Core/Infrastructure; this boundary enforces transport
/// limits, rate limits, and exact NT-hash length.
/// </summary>
public sealed class AuthGrpcService : AuthService.AuthServiceBase
{
    public const int MaximumPageSize = 1_000;
    public const int MaximumExportUsers = 100_000;

    private readonly IAuthenticationLookup _auth;
    private readonly HashExportRateLimiter _rateLimiter;
    private readonly ILogger<AuthGrpcService> _logger;

    public AuthGrpcService(
        IAuthenticationLookup auth,
        HashExportRateLimiter rateLimiter,
        ILogger<AuthGrpcService> logger)
    {
        _auth = auth;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public override async Task<GetNtHashReply> GetNtHash(
        GetNtHashRequest request, ServerCallContext context)
    {
        if (!SambaName.IsValidUsername(request.Username))
            return new GetNtHashReply { Found = false };

        CancellationToken cancellationToken =
            context?.CancellationToken ?? CancellationToken.None;
        byte[]? hash = await _auth.GetNtHashAsync(request.Username)
            .WaitAsync(cancellationToken);
        if (hash is not { Length: 16 })
            return new GetNtHashReply { Found = false };

        return new GetNtHashReply
        {
            Found = true,
            NtHash = ByteString.CopyFrom(hash),
        };
    }

    public override async Task<ListUsersReply> ListUsers(
        ListUsersRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CancellationToken cancellationToken = context.CancellationToken;
        if (request.Offset >= MaximumExportUsers
            || request.PageSize == 0
            || request.PageSize > MaximumPageSize)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"User export requires offset below {MaximumExportUsers} and page_size between 1 and {MaximumPageSize}."));
        }
        int offset = (int)request.Offset;
        int pageSize = (int)request.PageSize;

        string clientId = ResolveClientId(context);

        // Charge one permit for the complete logical export. Continuation pages
        // require a short-lived client/offset-bound token so an arbitrary offset
        // cannot bypass the rate limit.
        if (offset == 0)
        {
            if (!string.IsNullOrEmpty(request.ContinuationToken))
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    "The first user-export page must not include a continuation token."));

            if (!_rateLimiter.TryAcquire(clientId, out TimeSpan retryAfter))
            {
                _logger.LogWarning(
                    "NT-hash export rate limit exceeded for control-plane client {ClientId}; retry after {RetryAfterMs} ms.",
                    clientId,
                    Math.Ceiling(retryAfter.TotalMilliseconds));
                context.ResponseTrailers.Add(
                    "retry-after-ms",
                    Math.Max(
                            0,
                            (long)Math.Ceiling(
                                retryAfter.TotalMilliseconds))
                        .ToString());
                throw new RpcException(new Status(
                    StatusCode.ResourceExhausted,
                    "NT-hash export rate limit exceeded."));
            }
        }
        else if (!_rateLimiter.IsValidContinuationToken(
                     clientId,
                     request.Offset,
                     request.ContinuationToken))
        {
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "Invalid or expired user-export continuation token."));
        }

        _logger.LogInformation(
            "NT-hash export page requested by control-plane client {ClientId}: offset {Offset}, page size {PageSize}.",
            clientId,
            offset,
            pageSize);

        SambaCredentialBatch batch =
            await _auth.GetSambaCredentialBatchAsync(
                offset,
                pageSize,
                cancellationToken);
        if (batch.SourceCount < 0
            || batch.SourceCount > pageSize
            || (batch.HasMore && batch.SourceCount != pageSize)
            || batch.Credentials.Count > batch.SourceCount
            || batch.RejectedUsernames.Count > batch.SourceCount
            || batch.Credentials.Count + batch.RejectedUsernames.Count
                > batch.SourceCount)
        {
            _logger.LogError(
                "NT-hash export produced invalid batch metadata at offset {Offset}: source count {SourceCount}, page size {PageSize}, has more {HasMore}.",
                offset,
                batch.SourceCount,
                pageSize,
                batch.HasMore);
            throw new RpcException(new Status(
                StatusCode.Internal,
                "Credential batch metadata is inconsistent."));
        }

        var reply = new ListUsersReply();
        foreach (SambaCredential credential in batch.Credentials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SambaName.IsValidUsername(credential.Username)
                || credential.NtHash is not { Length: 16 })
            {
                _logger.LogWarning(
                    "NT-hash export rejected an invalid in-memory credential record.");
                continue;
            }

            reply.Users.Add(new UserEntry
            {
                Username = credential.Username,
                NtHash = ByteString.CopyFrom(credential.NtHash),
            });
        }

        int nextOffset = checked(offset + batch.SourceCount);
        if (nextOffset > MaximumExportUsers
            || (batch.HasMore && nextOffset >= MaximumExportUsers))
        {
            throw new RpcException(new Status(
                StatusCode.ResourceExhausted,
                $"NT-hash export exceeds the {MaximumExportUsers}-user safety limit."));
        }

        reply.HasMore = batch.HasMore;
        reply.NextOffset = checked((uint)nextOffset);
        reply.RejectedUsers = checked((uint)batch.RejectedUsernames.Count);
        if (reply.HasMore)
        {
            reply.ContinuationToken =
                _rateLimiter.CreateContinuationToken(
                    clientId,
                    reply.NextOffset);
        }
        if (batch.RejectedUsernames.Count > 0)
        {
            _logger.LogWarning(
                "NT-hash export skipped {RejectedCount} invalid credential rows in the current page.",
                batch.RejectedUsernames.Count);
        }

        _logger.LogInformation(
            "NT-hash export page completed for control-plane client {ClientId}: {Count} active users, {RejectedCount} rejected rows, has more {HasMore}.",
            clientId,
            reply.Users.Count,
            reply.RejectedUsers,
            reply.HasMore);
        return reply;
    }

    private static string ResolveClientId(ServerCallContext context)
    {
        try
        {
            return context.GetHttpContext().Items.TryGetValue(
                    ControlPlaneAuthorizationInterceptor.ClientIdItemKey,
                    out object? value)
                && value is string authenticatedClient
                    ? authenticatedClient
                    : "unknown";
        }
        catch (InvalidOperationException)
        {
            // Direct service unit tests do not have ASP.NET Core's call feature.
            // Network calls always pass the authorization interceptor first.
            return "unknown";
        }
    }
}
