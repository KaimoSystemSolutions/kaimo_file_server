using Google.Protobuf;
using Grpc.Core;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.SmbBridge.Grpc;
using Kaimo_File_Server.SmbBridge.Security;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC facade for the auth control plane. Thin layer over
/// <see cref="IAuthenticationLookup"/> — all logic (decryption,
/// disabled/empty password filter) remains in Core/Infrastructure.
/// </summary>
public sealed class AuthGrpcService : AuthService.AuthServiceBase
{
    private readonly IAuthenticationLookup _auth;
    private readonly IUserRepository _users;
    private readonly HashExportRateLimiter _rateLimiter;
    private readonly ILogger<AuthGrpcService> _logger;

    public AuthGrpcService(
        IAuthenticationLookup auth,
        IUserRepository users,
        HashExportRateLimiter rateLimiter,
        ILogger<AuthGrpcService> logger)
    {
        _auth = auth;
        _users = users;
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
        if (hash is not { Length: > 0 })
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
        string clientId =
            context.GetHttpContext().Items.TryGetValue(
                ControlPlaneAuthorizationInterceptor.ClientIdItemKey,
                out object? value)
            && value is string authenticatedClient
                ? authenticatedClient
                : "unknown";

        if (!_rateLimiter.TryAcquire(clientId, out TimeSpan retryAfter))
        {
            _logger.LogWarning(
                "NT-hash export rate limit exceeded for control-plane client {ClientId}; retry after {RetryAfterMs} ms.",
                clientId,
                Math.Ceiling(retryAfter.TotalMilliseconds));
            context.ResponseTrailers.Add(
                "retry-after-ms",
                Math.Max(0, (long)Math.Ceiling(retryAfter.TotalMilliseconds)).ToString());
            throw new RpcException(new Status(
                StatusCode.ResourceExhausted,
                "NT-hash export rate limit exceeded."));
        }

        _logger.LogInformation(
            "NT-hash export requested by control-plane client {ClientId}.",
            clientId);
        var reply = new ListUsersReply();

        var users = await _users.GetAllAsync().WaitAsync(cancellationToken);
        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // GetNtHashAsync already filters out disabled accounts and empty passwords
            // (returns null) — such users are not synchronized to Samba.
            byte[]? hash = await _auth.GetNtHashAsync(user.Username)
                .WaitAsync(cancellationToken);
            if (hash is not { Length: > 0 })
                continue;

            reply.Users.Add(new UserEntry
            {
                Username = user.Username,
                NtHash = ByteString.CopyFrom(hash),
            });
        }

        _logger.LogInformation(
            "NT-hash export completed for control-plane client {ClientId}: {Count} active users.",
            clientId,
            reply.Users.Count);
        return reply;
    }
}
