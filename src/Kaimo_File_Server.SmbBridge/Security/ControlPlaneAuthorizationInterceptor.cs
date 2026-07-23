using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Kaimo_File_Server.SmbBridge.Security;

/// <summary>
/// Enforces the per-client RPC allow-list after the TLS handshake has validated
/// the certificate chain.
/// </summary>
public sealed class ControlPlaneAuthorizationInterceptor(
    ILogger<ControlPlaneAuthorizationInterceptor> logger) : Interceptor
{
    public const string ClientIdItemKey = "Kaimo.ControlPlane.ClientId";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        HttpContext httpContext = context.GetHttpContext();
        string? clientId = ControlPlaneTls.GetClientId(
            httpContext.Connection.ClientCertificate);

        if (clientId is null)
        {
            logger.LogWarning(
                "Rejected unauthenticated SMB control-plane RPC {Method} from {RemoteIp}.",
                context.Method,
                httpContext.Connection.RemoteIpAddress);
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "A recognized client certificate is required."));
        }

        if (!ControlPlaneAccessPolicy.IsAllowed(clientId, context.Method))
        {
            logger.LogWarning(
                "Rejected unauthorized SMB control-plane RPC {Method} from client {ClientId}.",
                context.Method,
                clientId);
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "The authenticated workload is not allowed to call this method."));
        }

        httpContext.Items[ClientIdItemKey] = clientId;
        return await continuation(request, context);
    }
}
