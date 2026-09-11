# WebDAV Service — Implementation Plan

> **Status:** Phases 0–4 implemented (`src/Kaimo_File_Server.Web/Controllers/WebDav/`); Phase 5 optional, not built
> **Scope:** A WebDAV *server* the product provides, alongside SMB — not an outbound connection to a foreign WebDAV host.
> **Base path:** `/dav` on the existing Web container
> **Transport:** RFC 4918 (class 1, 2, 3) over the existing HTTPS endpoint
> **Auth:** HTTP Basic (over TLS) plus the existing JWT bearer

The goal is a second first-class file transport with the same reach as SMB: every
share, every ACL, the recycle bin, versioning side effects, search indexing and the
read-only demo mode all keep working, because WebDAV is only a new transport on top
of the existing [`IFileService`](../src/Kaimo_File_Server.Core/Services/File/IFileService.cs).
Nothing in Core changes.

## Contents
1. [Where the service runs](#1-where-the-service-runs)
2. [URL space](#2-url-space)
3. [Authentication](#3-authentication)
4. [Method surface](#4-method-surface)
5. [Feature parity with what we already have](#5-feature-parity-with-what-we-already-have)
6. [Locking](#6-locking)
7. [Service toggle and settings UI](#7-service-toggle-and-settings-ui)
8. [Client compatibility](#8-client-compatibility)
9. [Security](#9-security)
10. [Phases](#10-phases)
11. [Tests](#11-tests)
12. [Deliberate omissions](#12-deliberate-omissions)

---

## 1. Where the service runs

**Decision: inside the existing Web container, as a routed branch of the ASP.NET Core
pipeline (`/dav`).**

SMB needs its own process because SMB is not HTTP: the protocol layer is `smbd` in the
`kaimo_samba` container, driven over a gRPC control plane by `Kaimo_File_Server.SmbBridge`.
WebDAV *is* HTTP, and the Web container already terminates TLS with the live-reloadable
certificate from `HttpsCertificateProvider`, already has the DI graph (`IFileServiceFactory`,
`IShareRepository`, `IUserContextFactory`, `ILoginService`), and already publishes ports
8080/8443. Re-hosting any of that in a second process buys nothing.

Alternatives considered and rejected:

- **Own Kestrel in `Kaimo_File_Server.Host`.** The Host is a generic worker host without
  ASP.NET Core. Adding it would duplicate the TLS certificate wiring and add a port to
  publish, with no isolation benefit that matters here.
- **A third-party WebDAV library (NWebDav and similar).** Every one of them wants to own
  its own store, principal and locking abstraction. Our store is `IFileService` with an
  ACL model, a recycle bin, versioning hooks and snapshot access that none of those
  abstractions express, so the adapter would be about as large as the protocol code
  itself — with a dependency we do not control on top. The protocol surface we need is
  bounded and well specified; we write it.

The implementation stays DI-only (no static state, no `HttpContext` reaching into Core),
so moving it into a dedicated ASP.NET host later is a project-file change, not a rewrite.

**New files** (all under `src/Kaimo_File_Server.Web/Controllers/WebDav/`):

| File | Responsibility |
|---|---|
| `WebDavController.cs` | Routing for all DAV methods, one action per method |
| `WebDavPathResolver.cs` | URL to (share, share-relative path); `Destination` header parsing |
| `WebDavPropFind.cs` | PROPFIND/PROPPATCH request parsing and `207` multistatus writing |
| `WebDavLockManager.cs` | In-memory lock table (singleton) |
| `WebDavIfHeader.cs` | RFC 4918 `If:` header parsing (lock-token preconditions) |
| `WebDavCopy.cs` | Recursive copy/move helper over `IFileService` |
| `WebDavOptions.cs` | Config-backed options (enabled, HTTPS requirement, lock timeout) |

## 2. URL space

```
/dav/                          root collection, lists the shares the caller may list
/dav/{shareName}/              share root
/dav/{shareName}/sub/file.txt  item inside the share
```

Shares are addressed by **name**, not id, so a mapped drive reads like its SMB
counterpart (`\\host\Projekte` becomes `https://host:8443/dav/Projekte`). Share names are
already constrained by `SambaName.EnsureValidShareName`, so they are safe URL segments.

Resolution rules, mirroring `BrowseApiController.ResolveAsync`:

- The root collection lists shares that are enabled, not `IsShareHidden`, and pass
  `CanListAsync(string.Empty, user)`. Hidden shares stay reachable by direct URL —
  identical to the SMB semantics.
- A disabled or unknown share is `404`, never `403`, so the URL space leaks nothing.
- Path segments are normalized through `ShareRelativePath.TryNormalizeStrict`; anything
  it rejects is `400`. Traversal never reaches the storage layer.
- Entries classified by `ShareEntryPolicy` as internal (`.kaimo-*`) are omitted from
  listings and refused on access. The recycle bin (`.RECYCLE_BIN`) stays visible, as it
  is in the file browser.

## 3. Authentication

Two schemes are accepted on `/dav`, selected by the `Authorization` header:

- **Basic** — a new `WebDavBasicAuthenticationHandler` registered as scheme
  `"webdav-basic"`. It delegates to `ILoginService.AuthenticateAsync`, so the existing
  brute-force throttle (`ILoginThrottle`) and username-enumeration resistance apply
  unchanged. This is the only scheme desktop clients can use.
- **Bearer** — the existing JWT handler, so the mobile and desktop clients can reach
  `/dav` with the token they already hold.

Endpoints are annotated with both scheme names in a single `[Authorize]` attribute.

Two details that matter:

- **BCrypt cost.** A single Explorer directory open issues dozens of requests, and each
  Basic request would otherwise re-run a BCrypt verification of roughly 100 ms. The
  handler keeps a short-lived positive-result cache in `IMemoryCache`, keyed by the
  username plus an HMAC of the password, with a 60-second TTL. Failures are never
  cached, so the throttle still sees every bad attempt.
  *Ceiling: a password change or a disabled account takes effect after at most 60 seconds
  on WebDAV. Lower the TTL or add an explicit invalidation on user save if that is not
  acceptable.*
- **Basic requires TLS.** The handler refuses Basic on a plain-HTTP request and answers
  `426 Upgrade Required`, unless `services.webdav.requireHttps` is turned off for a
  deployment that terminates TLS in front. Honoring `X-Forwarded-Proto` needs
  `UseForwardedHeaders` in `Program.cs`, which is not configured today and is part of
  Phase 0.

The `401` challenge carries `WWW-Authenticate: Basic realm="Kaimo File Server"` only. It
must never offer `Negotiate` — the Windows redirector aborts the mapping when it sees a
scheme it cannot satisfy. `OPTIONS` is allowed anonymously because it returns capability
headers only; everything else requires authentication.

## 4. Method surface

| Method | Backed by | Notes |
|---|---|---|
| `OPTIONS` | — | `DAV: 1, 2, 3`, `MS-Author-Via: DAV`, `Allow`, `Accept-Ranges: bytes` |
| `PROPFIND` | `ListAsync`, `GetMetadataAsync`, `FilterReadablePathsAsync` | `Depth: 0` and `1`; `Depth: infinity` answers `403 propfind-finite-depth` |
| `PROPPATCH` | `SetModifiedAtAsync` | Persists `Win32LastModifiedTime`; other Win32 properties are accepted and ignored |
| `GET` / `HEAD` | `ReadFileAsync`, `GetMetadataAsync` | Range requests via `File(..., enableRangeProcessing: true)`, as in `BrowseApiController.Download` |
| `PUT` | `WriteFileAsync` | Streams the request body; honors `If-Match` and `If-None-Match` through `ItemTag` |
| `MKCOL` | `CreateDirectoryAsync` | `405` when the target exists, `409` when the parent does not |
| `DELETE` | `DeleteFileAsync(path, user, share.IsRecycleEnabled)` | Per-share recycle bin applies automatically |
| `MOVE` | `RenameAsync`, or copy plus delete across shares | `Destination` and `Overwrite` headers |
| `COPY` | `WebDavCopy` (recursive read/write) | `IFileService` has no copy primitive; the recursion lives in the DAV layer |
| `LOCK` / `UNLOCK` | `WebDavLockManager` | See [section 6](#6-locking) |

Properties returned by PROPFIND: `resourcetype`, `displayname`, `getcontentlength`,
`getcontenttype`, `getlastmodified`, `creationdate`, `getetag`, `supportedlock`,
`lockdiscovery`. The ETag reuses `ItemTag.For(metadata)`, so a WebDAV client and a REST
client see the same validator for the same file. Unknown properties are reported as
`404` inside the multistatus, which is what the specification requires and what clients
expect.

Exception mapping follows `BrowseApiController.GuardAsync`: `UnauthorizedAccessException`
becomes `403`, `FileNotFoundException`, `DirectoryNotFoundException` and
`KeyNotFoundException` become `404`, `InvalidOperationException` becomes `409`.

Listing a directory is one `ListAsync` call plus one `FilterReadablePathsAsync` batch —
the same N+1-free path SMB's QueryDirectory uses.

## 5. Feature parity with what we already have

| Existing capability | How WebDAV exposes it |
|---|---|
| Per-item ACLs | Enforced inside `IFileService`; the DAV layer never checks permissions itself |
| Hidden and disabled shares | Hidden shares omitted from the root listing but reachable directly; disabled shares are `404` |
| Recycle bin | `DELETE` passes `share.IsRecycleEnabled`; the bin itself is browsable at `/dav/{share}/.RECYCLE_BIN/` |
| Versioning | Automatic — every `WriteFileAsync` snapshots a version exactly as the web UI does |
| Search indexing | Automatic — the same `FileService` write hooks index the file |
| Ownership stamping | Automatic — writes carry the authenticated `UserContext` |
| Change feed for client sync | Automatic — DAV writes land in the same `IFileChangeLog` |
| Read-only demo mode | Automatic — the EF interceptor and `ReadOnlyDemoAclService` turn writes into `403` |
| Departments, roles, groups | Automatic — `UserContext` is built by the same `IUserContextFactory` |
| Snapshots and previous versions | Not in Phase 1 to 4; optional Phase 5 below |
| Full-text search | Not exposed; SEARCH/DASL is out of scope, the web UI and REST API keep it |
| Cloud and virtual shares | Not in v1, matching `/api/v1/browse`, which is also local-shares-only |

**Optional Phase 5 — snapshots.** `GetFolderSnapshotTimestampsAsync`,
`GetFolderSnapshotAsync` and `OpenSnapshotAsync` already back the Samba shadow-copy
support. A virtual collection `/dav/{share}/.snapshots/{timestamp}/` would expose the
same point-in-time trees read-only over WebDAV. Worth doing only if users ask for it, as
no WebDAV client renders it the way Explorer renders Previous Versions over SMB.

## 6. Locking

WebDAV class 2 is required in practice: Windows Explorer, Finder and Office all refuse
to write reliably to a class-1 server.

`WebDavLockManager` is a singleton over a `ConcurrentDictionary` keyed by share id plus
path, holding the owner, the token (`opaquelocktoken:{guid}`), the depth and an absolute
expiry. The default timeout is 300 seconds, capped at 3600, refreshed by a `LOCK` whose
`If` header carries the token. Expired entries are swept lazily on access. Exclusive
write locks only; shared locks are advertised as unsupported.

Every mutating method parses the `If:` header and rejects a conflicting lock with
`423 Locked`.

*Ceiling: the lock table is per-process and in-memory. It does not survive a Web restart
and does not coordinate with SMB locks held by `smbd`, which live in a different process
on a different protocol. A file open for writing over SMB can still be written over
WebDAV and the reverse — exactly the situation that exists today between SMB and the web
UI. Cross-protocol locking would need a shared lock authority behind the bridge and is
explicitly out of scope.*

## 7. Service toggle and settings UI

The same control surface as SMB, using the existing config-key convention in
`DataServiceKeys`:

| Key | Meaning | Default |
|---|---|---|
| `services.webdav.enabled` | Desired state | `false` (opt-in; SMB defaults to `true` for backward compatibility) |
| `services.webdav.status` | Reported status | written by the Web process |
| `services.webdav.requireHttps` | Refuse Basic on plain HTTP | `true` |
| `services.webdav.lockTimeoutSeconds` | Default lock duration | `300` |

Because the service runs in-process, there is no reconciler round trip: the middleware
reads the enabled flag through `IConfigRepository` with a five-second cache and answers
`503 Service Unavailable` while it is off, and `SettingsViewModel` writes the status key
directly when the toggle is saved. That is the one asymmetry with SMB, whose status is
written by `DataServiceReconciler` in the Host.

UI work, in `Components/Pages/Settings/Components/DataServiceSettings.razor` and
`Components/ViewModels/SettingsViewModel.cs`: a second service row named WebDAV with the
same toggle and status markup, a read-only hint showing the connect URL, and a checkbox
for the HTTPS requirement. New resource keys go into **both** `Resources.resx` and
`Resources.de.resx`:

`Web_Settings_Data_WebDavToggle`, `Web_Settings_Data_WebDavUrl`,
`Web_Settings_Data_WebDavRequireHttps`, `Web_Settings_WebDavEnabled`,
`Web_Settings_WebDavDisabled`.

The tab is already permission-guarded, so WebDAV inherits that guard and needs no new
management permission.

## 8. Client compatibility

| Client | Needs | Notes |
|---|---|---|
| Windows Explorer (`net use`, Map network drive) | Class 2, Basic over HTTPS | The WebClient service caps files at 50 MB by default (`FileSizeLimitInBytes`) and refuses Basic over plain HTTP unless `BasicAuthLevel` is 2. Both are documented registry values for the admin guide. |
| macOS Finder | Class 2 | Needs `OPTIONS` and `PROPFIND` on the root; creates `._*` and `.DS_Store` files |
| Microsoft Office | Class 2 | Uses LOCK plus PUT, then a `Win32*` PROPPATCH after writing |
| rclone, Cyberduck, WinSCP, Nextcloud-style mobile apps | Class 1 | Usable at the end of Phase 1 for reads and Phase 2 for writes |
| Linux `davfs2` and GVfs | Class 2 | |

An admin-facing connection guide (`docu/webdav/README.md`) ships with Phase 4, covering
the URL form, the two Windows registry values, and the TLS requirement.

## 9. Security

- Basic credentials only over TLS; `426` otherwise, configurable for a TLS-terminating
  proxy.
- `X-Content-Type-Options: nosniff` on every GET, as `BrowseApiController.Download` does.
  GET returns `application/octet-stream` rather than a sniffable type, so a stored HTML
  file can never execute against the app origin. Hosting `/dav` on a separate hostname is
  recommended in the admin guide but not required.
- No antiforgery interaction: DAV endpoints are controller endpoints without antiforgery
  metadata, and Basic and Bearer auth are not ambient, so a browser cannot be tricked
  into an authenticated cross-site DAV write.
- Failed Basic attempts flow through `ILoginThrottle` and are logged with the existing
  `LogEvents` and `LogMessages` entries; new messages go into
  `Core/Logging/LogMessages.resx`.
- `Kestrel.Limits.MaxRequestBodySize` defaults to 30 MB and would truncate uploads. It is
  lifted for the DAV branch only, through `[DisableRequestSizeLimit]` on `PUT`, not
  globally. `MinRequestBodyDataRate` needs relaxing for large uploads over slow links.

## 10. Phases

Each phase is independently shippable and leaves the product working.

**Phase 0 — groundwork (about half a day).**
`UseForwardedHeaders`, the request-size limit exception, `WebDavOptions` and the config
keys, the enabled-flag middleware returning `503`, and the route branch. No protocol yet.

**Phase 1 — read-only, class 1 (about two days).**
`OPTIONS`, `PROPFIND` at depth 0 and 1, `GET` and `HEAD` with ranges, the root share
listing, and the Basic handler with its credential cache. Deliverable: rclone and
Cyberduck browse and download with correct ACL filtering.

**Phase 2 — writes (about two days).**
`PUT`, `MKCOL`, `DELETE`, `MOVE`, `COPY`, `PROPPATCH`. Deliverable: full read/write for
the class-1 clients, with recycle bin, versioning and indexing verified on DAV writes.

**Phase 3 — locking, class 2 (about one and a half days).**
`LOCK` and `UNLOCK`, `If:` header parsing, `423` on conflicts, `lockdiscovery`.
Deliverable: Windows Explorer and Finder mount read/write, and Office saves in place.

**Phase 4 — operations (about one day).**
The settings row, view-model wiring, both resx files, log messages,
`docu/webdav/README.md`, and `tests/manual_testing/webdav_tester.py` alongside the
existing SMB testers.

**Phase 5 — optional.**
The snapshot collection from section 5, a per-share WebDAV opt-in flag, and
`quota-used-bytes` reporting. None of these is built until someone asks.

## 11. Tests

Unit tests in `tests/Kaimo_File_Server.Tests`, following the pure-unit style of
`ClientSyncApiTests`:

- URL to share-and-path mapping, including percent-encoding, trailing slashes and
  traversal attempts.
- `Destination` header parsing: absolute and origin-relative forms, and a foreign host
  answering `502`.
- `If:` header parsing across the tagged and untagged list forms.
- Lock manager: acquisition, conflict, refresh, expiry, and unlock with a wrong token.
- PROPFIND XML: the shape for a file, a collection, a mixed depth-1 listing, and the
  `404` propstat for an unknown property.
- The Basic credential cache: a hit skips verification, a failure is never cached.

Behavioral verification runs in the container, since the Web app cannot run on Windows
because it reads `/proc/mounts`: `docker compose up`, then `webdav_tester.py` against
`https://localhost:8443/dav/`, plus one manual Explorer mount and one Finder mount.

## 12. Deliberate omissions

- **No DeltaV, no ACL protocol (RFC 3744), no SEARCH/DASL.** No client we care about
  uses them, and our ACL model does not map onto RFC 3744 principals.
- **No shared locks and no `Depth: infinity` PROPFIND.** Both are legal to refuse and
  both are cost without a consumer.
- **No quota properties in v1.** `GetDirectorySizeAsync` walks the tree, which is too
  expensive to run on every PROPFIND.
- **No per-share WebDAV flag.** SMB exposes every enabled share; WebDAV matches that
  until someone needs otherwise.
- **No second container or port.** Added only if WebDAV traffic ever needs to be isolated
  from the UI, which the DI-only design leaves open.
