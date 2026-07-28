# P0 – Critical Findings

[← Table of contents](README.md)

## 5. Critical findings

### P0-01: Connection-context allocation failure disables open authorization

> **Remediation status (2026-07-22): Implemented in source; native build/runtime verification pending.**
> The context is now allocated and populated before `SMB_VFS_NEXT_CONNECT`. An allocation failure returns `ENOMEM` without establishing the next VFS connection, and a downstream connect failure frees the prepared context. P0-01 introduced the build marker `2026-07-22a fail-closed connect context allocation`; the current marker is `2026-07-22d complete rename authorization` after P0-04.

**Original evidence (before remediation)**

- The original `kaimo_connect` allocated `kaimo_conn_ctx` with `malloc()` after the next connect succeeded.
- If that allocation failed, the function still returned a successful connect.
- `create_file` authorizes only when `ctx != NULL`.
- `readdir` filtering is also disabled when the context is absent.

**Impact**

Under memory pressure, a valid TREE_CONNECT can continue without per-file open authorization or directory filtering. Delete happens to fail closed because its hook rejects a missing context, but read/write/create do not.

**Implemented fix and remaining verification**

1. The connection data is allocated with zero-initialized `calloc()` before the next VFS connect and remains owned through the existing `SMB_VFS_HANDLE_SET_DATA`/`kaimo_free_data` lifetime contract.
2. Allocation failure is now fail-closed before a native share connection exists.
3. A failed downstream connect frees the prepared context.
4. Still required: native compilation against Samba 4.19.5, an OOM/fault-injection test, and live verification that the connection is rejected without leaving partial connection state.

**Acceptance criteria**

- A forced allocation failure never results in a usable share connection.
- A non-IPC share can never reach an authorization hook after connecting without a valid context.

### P0-02: Fixed-size path truncation can authorize a different object than Samba modifies

> **Remediation status (2026-07-22): Implemented in source; native boundary/runtime verification pending.**
> All request and reconstructed operation paths are now dynamically allocated, checked against the sidecar's explicit 8191-byte request limit, and canonicalized before authorization/event use. Delete, rename, and mkdir reject local allocation/size failures before their native mutation. P0-02 introduced `2026-07-22b exact dynamic paths`; the current marker is `2026-07-22d complete rename authorization` after P0-04.

**Original evidence (before remediation)**

- `kaimo_join_path()` writes into a caller-provided fixed buffer using `snprintf()` but returns no success/truncation status.
- Delete uses a 4096-byte joined path for the authorization RPC.
- The actual `SMB_VFS_NEXT_UNLINKAT()` receives the original `dirfsp` and `smb_fname`, not the truncated joined string.
- Similar silent truncation exists in listing paths and lifecycle event paths.

**Impact**

Samba uses `*at` operations specifically to support directory-handle-relative paths. A deeply nested path can be operable through `dirfsp` even when a reconstructed full string exceeds a local buffer. The bridge may authorize the truncated prefix and Samba may then delete or otherwise act on the full path.

**Implemented fix and remaining verification**

1. `kaimo_join_path()` now returns an exact `talloc_strdup()`/`talloc_asprintf()` result instead of writing to an unchecked caller buffer.
2. CONNECT, OPEN, DELETEAUTH, SNAPRESOLVE, SNAPENUM, CLOSE, DELETE, RENAME, and MKDIR messages are dynamically formatted and sent with their exact computed byte length.
3. `KAIMO_AUTHD_MAX_REQUEST` documents and enforces the current sidecar read limit of 8191 bytes. Allocation and size failures set `ENOMEM`/`ENAMETOOLONG` and are never passed through the configurable infrastructure fail-open behavior.
4. Create, directory listing, close, delete, rename, mkdir, snapshot enumeration/resolution, and snapshot open paths now pass through the same share-relative canonicalization step. Connectpath stripping now requires a complete path-component boundary, preventing `/share` from incorrectly matching `/share-backup`.
5. DELETE, RENAME, and MKDIR lifecycle requests are built and validated before `SMB_VFS_NEXT_UNLINKAT`, `SMB_VFS_NEXT_RENAMEAT`, or `SMB_VFS_NEXT_MKDIRAT`. A locally unrepresentable path therefore cannot reach those native mutations.
6. Snapshot resolver response copying detects overflow, and the absolute snapshot cache path used by `openat()` is now dynamically allocated.
7. CLOSE preserves and validates its exact path/request before calling the native close. Native close still proceeds if that best-effort event cannot be allocated, because refusing to close would leak a descriptor; durable lifecycle delivery remains tracked separately under P1-11/P1-12.
8. Still required: compile against Samba 4.19.5 and execute deep-path, exact-boundary, over-boundary, multi-byte UTF-8, and common-prefix attack tests. A future protocol revision should replace reconstructed path identity with a stable Samba handle/inode identity where the end-to-end API can support it.

**Acceptance criteria**

- Paths larger than the configured protocol limit are rejected before native mutation.
- The exact bytes authorized are the exact bytes used for the native operation.
- Tests cover deep paths, boundary lengths, UTF-8 multi-byte names, and a path whose first 4095 bytes match an allowed path.

### P0-03: The Samba access mask is not mapped to the complete Kaimo permission model

> **Remediation status (2026-07-22): Implemented in source; managed tests verified; native build/runtime verification pending.**
> The complete raw SMB desired-access mask now crosses the VFS/sidecar/gRPC boundary. The bridge expands generic rights, maps every supported specific right, resolves `MAXIMUM_ALLOWED` to an attenuated mask, and returns that exact mask to the VFS before Samba continues. P0-03 introduced `2026-07-22c complete access masks`; the current module build marker is `2026-07-22d complete rename authorization`.

**Original evidence (before remediation)**

`create_file` currently maps only:

- `SEC_FILE_READ_DATA` to a read request.
- `SEC_FILE_WRITE_DATA | SEC_FILE_APPEND_DATA` to one write request.
- `SEC_STD_DELETE` to a delete request.

The Kaimo model also defines:

- `TraverseExecute`
- `ReadAttributes`
- `ReadExtAttributes`
- `ReadPermissions`
- `CreateAppendData`
- `WriteAttributes`
- `WriteExtAttributes`
- `DeleteSubItems`
- `ChangePermissions`
- `TakeOwnership`

**Impact**

- A user with append-only permission is checked as if full write permission were required.
- A read-authorized user may reach metadata mutation paths that require WriteAttributes or WriteExtAttributes.
- Permission and ownership changes are not evaluated against ChangePermissions/TakeOwnership.
- Traversal semantics are not explicitly enforced.
- The documentation's claim of exact ACL parity is therefore too broad.

**Implemented fix and remaining verification**

The former read/write/create/delete booleans have been removed from the wire contract. Their protobuf field numbers are reserved so mixed old/new peers cannot silently reinterpret a request. `AuthorizeOpenRequest` now contains the raw 32-bit access mask plus create-target type and directory-listing context; `AuthorizeReply` contains the exact specific mask that may be granted.

Recommended conceptual mapping:

| Samba access | Kaimo permission |
|---|---|
| Read data / list directory | `ListReadData` |
| Write data / add file | `CreateWriteData` |
| Append data / add subdirectory | `CreateAppendData` |
| Execute/traverse | `TraverseExecute` |
| Read attributes | `ReadAttributes` |
| Write attributes | `WriteAttributes` |
| Read EA | `ReadExtAttributes` |
| Write EA | `WriteExtAttributes` |
| Read control/security descriptor | `ReadPermissions` |
| Write DAC | `ChangePermissions` |
| Write owner | `TakeOwnership` |
| Delete target | `Delete` |
| Delete child from directory | `DeleteSubItems` |

Additional implemented behavior:

1. Generic read/write/execute/all bits are expanded to Samba 4.19.5's exact file generic masks.
2. `MAXIMUM_ALLOWED` is evaluated permission by permission and returned as a reduced specific mask. The VFS replaces the original request with this mask before `SMB_VFS_NEXT_CREATE_FILE`, preventing Samba's broad POSIX identity from restoring Kaimo-denied rights.
3. `SYNCHRONIZE` is preserved/ignored as allowed by SMB semantics. `ACCESS_SYSTEM_SECURITY`, reserved bits, malformed masks, malformed ALLOW responses, and granted masks outside Samba's specific-access domain are fail-closed.
4. Every ancestor directory is checked for `TraverseExecute` before target authorization.
5. Missing file creation requires `CreateWriteData` on the parent; missing directory creation requires `CreateAppendData` on the parent.
6. Samba 4.19.5 unconditionally adds `FILE_READ_ATTRIBUTES` to successful FSP opens. The bridge therefore requires `ReadAttributes` for real opens even when the client omitted the bit. The readdir visibility check is explicitly marked as listing-only and does not invent this FSP right.
7. Delete-on-open retains Windows-compatible target `Delete` or parent `DeleteSubItems` semantics. `FILE_DELETE_CHILD` is accepted only for directories.
8. Timewarp opens now also pass through the complete access-mask mapping. P1-06 still requires a separate read-only snapshot enforcement and VFS-stack-safe redirect.

The mapping was checked against the project's exact pinned [Samba 4.19.5 source archive](https://download.samba.org/pub/samba/stable/samba-4.19.5.tar.gz), including `source3/smbd/smb2_create.c`, `source3/smbd/open.c`, and `libcli/security/security.h`, and against the [MS-SMB2 file access-mask definition](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-smb2/77b36d0f-6016-458a-a7a0-0f4a72ae1534). This confirmed that the custom VFS receives raw client access before Samba's generic/maximum resolution and that Samba later adds read-attributes access.

Still required: native C/C++ compilation, live SMB access-mask tests, and SET_INFO verification for attributes, EAs, DACL, and owner changes. P0-05 and P0-06 are implemented in source; live snapshot verification and P1-06 read-only/VFS-stack hardening remain.

### P0-04: Rename lacks complete source, destination, and overwrite authorization

> **Remediation status (2026-07-22): Implemented in source; managed tests verified; native build/runtime verification pending.**
> `AuthorizeRename` now covers source removal, destination-parent creation, and replacement deletion before the native operation. The VFS revalidates source and destination identities around the RPC. The current build marker is `2026-07-22d complete rename authorization`.

**Original evidence (before remediation)**

- `vfs_kaimo_bridge.c:633-656` invokes `SMB_VFS_NEXT_RENAMEAT()` without a rename authorization RPC.
- The Core `FileService` requires Delete on the source, CreateWriteData at the destination, and Delete on an overwritten destination.
- The protobuf contract has no `AuthorizeRename` method.

**Impact**

An SMB user can potentially rename or move content into a destination where Kaimo ACLs deny creation, or overwrite a target without the corresponding target permission. The broad shared POSIX group makes it unsafe to rely on filesystem permissions as a fallback.

**Required fix**

1. Add an authorization operation with source path, destination path, source type, destination existence/type, and replace intent.
2. Require Delete on the source or the explicitly selected source-parent semantics.
3. Require CreateWriteData/CreateAppendData or the chosen directory-create permission at the destination parent.
4. If replacing a target, require Delete on the target or DeleteSubItems on its parent.
5. Authorize before `SMB_VFS_NEXT_RENAMEAT()`.
6. Revalidate the destination at the native operation boundary as far as Samba's API permits to reduce TOCTOU exposure.

**Temporary containment**

If the complete authorization cannot be implemented immediately, return `EACCES` for rename rather than allowing an operation outside the modeled policy.

**Implemented fix and remaining verification**

1. Added `AuthorizeRenameRequest` with both canonical paths, source type, destination existence/type, and effective replacement intent.
2. The managed decision checks traversal for both paths, source `Delete` or source-parent `DeleteSubItems`, destination-parent `CreateWriteData`/`CreateAppendData`, and replacement-target `Delete` or parent `DeleteSubItems`.
3. The bridge cross-checks the native type/existence claims against its shared storage view.
4. The VFS obtains source/destination state relative to Samba's exact parent directory handles before authorization, then repeats `fstatat` immediately afterward. Appearance, disappearance, type change, or inode exchange returns `EAGAIN` without mutation.
5. The post-rename event now carries the real source directory type instead of hardcoding file.
6. Still required: native compilation in the pinned image and live SMB rename tests, including concurrent destination exchange. A minimal syscall race remains between the final revalidation and `renameat`; Samba 4.19.5 does not expose the original replace flag to this VFS hook.

### P0-05: Folder snapshot materialization bypasses per-file ACL filtering

> **Remediation status (2026-07-22): Implemented and managed-tested; live SMB verification pending.**
> Folder enumeration/materialization now goes through `IFileService`, checks directories with `isDirectory: true`, filters every historical child, and serves a reconciled per-user cache projection.

**Original evidence (before remediation)**

- `SnapshotGrpcService.ResolveVersion()` calls `_versions.GetFolderSnapshotAsync()` directly.
- It materializes every returned `FileVersion` under the requested directory.
- `FileService.GetFolderSnapshotAsync()` already contains the missing behavior: it filters every returned file through the user's effective read ACL.

**Impact**

A user who may list a directory but has an explicit read deny on a child file can cause that child's historical content to be materialized. This violates the intended per-file ACL semantics and can disclose version content.

**Required fix**

1. Route folder snapshot retrieval through an ACL-aware service method.
2. Check `ListReadData` for every file returned by the folder snapshot.
3. Check directory ACLs with `isDirectory: true`, not `false`.
4. Never place an unreadable file in a cache namespace reachable by the requesting SMB session.
5. Test inherited allow plus explicit child deny, child allow plus parent restrictions, and ACL revocation after prior materialization.

**Implemented fix and remaining verification**

1. `EnumerateSnapshots` treats paths with exact file versions as files; all other paths, including the share root, use `IFileService.GetFolderSnapshotTimestampsAsync` and its directory ACL check.
2. `ResolveVersion` routes historical directories through `IFileService.GetFolderSnapshotAsync`, reusing its batch per-file `ListReadData` filter.
3. Materialized paths are partitioned by immutable share and user ids under `<cache-root>/<share-id>/<@GMT>/<user-id>/...`; one user's folder projection is never returned to another user.
4. Before a folder cache path is returned, the bridge deletes files and empty directories in that user's projection that are absent from the freshly ACL-filtered snapshot. This covers ACL revocation after an earlier materialization.
5. Concrete historical files retain an explicit file (`isDirectory: false`) read check, while directory checks use `isDirectory: true` through the central service.
6. Invalid traversal paths and invalid version-record paths fail closed before lookup/materialization.
7. Still required: live Windows/smbclient tests for listing/opening folder snapshots with child denies and revocation. Cache isolation is now handled by P0-06; per-user namespacing remains the ACL-projection boundary inside that private cache.

### P0-06: Snapshot cache content is inside the SMB share and is only hidden, not access-protected

> **Remediation status (2026-07-22): Implemented in source and managed/structurally tested; native/live SMB verification pending.**
> Materialized content now lives under a global internal root outside all shares. The bridge and share synchronizer both reject overlap, and the VFS validates cache-root-relative redirects.

**Original evidence (before remediation)**

**Evidence**

- Materialized files live under `<share>/.kaimo-snapshots/...`.
- `readdir` skips names beginning with `.kaimo-`.
- No general open/delete/rename/mkdir rule rejects direct client access to the internal namespace.
- Snapshot files are opened directly through an absolute path during timewarp resolution.

**Impact**

Hiding a name from directory enumeration is not access control. A client that knows or guesses a token/path can request the internal path directly. Cached content also survives ACL changes until cleanup.

**Required fix**

Preferred design:

- Store materialized content outside the client-visible share namespace and expose it only through an internal VFS redirect that preserves Samba's path-safety requirements.

If the cache must remain inside the share:

1. Reserve the exact `.kaimo-snapshots` root.
2. Reject all client-originated access to this root from every relevant VFS hook.
3. Distinguish an internal snapshot redirect from a client path without trusting a filename convention alone.
4. Open internal content through the VFS stack, not by bypassing `SMB_VFS_NEXT_OPENAT`.
5. Use restrictive ownership/mode and prevent SMB writes, deletes, renames, links, and metadata changes.

**Implemented fix and remaining verification**

1. Added `Snapshots:Cache:RootPath` / `KAIMO_SNAPSHOT_CACHE_ROOT`, defaulting to `/data/storage/.kaimo-snapshots`, a sibling of the normal `/data/storage/<share>` roots rather than a child of any share.
2. Changed the cache layout to `<cache-root>/<share-id>/<@GMT>/<user-id>/<relative-path>` and changed `ResolveVersionReply.cache_path` to a cache-root-relative path.
3. The bridge canonicalizes the configured root, verifies it does not overlap the requested share in either direction, validates every projected path, and fails closed on overlap.
4. The VFS accepts only non-empty relative redirect paths without empty, `.` or `..` components, then joins them to its independently configured absolute root. It no longer prefixes responses with the share connectpath.
5. `sync-shares.sh` canonicalizes all desired paths and refuses to publish a share that contains the cache, equals it, or is contained by it. Such a share is also removed from Samba registry desired state.
6. The legacy top-level `.kaimo-snapshots` name remains reserved and is rejected by create/open/stat/lstat/list/delete/rename/mkdir and snapshot-enumeration paths. The cleanup service removes recognizable legacy cache trees and orphan external share projections.
7. Cache directories/files use `0750`/`0640` on Unix for the bridge owner and shared Samba storage group.
8. Still required: compile against Samba 4.19.5 and run direct-path, overlap, timewarp browse/copy, rolling-upgrade legacy-cache, and multi-user tests. P1-06 remains open for strictly read-only timewarp flags and redirecting through the next VFS layer instead of raw `openat`.

### P0-07: The gRPC control plane is unauthenticated and exposes NT hashes

> **Remediation status (2026-07-23): Implemented; managed tests, native
> Samba-image build, and focused mTLS runtime checks verified. Full live SMB
> regression remains pending.**
> Compose now isolates the bridge on dedicated SMB-control and bridge-only database
> networks. Kestrel requires a client certificate chaining to a private CA; all
> C++ clients use TLS credentials. They share one `kaimo-samba` workload
> certificate because all helpers run in the same container and can read the
> same credential mount. The bridge enforces an explicit RPC method allow-list;
> `GetNtHash` is not allowed.
> Bulk `ListUsers` export is fixed-window rate-limited and emits request,
> completion, and rejection audit events without logging hashes or usernames.

**Evidence**

- The bridge listens on all interfaces on port 5080 using plaintext HTTP/2.
- C++ clients use `grpc::InsecureChannelCredentials()`.
- `AuthService.ListUsers` exports active usernames and raw 16-byte NT hashes.
- The bridge is on the default Compose network with other application services.

**Impact**

A compromised peer container can retrieve NT hashes for offline cracking/pass-the-hash scenarios, invoke authorization using arbitrary claimed identities, trigger lifecycle events, enumerate snapshots, and materialize version content.

**Required fix**

1. Place Samba and the bridge on a dedicated control network; attach the bridge separately to the database network.
2. Require mutually authenticated TLS or another workload-identity mechanism.
3. Authorize individual RPC groups so the sync client cannot automatically invoke snapshot/event operations.
4. Rate-limit and audit hash export.
5. Consider replacing bulk raw-hash export with an on-demand Samba passdb integration or a narrower one-time synchronization credential.
6. Ensure secrets cannot be retrieved by Web, Adminer, or unrelated containers.
