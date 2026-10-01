# WebDAV Server

Kaimo File Server exposes every enabled local share over WebDAV (RFC 4918, classes 1, 2 and 3) at
`/dav` on the Web container. WebDAV is a transport over `IFileService`, so ACLs, recycle bin,
versioning, ownership, the change log and read-only demo mode apply exactly as for the web UI, the
REST API and SMB.

Related: [REST API v1](rest-api.md) · [Security model](../architecture/security-model.md) ·
[WebDAV client setup](../operations/webdav-client-setup.md)

## Placement

WebDAV runs **in-process** as a routed branch of the Web host's ASP.NET Core pipeline. It shares
Kestrel's ports (8080/8443) and the live-reloadable HTTPS certificate from `HttpsCertificateProvider`,
and uses the same DI graph (`IFileServiceFactory`, `IShareRepository`, `IUserContextFactory`,
`ILoginService`). The protocol is implemented without a third-party WebDAV library.

| File (`src/Kaimo_File_Server.Web/Controllers/WebDav/`) | Responsibility |
|---|---|
| `WebDavController.cs` | One action per DAV method |
| `WebDavPathResolver.cs` | URL → (share, share-relative path); `Destination` header parsing |
| `WebDavPropFind.cs` | PROPFIND/PROPPATCH parsing and `207 Multi-Status` writing |
| `WebDavLockManager.cs` | In-memory lock table (singleton) |
| `WebDavIfHeader.cs` | RFC 4918 `If:` header parsing |
| `WebDavCopy.cs` | Recursive copy/move over `IFileService` |
| `WebDavOptions.cs` | Settings from `config_settings`, cached for five seconds |
| `WebDavBasicAuthenticationHandler.cs` | Basic authentication scheme |
| `WebDavEnabledMiddleware.cs` | `503 Service Unavailable` while the service is disabled |

## URL space

```
/dav/                          root collection: shares the caller may list
/dav/{shareName}/              share root
/dav/{shareName}/sub/file.txt  item inside the share
```

- Shares are addressed by name, so `\\host\Projects` corresponds to `https://host:8443/dav/Projects`.
  Share names are constrained by `SambaName.EnsureValidShareName` and are safe URL segments.
- The root collection lists enabled, non-hidden shares on which the caller passes `CanListAsync`.
  Hidden shares remain reachable by direct URL, as on SMB.
- Disabled or unknown shares answer `404`, never `403`.
- Paths are normalized with `ShareRelativePath.TryNormalizeStrict`; rejected paths answer `400`.
- Internal `.kaimo-*` entries are omitted and refused. `.RECYCLE_BIN` is visible and browsable.
- Only local shares are exposed; Cloud Access virtual shares are not part of the DAV namespace.

## Authentication

Two schemes are accepted, selected by the `Authorization` header:

- **Basic** (`WebDavBasicAuthenticationHandler`): delegates to `ILoginService`, so lockout and
  username-enumeration resistance are identical to the web login. Successful verifications are
  cached for 60 seconds in `IMemoryCache`, keyed by username plus an HMAC of the password, to avoid a
  BCrypt verification on every request of a directory walk. Failures are never cached.
- **Bearer**: the device-scoped JWT of the REST API, validated by `BearerTokenValidation`.

Rules:

- Basic over plain HTTP answers `426 Upgrade Required` unless `services.webdav.requireHttps` is off
  (for deployments that terminate TLS in a proxy; `X-Forwarded-Proto` is honored from trusted
  proxies only).
- The `401` challenge offers `WWW-Authenticate: Basic realm="Kaimo File Server"` only. The bearer
  handler suppresses its own challenge on `/dav`, because the Windows redirector aborts a mount
  that is offered a scheme it cannot satisfy.
- `OPTIONS` is anonymous; every other method requires authentication.

## Methods

| Method | Backed by | Notes |
|---|---|---|
| `OPTIONS` | – | `DAV: 1, 2, 3`, `MS-Author-Via: DAV`, `Allow`, `Accept-Ranges: bytes` |
| `PROPFIND` | `ListAsync`, `GetMetadataAsync`, `FilterReadablePathsAsync` | `Depth: 0` and `1`; `Depth: infinity` answers `403` (`propfind-finite-depth`) |
| `PROPPATCH` | `SetModifiedAtAsync` | Persists `Win32LastModifiedTime`; other `Win32*` properties are accepted and ignored |
| `GET` / `HEAD` | `ReadFileAsync`, `GetMetadataAsync` | Range requests supported |
| `PUT` | `WriteFileAsync` | Streams the body; honors `If-Match` / `If-None-Match` via `ItemTag`; request size limit lifted for this action only |
| `MKCOL` | `CreateDirectoryAsync` | `405` if the target exists, `409` if the parent is missing |
| `DELETE` | `DeleteFileAsync(path, user, share.IsRecycleEnabled)` | Per-share recycle bin applies |
| `MOVE` | `RenameAsync`, or copy + delete across shares | `Destination`, `Overwrite`; a foreign host in `Destination` answers `502` |
| `COPY` | `WebDavCopy` | Recursive read/write |
| `LOCK` / `UNLOCK` | `WebDavLockManager` | See below |

PROPFIND returns `resourcetype`, `displayname`, `getcontentlength`, `getcontenttype`,
`getlastmodified`, `creationdate`, `getetag`, `supportedlock` and `lockdiscovery`; unknown
properties are reported as `404` inside the multistatus. `getetag` is `ItemTag.For(metadata)`, the
same validator the REST API uses.

A directory listing is one `ListAsync` call plus one batched `FilterReadablePathsAsync`, without
per-entry queries.

Exceptions map uniformly: `UnauthorizedAccessException` → `403`, `FileNotFoundException` /
`DirectoryNotFoundException` / `KeyNotFoundException` → `404`, `InvalidOperationException` → `409`.

## Locking

- `WebDavLockManager` is a process-wide `ConcurrentDictionary` keyed by share ID and path, holding
  owner, token (`opaquelocktoken:{guid}`), depth and absolute expiry.
- Exclusive write locks only; shared locks are advertised as unsupported.
- Default timeout `services.webdav.lockTimeoutSeconds` (300 s), capped at 3,600 s, refreshed by a
  `LOCK` carrying the token in `If:`. Expired entries are swept lazily.
- Every mutating method evaluates the `If:` header and answers `423 Locked` on conflict.
- The lock table lives in Web process memory and applies to WebDAV only; SMB locks are held by
  `smbd` independently.

## Settings

| Key (`config_settings`) | Meaning | Default |
|---|---|---|
| `services.webdav.enabled` | Desired state | `false` |
| `services.webdav.status` | Reported status, written by the Web process | – |
| `services.webdav.requireHttps` | Refuse Basic on plain HTTP | `true` |
| `services.webdav.lockTimeoutSeconds` | Default lock duration | `300` |

Because WebDAV is in-process, there is no reconciler: `WebDavEnabledMiddleware` reads the enabled
flag with a five-second cache.

## Response hardening

- `GET` serves `application/octet-stream` with `X-Content-Type-Options: nosniff`, so stored HTML
  never renders against the application origin.
- DAV endpoints carry no antiforgery metadata; Basic and Bearer credentials are not ambient, so a
  browser cannot be induced into an authenticated cross-site DAV write.
- Failed Basic attempts go through the login throttle and the security monitor.
