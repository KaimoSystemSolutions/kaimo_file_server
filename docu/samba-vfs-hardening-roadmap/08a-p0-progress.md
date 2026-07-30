# Implementation Progress – P0

[← Progress overview](08-implementation-progress.md) · [Main table of contents](README.md)

### 2026-07-22 — P0-01: Fail-closed connection-context allocation

**Status:** Implemented in source; native build/runtime verification pending.

**Original problem**

`kaimo_connect` originally called `SMB_VFS_NEXT_CONNECT` first and allocated the mandatory `kaimo_conn_ctx` afterward. If `malloc()` failed, the function still returned a successful connection. Later `create_file` and `readdir` logic interpreted the missing context as a reason to skip authorization/filtering, creating an ACL fail-open condition under memory pressure.

**Solution implemented**

1. Declare the connection context before authorization/native connect processing.
2. Allocate it with zero-initialized `calloc()` before `SMB_VFS_NEXT_CONNECT` for every non-IPC share.
3. If allocation fails, log the failure, set `errno = ENOMEM`, and return `-1` before a native share connection exists.
4. Populate the username/share fields before calling the next VFS layer.
5. If the downstream `SMB_VFS_NEXT_CONNECT` fails, free the prepared context before propagating the failure.
6. After a successful downstream connect, attach the context through the existing `SMB_VFS_HANDLE_SET_DATA`/`kaimo_free_data` ownership contract.
7. Preserve the intentional `IPC$` bypass: IPC connections do not receive a file-authorization context because they are not ordinary filesystem shares.
8. Update the module build marker to `2026-07-22a fail-closed connect context allocation` so the deployed binary can be identified in logs.

This order was selected instead of connecting first and rolling back afterward because it prevents partial native connection state from existing at all when the mandatory security context cannot be created.

**Files changed**

- `src/samba-vfs/module/vfs_kaimo_bridge.c`
- `docu/samba-vfs-hardening-roadmap/08a-p0-progress.md`

**Validation completed**

- Verified against the pinned Samba 4.19.5 `SMB_VFS_HANDLE_SET_DATA` macro definition. The macro does not allocate memory and, for a valid handle, attaches the supplied pointer and free callback.
- Added and executed a structural source invariant check confirming that `calloc()` occurs before `SMB_VFS_NEXT_CONNECT`.
- The same check confirmed cleanup with `free(ctx)` after a downstream connect failure.
- `git diff --check` completed successfully.

**Validation still required**

- Compile the native module against the pinned Samba 4.19.5 source tree.
- Run an allocation-failure/fault-injection test proving that TREE_CONNECT fails and no usable share state remains.
- Run the live Samba connection test and verify the current `2026-07-22d` build marker (`2026-07-22a` was the marker when P0-01 alone was implemented).
- Run ASan/UBSan as part of the later native hardening test phase.

These checks could not be completed in the current audit environment because neither a Docker daemon nor an installed WSL distribution was available.

**Next planned finding:** P0-02 — eliminate fixed-size path truncation so the path authorized by the bridge is always identical to the path Samba modifies.

### 2026-07-22 — P0-02: Exact dynamic authorization and lifecycle paths

**Status:** Implemented in source; native build/runtime and boundary verification pending.

**Original problem**

The VFS reconstructed directory-handle-relative paths into fixed 4096-byte arrays and formatted sidecar messages into fixed 512-8192-byte arrays. Several `snprintf()` results were ignored or converted into configurable infrastructure failure handling. Most critically, delete authorization used the potentially truncated reconstruction while `SMB_VFS_NEXT_UNLINKAT` received the original `dirfsp` and `smb_fname`. A long path could therefore be authorized under one string while Samba mutated a different full path. Listing and lifecycle events had equivalent truncation or silent-drop behavior.

**Solution implemented**

1. Added `KAIMO_AUTHD_MAX_REQUEST = 8191`, matching the largest payload the current sidecar can consume in its 8192-byte read buffer while retaining the terminating NUL.
2. Added `kaimo_request_ready()`, which calculates the exact formatted length, rejects empty/oversized messages, sets `ENOMEM` or `ENAMETOOLONG`, and deliberately does not consult `KAIMO_AUTHZ_FAILOPEN`. Fail-open is now limited to genuine sidecar infrastructure failures for these paths.
3. Replaced fixed CONNECT, OPEN, DELETEAUTH, SNAPRESOLVE, and SNAPENUM request arrays with operation-scoped `talloc_stackframe()` plus `talloc_asprintf()` allocations. Every exit releases its frame.
4. Changed `kaimo_join_path()` from an unchecked `void` buffer writer into a dynamic string-returning function. Its callers explicitly reject allocation failure.
5. Applied `kaimo_share_rel()` consistently before authorization or lifecycle use in create, readdir, close, unlink, rename, mkdir, snapshot enumeration, snapshot resolution, and snapshot open handling.
6. Hardened `kaimo_share_rel()` so the connectpath is stripped only on a complete component boundary. For example, connectpath `/data/share` no longer rewrites `/data/share-backup/file` as `-backup/file`.
7. Reworked `readdir` to construct each candidate path dynamically. Allocation failure ends enumeration with `ENOMEM`; an oversized authorization request is denied/hidden and never exposes the entry through a truncated prefix decision.
8. Reworked `unlinkat` so the exact dynamic path is authorized and its DELETE event is formatted/validated before `SMB_VFS_NEXT_UNLINKAT`. Local allocation and size errors prevent the mutation; `ENOMEM`/`ENAMETOOLONG` are preserved rather than overwritten with `EACCES`.
9. Reworked `renameat` and `mkdirat` so exact canonical event paths are formatted and size-validated before their native operations. This change does not claim to solve P0-04: rename still requires a separate source/destination ACL decision before mutation.
10. Reworked CLOSE to copy and format the exact canonical event path before `SMB_VFS_NEXT_CLOSE`, because Samba may invalidate the FSP afterward. Close intentionally still proceeds if event preparation fails so that the native descriptor is not leaked; durable/retryable event delivery is a separate P1 remediation.
11. Replaced the fixed snapshot `openat` joined/absolute path buffers with dynamic strings. SNAPRESOLVE response copying now detects an oversized cache path and returns `ENAMETOOLONG` instead of opening a truncated path.
12. Updated the module build marker to `2026-07-22b exact dynamic paths`.

The original 8191-byte cap was an explicit compatibility boundary, not the
final protocol design. P1-03 subsequently replaced the one-read line protocol
with framed messages, full I/O loops, field limits, and explicit versioning.
Stable handle/inode-based object identity remains preferable to reconstructed
strings but requires a coordinated Samba-to-sidecar API change.

**Files changed**

- `src/samba-vfs/module/vfs_kaimo_bridge.c`
- `docu/samba-vfs-hardening-roadmap/08a-p0-progress.md`

**Validation completed**

- Ran a structural source invariant check. It confirmed that the old fixed `path`, `oldp`, `newp`, `joined`, and request-array patterns are absent.
- The same check confirmed an explicit request-size limit and request validation before `SMB_VFS_NEXT_UNLINKAT`, `SMB_VFS_NEXT_RENAMEAT`, and `SMB_VFS_NEXT_MKDIRAT`.
- Manually reviewed every new operation-scoped talloc frame and confirmed cleanup on success, denial, allocation/size error, sidecar failure, native-operation failure, and snapshot-resolution failure paths.
- Confirmed that local request allocation/size errors do not enter the `KAIMO_AUTHZ_FAILOPEN` branch.
- Confirmed the source contains the `2026-07-22b exact dynamic paths` deployment marker.
- `git diff --check` completed successfully.
- The existing .NET solution test suite completed with 477 passed, 0 failed, and 0 skipped. This protects the managed bridge baseline but does not compile or exercise the native VFS module.

**Validation still required**

- Compile the native module against the pinned Samba 4.19.5 source and headers with warnings enabled.
- Run ASan/UBSan and allocation fault injection over every new dynamic path and cleanup branch.
- Run live Samba tests with paths below, exactly at, and above the total 8191-byte sidecar request boundary.
- Test deep `dirfsp` paths, multi-byte UTF-8 names at byte boundaries, and two paths sharing the first 4095 bytes where only one is authorized.
- Verify OPEN/create, list, unlink, rmdir, mkdir, rename, close-event, snapshot enumeration, and snapshot open behavior against the exact same canonical target.
- Verify `ENOMEM`/`ENAMETOOLONG` mapping through Samba to SMB client-visible statuses.
- Verify the current `2026-07-22d complete rename authorization` marker in a running `smbd` log (`2026-07-22b` identifies the P0-02-only revision).

Native verification is currently unavailable: the Docker CLI is installed but its engine pipe is not running, no WSL distribution is installed, and no local GCC/Clang compiler is available. This is an environment limitation, not a passing native test result.

**Known related work intentionally not folded into P0-02**

- P0-03: complete Samba access-mask-to-Kaimo permission mapping (implemented in source after this entry; native verification remains pending).
- P0-04: implemented in source after this entry; native/runtime verification remains pending.
- P1-03: framed protocol and correct partial/EINTR I/O handling.
- P1-11/P1-12: durable, authenticated, idempotent lifecycle delivery.
- P2-01: replace the fixed 128-byte username/share fields in the connection context.

**Next planned finding:** P0-03 — map the complete Samba access mask to the Kaimo permission model and deny unsupported security-relevant operations.

### 2026-07-22 — P0-03: Complete SMB access-mask authorization and attenuation

**Status:** Implemented in source; managed mapping tests verified; native build/runtime verification pending.

**Original problem**

The native VFS reduced Samba's 32-bit desired-access mask to four booleans (`read`, `write`, `create`, and `delete`). This merged append with full write and omitted execute/traverse, attributes, extended attributes, security descriptor, owner, and directory-delete-child rights. Because Samba runs under a broad shared POSIX identity, any omitted Kaimo check could become an effective authorization bypass. The old contract also could not safely represent `GENERIC_*` or `MAXIMUM_ALLOWED`.

**Samba 4.19.5 behavior verified before implementation**

The exact source version pinned by `Dockerfile.vfs` was downloaded from the official Samba archive and inspected locally:

- `source3/smbd/smb2_create.c` passes the raw SMB2 `DesiredAccess` value into `SMB_VFS_CREATE_FILE`; the custom VFS therefore sees the mask before Samba normalizes it.
- `source3/smbd/open.c::smbd_calculate_access_mask_fsp` expands generic access and calculates `MAXIMUM_ALLOWED` only inside the next/default VFS implementation.
- The same file later adds `FILE_READ_ATTRIBUTES` to the FSP access mask even when the client omitted it.
- `libcli/security/security.h` confirms the exact generic masks and the file/directory bit aliases used by Samba 4.19.5.

This changed the implementation plan materially: a boolean allow/deny result is insufficient. The control plane must return an attenuated specific access mask and the VFS must pass that mask, not the original raw request, to Samba.

**Solution implemented**

1. Replaced the four boolean approximation in `AuthorizeOpenRequest` with `uint32 access_mask`, `wants_create`, `create_directory`, and `directory_listing` fields. Reserved protobuf field numbers 4-7 to prevent unsafe reuse of the old wire layout.
2. Added `granted_access_mask` to `AuthorizeReply`. CONNECT and DELETE leave it zero; OPEN returns the exact specific mask Samba may grant.
3. Changed the Unix-socket OPEN message to carry an eight-digit hexadecimal access mask and explicit create/directory/listing flags. The sidecar validates all field forms before issuing gRPC.
4. Changed the sidecar decision cache to store both allow/deny and the granted mask. A cached `MAXIMUM_ALLOWED` result therefore cannot accidentally become a full raw-mask grant.
5. Changed native ALLOW parsing to require exactly eight hexadecimal digits, reject bits outside Samba's `0x001F01FF` specific-access domain, and treat malformed protocol responses as fail-closed even when `KAIMO_AUTHZ_FAILOPEN=1`.
6. Replaced the VFS `access_mask` with `granted_access_mask` before every live and timewarp `SMB_VFS_NEXT_CREATE_FILE` call. This is the enforcement point that prevents the broad POSIX account from restoring rights removed by Kaimo.
7. Implemented exact Samba 4.19.5 generic expansion for `GENERIC_READ`, `GENERIC_WRITE`, `GENERIC_EXECUTE`, and `GENERIC_ALL`.
8. Implemented one-to-one mapping for all 13 Kaimo permission bits: data/list, write/add-file, append/add-subdirectory, execute/traverse, read/write attributes, read/write EA, read-control, write-DAC, write-owner, delete, and delete-child.
9. Implemented `MAXIMUM_ALLOWED` by evaluating each Kaimo permission and building only the allowed specific mask. `SYNCHRONIZE` is retained as the SMB-ignored synchronization bit.
10. Denied `ACCESS_SYSTEM_SECURITY`, reserved/unknown access bits, and `FILE_DELETE_CHILD` on non-directory objects because the Kaimo model cannot safely represent those requests.
11. Added explicit `TraverseExecute` checks for each ancestor directory before target access.
12. Distinguished missing file creation (`CreateWriteData` on the parent) from missing directory creation (`CreateAppendData` on the parent).
13. Required `ReadAttributes` for actual FSP opens because Samba 4.19.5 grants that bit implicitly. Kept readdir authorization as an explicit listing-only request so visibility still maps only to `ListReadData` plus ancestor traversal.
14. Preserved delete-on-open's target `Delete` or parent `DeleteSubItems` alternative.
15. Updated the deployment marker to `2026-07-22c complete access masks`.

**Files changed**

- `src/samba-vfs/protos/kaimo_smb_bridge.proto`
- `src/samba-vfs/module/authd.cpp`
- `src/samba-vfs/module/vfs_kaimo_bridge.c`
- `src/Kaimo_File_Server.SmbBridge/Services/AuthzGrpcService.cs`
- `tests/Kaimo_File_Server.Tests/AuthzGrpcServiceAccessMaskTests.cs`
- `tests/Kaimo_File_Server.Tests/AuthzGrpcServiceDeleteTests.cs`
- `src/samba-vfs/README.md`
- `docu/smb-samba-vfs-migration.md`
- `docu/samba-vfs-hardening-roadmap/08a-p0-progress.md`

**Validation completed**

- Compiled the managed gRPC service against the regenerated protobuf contract.
- Added focused tests for every supported specific access-bit mapping, append-only behavior, Delete/DeleteSubItems alternatives, directory-only delete-child, Generic Write expansion, `MAXIMUM_ALLOWED` attenuation, implicit read-attributes handling, file/directory parent-create semantics, listing behavior, ancestor traversal, `ACCESS_SYSTEM_SECURITY`, and unknown bits.
- Focused authorization suite: 29 passed, 0 failed, 0 skipped.
- Full solution suite: 501 passed, 0 failed, 0 skipped (24 new P0-03 tests increased the previous total from 477).
- Ran a structural contract check confirming the old boolean fields are absent from C/C++/C#, protobuf field numbers 4-7 are reserved, all 13 Kaimo permissions are mapped, the sidecar and VFS agree on the OPEN field count/format, and the granted mask is applied before the next VFS create call.
- Confirmed malformed OPEN ALLOW masks cannot enter the configurable fail-open path.
- Confirmed the P0-03 revision marker `2026-07-22c complete access masks`; it is superseded by the current P0-04 marker.
- `git diff --check` completed successfully.

**Validation still required**

- Compile `vfs_kaimo_bridge.c`, `authd.cpp`, and regenerated C++ protobuf/gRPC stubs inside the pinned Samba 4.19.5 image.
- Run native parser tests and ASan/UBSan/fuzz cases for malformed OPEN fields, short/long hexadecimal values, overflow, partial responses, invalid granted bits, and cache entries.
- Run live SMB tests for specific, generic, and `MAXIMUM_ALLOWED` requests and verify the FSP's effective mask is exactly the returned Kaimo mask.
- Exercise Windows/macOS/Linux client flows for append-only writes, directory creation, traversal denial, attribute/EA reads and writes, DACL changes, ownership changes, delete-on-close, and directory delete-child.
- Verify SET_INFO operations cannot bypass the opened handle's `WriteAttributes`, `WriteExtAttributes`, `ChangePermissions`, or `TakeOwnership` mask.
- Benchmark cold-cache authorization for deep paths and `MAXIMUM_ALLOWED`; the current correctness-first implementation can perform multiple ACL evaluations and must remain within the sidecar's five-second deadline.
- Verify timewarp opens receive the attenuated mask. P1-06 still must force them read-only and avoid raw VFS-stack bypass.
- P0-04/P0-05 were implemented after this entry; verify remaining mutating operations whose authorization is not fully determined by the create/open handle.

Native verification remains unavailable in this environment: Docker is installed but its engine is not running, WSL has no installed distribution, and no local C/C++ compiler is present. The successful managed build does not compile the native VFS or sidecar.

**Next planned finding:** P0-04 — authorize rename source, destination parent, and replacement target before native mutation.

### 2026-07-22 — P0-04: Complete rename authorization

**Status:** Implemented in source; managed authorization tests verified; native build/runtime verification pending.

**Solution implemented**

1. Added `AuthzService.AuthorizeRename` and a request carrying canonical source/destination paths, source type, destination existence/type, and effective replacement intent.
2. Added the `RENAMEAUTH` sidecar operation with strict boolean-field/count validation and a five-second gRPC deadline.
3. Added the managed decision for traversal on both paths, source `Delete`/parent `DeleteSubItems`, destination-parent `CreateWriteData` for files or `CreateAppendData` for directories, and replacement-target `Delete`/parent `DeleteSubItems`.
4. Cross-checked request existence/type data against the bridge's shared-storage view before evaluating ACLs.
5. Added relative `SMB_VFS_NEXT_FSTATAT` inspection against the same parent handles passed to `SMB_VFS_NEXT_RENAMEAT` before authorization and immediately afterward. Inode exchange, appearance/disappearance, and type changes fail closed with `EAGAIN`.
6. Moved authorization before the native rename and retained exact prevalidated post-event formatting. Directory rename events now carry the actual type.
7. Updated the deployment marker to `2026-07-22d complete rename authorization`.

**Files changed**

- `src/samba-vfs/protos/kaimo_smb_bridge.proto`
- `src/samba-vfs/module/authd.cpp`
- `src/samba-vfs/module/vfs_kaimo_bridge.c`
- `src/Kaimo_File_Server.SmbBridge/Services/AuthzGrpcService.cs`
- `tests/Kaimo_File_Server.Tests/AuthzGrpcServiceRenameTests.cs`
- `src/samba-vfs/README.md`
- `docu/smb-samba-vfs-migration.md`
- `docu/samba-vfs-hardening-roadmap/08a-p0-progress.md`

**Validation completed**

- Compiled the managed gRPC service against the regenerated protobuf contract.
- Added fourteen focused tests covering file and directory moves, source denial and source/replacement-parent deletion, destination-parent creation, replacement deletion, stale existence/type state, replacement-intent mismatch, and path traversal.
- Focused authorization suite: 43 passed, 0 failed, 0 skipped.
- Full solution suite: 515 passed, 0 failed, 0 skipped.
- Checked the native signatures and rename call flow against the exact Samba 4.19.5 source pinned by the image.

**Validation still required**

- Compile the VFS module, sidecar, and regenerated C++ protobuf/gRPC stubs in the pinned image.
- Run live SMB file/directory rename tests for same-parent moves, cross-directory moves, overwrite allow/deny, source-parent `DeleteSubItems`, destination-parent creation denial, and target type mismatches.
- Exercise a concurrent target-exchange test to confirm the pre/post-RPC identity check fails closed.
- The Docker CLI is installed, but its engine is not running in this environment; no native result is claimed.

**Next planned finding:** P0-05 — enforce per-file ACL filtering for folder snapshots.

### 2026-07-22 — P0-05: ACL-filtered per-user folder snapshots

**Status:** Implemented and managed-tested; live SMB verification pending.

**Solution implemented**

1. Injected `IFileServiceFactory` into `SnapshotGrpcService` and routed folder timestamp enumeration and point-in-time folder retrieval through the central `IFileService` policy boundary.
2. Kept concrete-file reads explicit with `isDirectory: false`; folder methods now enforce the folder itself with `isDirectory: true` and batch-filter all returned `FileVersion` children.
3. Changed materialization paths to a per-user subtree so one user's filtered projection is never returned to another user. P0-06 subsequently relocated that subtree under `<cache-root>/<share-id>/<@GMT>/<user-id>/...`.
4. Added projection reconciliation before returning a snapshot directory: files no longer present in the current ACL-filtered set are deleted and stale empty directories are pruned. This handles ACL revocation after prior materialization.
5. Added traversal rejection for client paths, validation for version-record paths, and cache-scope containment checks.
6. Preserved token-level cache markers and cleanup layout, so existing TTL and per-share size eviction continue to operate over the per-user subtrees.

**Files changed**

- `src/Kaimo_File_Server.SmbBridge/Services/SnapshotGrpcService.cs`
- `src/Kaimo_File_Server.SmbBridge/Services/SnapshotCache.cs`
- `src/Kaimo_File_Server.SmbBridge/Services/SnapshotCacheCleanupService.cs`
- `src/Kaimo_File_Server.SmbBridge/Program.cs`
- `tests/Kaimo_File_Server.Tests/FileServiceFolderSnapshotTests.cs`
- `tests/Kaimo_File_Server.Tests/SnapshotGrpcServiceAclTests.cs`
- `src/samba-vfs/README.md`
- `docu/samba-vfs-hardening-roadmap/08a-p0-progress.md`

**Validation completed**

- Added two central `FileService` tests for inherited folder allow with explicit child deny and child allow with restricted parent.
- Added five bridge tests for directory-aware enumeration, per-file materialization filtering, per-user isolation, ACL revocation cleanup, concrete-file denial, and traversal rejection.
- Focused P0-05 suite: 7 passed, 0 failed, 0 skipped.
- Full solution suite: 522 passed, 0 failed, 0 skipped.

**Validation still required**

- Run live Windows and `smbclient` folder snapshot browsing with explicit child denies and confirm denied names/content never appear.
- Materialize as an allowed user, revoke the child ACL, browse again, and confirm the stale child disappears.
- Browse the same token concurrently as users with different ACL sets and verify each receives only its own projection.
- P0-06 was completed immediately afterward by relocating the cache and reserving the legacy in-share namespace.

**Next planned finding:** P0-06 — isolate or comprehensively protect the in-share snapshot cache.

### 2026-07-22 — P0-06: Isolated snapshot cache

**Status:** Implemented; managed/structural checks and the pinned native build verified; live SMB snapshot verification pending.

**Solution implemented**

1. Relocated materialized content from `<share>/.kaimo-snapshots` to the global `/data/storage/.kaimo-snapshots/<share-id>/<@GMT>/<user-id>/...` root outside normal share connectpaths.
2. Added the matching `Snapshots:Cache:RootPath` and `KAIMO_SNAPSHOT_CACHE_ROOT` settings to bridge, Samba, Compose, and the example environment.
3. Made bridge resolution fail closed when canonical cache/share paths overlap in either direction and made `sync-shares.sh` reject the same unsafe definitions before publishing them to Samba.
4. Changed the bridge/VFS contract to return only a cache-root-relative path. The native module rejects absolute paths, backslashes, empty components, `.` and `..` before joining the local absolute root.
5. Permanently reserved the legacy `.kaimo-snapshots` client namespace in path-bearing VFS hooks, and added conservative legacy/orphan cleanup.
6. Applied Unix `0750` directory and `0640` file modes to external projections while retaining share-id and immutable user-id partitioning.
7. Updated the native build marker to `2026-07-22e isolated snapshot cache`.

**Files changed**

- `docker-compose.yml`
- `.env.example`
- `src/samba-vfs/protos/kaimo_smb_bridge.proto`
- `src/samba-vfs/module/vfs_kaimo_bridge.c`
- `src/samba-vfs/module/authd.cpp`
- `src/samba-vfs/sync-shares.sh`
- `src/Kaimo_File_Server.SmbBridge/Services/SnapshotGrpcService.cs`
- `src/Kaimo_File_Server.SmbBridge/Services/SnapshotCache.cs`
- `src/Kaimo_File_Server.SmbBridge/Services/SnapshotCacheCleanupService.cs`
- `src/Kaimo_File_Server.SmbBridge/appsettings.json`
- `tests/Kaimo_File_Server.Tests/SnapshotGrpcServiceAclTests.cs`
- `src/samba-vfs/README.md`
- `docu/smb-samba-vfs-migration.md`
- `docu/samba-vfs-hardening-roadmap/08a-p0-progress.md`

**Validation completed**

- Strengthened the folder projection test to prove the returned/cache-on-disk path is external to the share while retaining per-user revocation reconciliation.
- Added a fail-closed test for a configured cache root inside the SMB share.
- Focused snapshot ACL/isolation suite: 9 passed, 0 failed, 0 skipped.
- Full solution suite: 524 passed, 0 failed, 0 skipped.
- Structural checks confirm both Compose services use the same root, `sync-shares.sh` rejects overlap, no bridge materialization combines cache content with `share.Path`, the VFS rejects traversal responses, and ordinary client hooks reserve the legacy namespace.

**Validation still required**

- Run Windows/`smbclient` snapshot browse, copy, restore, direct `.kaimo-snapshots` access, cache/share-overlap, multi-user, and rolling-upgrade legacy-cache tests.
- P1-06 remains open: timewarp access still needs strict read-only flag enforcement and a stack-safe alternative to raw `openat`.

**Next planned finding:** P0-07 — authenticate and isolate the gRPC control plane and NT-hash export.

### 2026-07-23 — P0-07: Authenticated and isolated gRPC control plane

**Status:** Implemented; native build and focused mTLS runtime checks verified; full live SMB regression pending.

**Solution implemented**

1. Replaced every C++ `InsecureChannelCredentials` use with TLS credentials
   loaded from read-only certificate/key mounts.
2. Configured Kestrel for HTTP/2 over TLS with mandatory client certificates,
   custom-root trust, client-auth EKU validation, and fail-fast startup when
   certificate material is absent.
3. Added one `kaimo-samba` client certificate for the helpers that share the
   Samba container and credential mount. The bridge allows only the required
   sync, authorization, event, and snapshot RPCs. `GetNtHash` remains denied.
4. Added a non-queuing fixed-window limiter for bulk hash export (default two
   calls per 60 seconds) plus request/completion/rejection audit logging that
   excludes usernames and hash bytes.
5. Split Compose connectivity into an internal `kaimo_smb_control` network and
   a bridge-only `kaimo_bridge_database` network. PostgreSQL joins that segment
   plus the separate application DB segment; the bridge shares no network with
   Web, Adminer, or unrelated application containers.
6. Mounted only the server key and public client CA into the bridge, and only
   the Samba workload key/public CA into Samba. Web and Adminer receive none of
   the control-plane material.
7. Added a git-ignored local-PKI bootstrap script; production can supply
   externally managed certificates through `KAIMO_SMB_CONTROL_PKI`.
8. Updated the native build marker to
   `2026-07-23a authenticated control plane` and replaced the P0-06 reserved
   namespace's forbidden libc `strncasecmp` call with Samba's `strnequal`, as
   discovered by the pinned native build.

**Validation completed**

- Focused P0-07 managed suite: 3 passed, 0 failed, 0 skipped; full managed
  suite: 528 passed, 0 failed, 0 skipped.
- The pinned Samba 4.19.5 image builds successfully, including all mTLS C++
  clients using the shared Samba workload certificate and the VFS module.
- A local bridge started on TLS port 5080 with generated development
  credentials. Auth, share, config, and runtime clients completed their allowed
  RPCs with the shared workload identity. `GetNtHash` remained denied.
- Two immediate hash exports succeeded; the third returned
  `RESOURCE_EXHAUSTED`. Bridge audit logs recorded request, completion,
  unassigned-method rejection, and rate-limit rejection without usernames or
  hash bytes.
- `docker compose config --quiet` succeeds with the isolated network and
  credential mount topology. Resolved topology checks show the bridge shares
  zero networks with Web and Adminer, while retaining its Samba-control and
  bridge-only PostgreSQL paths.
- Structural search finds no remaining native
  `grpc::InsecureChannelCredentials()` usage.
- `git diff --check` reports no whitespace errors.

**Validation still required**

- Verify untrusted/missing-certificate handshake failure explicitly and test
  production-issued certificate material and rotation.
- Exercise live NTLM login, ACL, event, share/config sync, and snapshot paths
  over the authenticated channel.
- Define certificate issuance, rotation, revocation, and expiry monitoring for
  the production orchestrator. Bridge high availability remains separate work.

**Next planned finding:** P1-01 — bound sidecar threads and open Unix-socket clients.
