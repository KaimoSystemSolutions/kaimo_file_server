using Grpc.Core;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.SmbBridge.Grpc;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// gRPC-Fassade fuer die Autorisierungs-Control-Plane (Phase 2). Entscheidet
/// anhand der echten Kaimo-ACLs, ob ein Benutzer einen Share betreten darf —
/// dieselbe Semantik wie das frühere <c>KaimoSharePolicy.AuthorizeConnect</c>
/// (→ <see cref="IAuthenticationLookup.CanAccessShareAsync"/>).
///
/// Der Samba-Sidecar <c>kaimo_authd</c> ruft dies im VFS-connect-Hook auf und
/// uebersetzt Namen (User/Share) — die Aufloesung auf Guids passiert hier.
/// </summary>
public sealed class AuthzGrpcService : AuthzService.AuthzServiceBase
{
    private readonly IUserRepository _users;
    private readonly IShareRepository _shares;
    private readonly IAuthenticationLookup _auth;
    private readonly IAclService _acl;
    private readonly ILogger<AuthzGrpcService> _logger;

    public AuthzGrpcService(
        IUserRepository users,
        IShareRepository shares,
        IAuthenticationLookup auth,
        IAclService acl,
        ILogger<AuthzGrpcService> logger)
    {
        _users = users;
        _shares = shares;
        _auth = auth;
        _acl = acl;
        _logger = logger;
    }

    public override async Task<AuthorizeReply> AuthorizeConnect(
        AuthorizeConnectRequest request, ServerCallContext context)
    {
        var user = await _users.GetByUsernameAsync(request.Username);
        if (user is null || !user.IsEnabled)
            return Deny($"unknown or disabled user '{request.Username}'");

        var share = await _shares.GetByNameAsync(request.Share);
        if (share is null)
            return Deny($"unknown share '{request.Share}'");

        bool allow = await _auth.CanAccessShareAsync(share.Id, user.Id);

        _logger.LogInformation(
            "AuthorizeConnect: user={User} share={Share} -> {Decision}",
            request.Username, request.Share, allow ? "ALLOW" : "DENY");

        return new AuthorizeReply { Allow = allow, Reason = allow ? "" : "acl denied" };
    }

    /// <summary>
    /// Datei-/Pfad-Autorisierung — spiegelt <c>FileService.OpenAsync</c> exakt:
    /// nicht-existent + create → Parent braucht <c>CreateWriteData</c>; existent →
    /// write braucht <c>CreateWriteData</c>, jeder nicht-reine-write-Open braucht
    /// zusaetzlich <c>ListReadData</c> (Read wird unabhaengig geprueft). Nicht
    /// vorhandene Dateien ohne Create-Absicht sind kein ACL-Deny (das behandelt
    /// Samba als „not found").
    /// </summary>
    public override async Task<AuthorizeReply> AuthorizeOpen(
        AuthorizeOpenRequest request, ServerCallContext context)
    {
        var user = await _auth.ResolveUserContextAsync(request.Username);
        if (user is null)
            return Deny($"unknown user '{request.Username}'");

        var share = await _shares.GetByNameAsync(request.Share);
        if (share is null)
            return Deny($"unknown share '{request.Share}'");

        string normalized = ShareRelativePath.Normalize(request.Path);
        string full = Path.Combine(share.Path, normalized);
        bool exists = File.Exists(full) || Directory.Exists(full);
        bool isDir = Directory.Exists(full);

        bool allow;
        string reason = "";
        if (!exists)
        {
            // Kein Ziel + keine Create-Absicht -> „not found", kein ACL-Deny.
            if (!request.WantsCreate)
            {
                allow = true;
            }
            else
            {
                string parent = ShareRelativePath.GetParent(normalized);
                allow = await _acl.HasAccessAsync(
                    user, share.Id, parent, true, FilePermission.CreateWriteData);
                if (!allow) reason = $"create denied: parent '{parent}' lacks CreateWriteData";
            }
        }
        else
        {
            allow = true;
            if (request.WantWrite &&
                !await _acl.HasAccessAsync(user, share.Id, normalized, isDir, FilePermission.CreateWriteData))
            {
                allow = false;
                reason = "write denied: CreateWriteData";
            }

            // Jeder nicht-reine-write-Open liest den Eintrag (auch delete-only).
            bool wantsRead = request.WantRead || !request.WantWrite;
            if (allow && wantsRead &&
                !await _acl.HasAccessAsync(user, share.Id, normalized, isDir, FilePermission.ListReadData))
            {
                allow = false;
                reason = "read denied: ListReadData";
            }
        }

        // ALLOW bleibt auf Debug (sonst flutet der readdir-Filter das Log); jede
        // ABLEHNUNG kommt auf Info MIT Grund -> so ist sofort sichtbar, ob (und
        // warum) die ACL einen Write blockt. Steht bei einem gescheiterten Write
        // KEINE DENY-Zeile hier, hat die ACL erlaubt -> die Ablehnung ist FS-seitig.
        if (allow)
            _logger.LogDebug(
                "AuthorizeOpen ALLOW: user={User} share={Share} path=[{Path}] r={R} w={W} c={C}",
                request.Username, request.Share, request.Path,
                request.WantRead, request.WantWrite, request.WantsCreate);
        else
            _logger.LogInformation(
                "AuthorizeOpen DENY: user={User} share={Share} path=[{Path}] r={R} w={W} c={C} — {Reason}",
                request.Username, request.Share, request.Path,
                request.WantRead, request.WantWrite, request.WantsCreate, reason);

        return new AuthorizeReply { Allow = allow, Reason = allow ? "" : reason };
    }

    private AuthorizeReply Deny(string reason)
    {
        _logger.LogInformation("Authz DENY: {Reason}", reason);
        return new AuthorizeReply { Allow = false, Reason = reason };
    }
}
