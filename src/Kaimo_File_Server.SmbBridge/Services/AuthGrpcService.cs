using Google.Protobuf;
using Grpc.Core;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.SmbBridge.Grpc;

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
    private readonly ILogger<AuthGrpcService> _logger;

    public AuthGrpcService(
        IAuthenticationLookup auth,
        IUserRepository users,
        ILogger<AuthGrpcService> logger)
    {
        _auth = auth;
        _users = users;
        _logger = logger;
    }

    public override async Task<GetNtHashReply> GetNtHash(
        GetNtHashRequest request, ServerCallContext context)
    {
        byte[]? hash = await _auth.GetNtHashAsync(request.Username);
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
        var reply = new ListUsersReply();

        foreach (var user in await _users.GetAllAsync())
        {
            // GetNtHashAsync already filters out disabled accounts and empty passwords
            // (returns null) — such users are not synchronized to Samba.
            byte[]? hash = await _auth.GetNtHashAsync(user.Username);
            if (hash is not { Length: > 0 })
                continue;

            reply.Users.Add(new UserEntry
            {
                Username = user.Username,
                NtHash = ByteString.CopyFrom(hash),
            });
        }

        _logger.LogInformation("ListUsers: {Count} active users exported.", reply.Users.Count);
        return reply;
    }
}
