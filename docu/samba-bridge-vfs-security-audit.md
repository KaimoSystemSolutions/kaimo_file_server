# Samba Bridge and VFS Security, Memory-Safety, and Coverage Audit

> **Audit date:** 2026-07-22  
> **Scope:** `samba-vfs`, `Kaimo_File_Server.SmbBridge`, the relevant Core/Infrastructure lifecycle code, Samba synchronization scripts, container wiring, and the shared gRPC contract  
> **Audit type:** Static code and architecture review, supported by the available .NET test suite  
> **Repository state:** No production source files were changed as part of the audit  
> **Overall result:** Not ready for production as a security boundary without the P0/P1 remediation described below

## 1. Executive summary

The Samba migration is functionally substantial: all RPCs in the protobuf contract have server implementations, the native VFS module covers the main SMB data-path seams, and the bridge reuses the existing Kaimo repositories, ACL service, version service, ownership logic, and search lifecycle hooks.

However, the current implementation is **not yet complete or safe enough to act as the sole enforcement boundary for Kaimo ACLs**. The audit found several issues that can cause authorization bypasses, disclosure of historical file content, silent state divergence, unbounded resource consumption, or incorrect lifecycle records.

The most important conclusions are:

1. No obvious direct stack overflow, heap overflow, use-after-free, or double-free was found during the static review. This is not a formal proof of C/C++ memory safety.
2. The native implementation still has security-relevant memory/resource failures: allocation failure can disable open authorization, the sidecar has unbounded thread and cache growth, and blocking Unix-socket calls can stall `smbd` workers.
3. The Kaimo file-permission model is only partially mapped to Samba access masks. Rename and several metadata/security operations are not fully authorized.
4. Folder snapshot materialization bypasses the existing per-file ACL filter and places historical content inside the client-visible share namespace.
5. The local sidecar protocol is not safely framed and assumes one `read()`/`write()` is sufficient for a stream socket.
6. The gRPC bridge exposes NT hashes and privileged control-plane functions over unauthenticated h2c on the shared Docker network.
7. Event delivery and synchronization are best-effort rather than durable. Failures can leave version, ACL, metadata, ownership, search, passdb, registry, and runtime state inconsistent.

The system should be treated as a **working migration prototype with critical hardening work remaining**, not as a completed production security boundary.

## 2. Audit scope

### 2.1 Native Samba components

- `samba-vfs/module/vfs_kaimo_bridge.c`
- `samba-vfs/module/authd.cpp`
- `samba-vfs/module/authsync.cpp`
- `samba-vfs/module/sharesync.cpp`
- `samba-vfs/module/configsync.cpp`
- `samba-vfs/protos/kaimo_smb_bridge.proto`

### 2.2 Synchronization and container components

- `samba-vfs/sync-users.sh`
- `samba-vfs/sync-shares.sh`
- `samba-vfs/sync-config.sh`
- `samba-vfs/entrypoint.vfs.sh`
- `samba-vfs/conf/smb.conf.vfs`
- `samba-vfs/Dockerfile.vfs`
- `docker-compose.yml`

### 2.3 .NET bridge and lifecycle components

- `src/Kaimo_File_Server.SmbBridge/Program.cs`
- All services under `src/Kaimo_File_Server.SmbBridge/Services`
- `ShareRelativePath`
- `FileService` external lifecycle methods and authorization semantics
- `FileVersionService` and `FileVersionRepository` paths used by the bridge
- `AuthenticationLookup`, `ShareRepository`, and `UserRepository`
- `SambaSmbControlService`

### 2.4 Validation performed

- The complete .NET test project passed: **477/477 tests**.
- A focused set covering path helpers, GMT tokens, and delete authorization passed: **46/46 tests**.
- Only a small part of the Samba bridge currently has direct unit-test coverage. In particular, Open authorization, snapshots, events, synchronization framing, and the native module lack meaningful automated coverage.
- Native container compilation, ASan/UBSan, and live Samba tests could not be run in the audit environment because no Docker daemon or WSL distribution was available.

## 3. Threat model and security boundary

The following assumptions are necessary when evaluating severity:

- All synchronized Samba users are placed in a shared storage group.
- Share directories and new content are deliberately group-writable.
- POSIX permissions therefore provide coarse storage access, not the complete Kaimo authorization policy.
- The VFS bridge is the primary enforcement boundary for per-user Kaimo ACLs.
- A missed VFS operation or a fail-open error is consequently more serious than it would be in a deployment with restrictive per-user POSIX ACLs.
- The Samba and bridge containers share the storage mount.
- The bridge has access to the database, decrypted NT hashes, ACL decisions, version blobs, and the materialization cache.
- Other services currently share the default Compose network unless explicitly separated.

The review considers:

- Malicious or compromised SMB clients.
- Malicious filenames and deep directory structures.
- A compromised process inside the Samba container.
- A compromised container on the shared Docker network.
- Infrastructure failures and memory pressure.
- Concurrent SMB operations and retries.
- Corrupt, legacy, or manually modified database records.

## 4. Severity definitions

| Severity | Meaning |
|---|---|
| P0 / Critical | Can bypass ACLs, disclose protected content or credential material, or invalidate the security boundary. Blocks production. |
| P1 / High | Can cause durable inconsistency, memory/resource exhaustion, broad availability failure, or significant security degradation. |
| P2 / Medium | Correctness, resilience, observability, or defense-in-depth problem that should be fixed before broad deployment. |
| P3 / Low | Maintenance, documentation, or narrowly scoped operational improvement. |

## 5. Critical findings

### P0-01: Connection-context allocation failure disables open authorization

**Evidence**

- `vfs_kaimo_bridge.c:429-436` allocates `kaimo_conn_ctx` with `malloc()` after the next connect succeeds.
- If allocation fails, the function still returns a successful connect.
- `create_file` authorizes only when `ctx != NULL`.
- `readdir` filtering is also disabled when the context is absent.

**Impact**

Under memory pressure, a valid TREE_CONNECT can continue without per-file open authorization or directory filtering. Delete happens to fail closed because its hook rejects a missing context, but read/write/create do not.

**Required fix**

1. Allocate connection data with Samba-owned lifetime, preferably `talloc_zero(handle->conn, ...)` or the ownership pattern recommended for VFS handle data.
2. If the allocation or `SMB_VFS_HANDLE_SET_DATA` operation fails, disconnect/undo the next connection and return failure.
3. Make every authorization hook explicitly fail closed when the connection context is absent.
4. Add fault-injection tests that force allocation failure at connect and verify that every data operation is denied.

**Acceptance criteria**

- A forced allocation failure never results in a usable share connection.
- No authorization hook treats a missing context as permission to continue.

### P0-02: Fixed-size path truncation can authorize a different object than Samba modifies

**Evidence**

- `kaimo_join_path()` writes into a caller-provided fixed buffer using `snprintf()` but returns no success/truncation status.
- Delete uses a 4096-byte joined path for the authorization RPC.
- The actual `SMB_VFS_NEXT_UNLINKAT()` receives the original `dirfsp` and `smb_fname`, not the truncated joined string.
- Similar silent truncation exists in listing paths and lifecycle event paths.

**Impact**

Samba uses `*at` operations specifically to support directory-handle-relative paths. A deeply nested path can be operable through `dirfsp` even when a reconstructed full string exceeds a local buffer. The bridge may authorize the truncated prefix and Samba may then delete or otherwise act on the full path.

**Required fix**

1. Never authorize a truncated representation.
2. Replace fixed reconstruction buffers with checked `talloc_asprintf()`/dynamic strings or return `ENAMETOOLONG` before invoking the native operation.
3. Make the canonical path builder return an explicit status and length.
4. Use the same canonicalization function for create, open, readdir, close, delete, rename, mkdir, stat, lstat, and snapshot operations.
5. Prefer stable handle/inode identity over reconstructed path strings where Samba exposes it.

**Acceptance criteria**

- Paths larger than the configured protocol limit are rejected before native mutation.
- The exact bytes authorized are the exact bytes used for the native operation.
- Tests cover deep paths, boundary lengths, UTF-8 multi-byte names, and a path whose first 4095 bytes match an allowed path.

### P0-03: The Samba access mask is not mapped to the complete Kaimo permission model

**Evidence**

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

**Required fix**

Replace the current read/write/create/delete booleans with a complete operation/access request. At minimum the server must receive either the normalized Samba access mask or explicit requested Kaimo permission bits.

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

The final mapping must be verified against the exact Samba 4.19.5 access-mask state delivered to `create_file` and against all relevant SET_INFO/VFS paths.

### P0-04: Rename lacks complete source, destination, and overwrite authorization

**Evidence**

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

### P0-05: Folder snapshot materialization bypasses per-file ACL filtering

**Evidence**

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

### P0-06: Snapshot cache content is inside the SMB share and is only hidden, not access-protected

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

### P0-07: The gRPC control plane is unauthenticated and exposes NT hashes

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

## 6. High-severity findings

### P1-01: Unbounded detached threads allow memory and file-descriptor exhaustion

`authd` accepts each Unix-socket connection and starts a detached `std::thread`. The first `read()` has no receive timeout. A local client can open connections without sending data, consuming one thread, stack, and file descriptor per connection.

**Fix:** use a bounded worker pool or event loop, enforce connection and request deadlines, cap concurrent clients, and reject excess load predictably.

### P1-02: The authorization cache grows without a global bound

Expired cache entries are removed only when the exact key is requested again. Unique file paths therefore accumulate indefinitely even though the advertised TTL is three seconds.

**Fix:** implement a size-bounded LRU/clock cache, periodic expiry, metrics, and a hard maximum memory budget. ACL changes should also support explicit invalidation or a documented maximum revocation delay.

### P1-03: Unix stream framing and partial I/O are incorrect

The native module assumes one `write()` sends the entire request. The sidecar assumes one `read()` receives the entire request. Replies are also written once. Stream sockets do not preserve application messages and may return partial reads/writes.

The tab/newline protocol also has no escaping. User, share, path, token, or future fields containing delimiters change the parsed message.

**Fix:** use a length-prefixed binary envelope, `read_exact`/`write_all`, explicit maximum frame size, versioning, enum operation types, and validation before allocation.

### P1-04: The Unix socket is world-writable and trusts claimed identity

`chmod(..., 0666)` permits every local process to submit requests. The sidecar does not inspect peer credentials and accepts `username` from the payload.

**Fix:** mode `0660`, dedicated service group, private directory permissions, `SO_PEERCRED` verification, and a design where the peer cannot choose an arbitrary Kaimo identity independently of the authenticated Samba session.

### P1-05: Native calls can block `smbd` workers for infrastructure timeouts

Authorization and notification paths perform synchronous `connect()`/`write()`/`read()` calls without socket-level deadlines. gRPC deadlines in `authd` limit some downstream work, but a full backlog, stalled sidecar, or local socket failure can still block before the gRPC deadline applies.

**Fix:** nonblocking connect with poll/deadline, send/receive timeouts, strict end-to-end budgets, and separate authorization from asynchronous event delivery.

### P1-06: Snapshot opens bypass the VFS stack and do not enforce read-only access

The timewarp branch skips normal `AuthorizeOpen`, then calls raw `openat(AT_FDCWD, absolutePath, how->flags, how->mode)`. This can preserve write/truncate/create flags and bypass `full_audit` or later VFS modules.

**Fix:** reject all non-read access for timewarp paths, strip unsafe flags defensively, and redirect through the next VFS module using Samba-supported path/FSP handling. Verify behavior against Samba 4.19.5's own shadow-copy modules.

### P1-07: Snapshot materialization is non-atomic and only validates file size

The bridge writes directly to the final cache filename using `FileMode.Create`. Another reader can race with a partial write, and a crash can leave a same-sized corrupt file that is subsequently reused.

**Fix:** write to a unique temporary file on the same filesystem, stream with a byte cap, verify expected length and content hash, flush/fsync, apply safe mode/timestamps, and atomically rename into place.

### P1-08: Snapshot materialization and cleanup are unsynchronized

The background cleanup service can delete a token directory while another RPC is materializing or while Samba is traversing it. Unix open file descriptors may survive deletion, but directory traversal and later child opens can fail inconsistently.

**Fix:** keyed locks/leases per share+token, atomic directory publication, cleanup that skips active leases, and tests that interleave materialization, enumeration, open, and eviction.

### P1-09: Folder materialization is unbounded and ignores cancellation

A single folder snapshot request may decompress every historical file under a prefix. The per-share cap is enforced later by the background sweeper, not before or during materialization. gRPC cancellation tokens are not propagated.

**Fix:** request-level file/byte/time quotas, preflight capacity reservation, incremental/lazy materialization, cancellation propagation, concurrency limits, and cleanup of abandoned temporary output.

### P1-10: Disabled shares remain valid in bridge authorization

`ShareRepository.GetByNameAsync()` returns disabled definitions. Connect, Open, Delete, Event, and Snapshot services generally check only for `null`, not `IsEnabled`.

**Impact:** during the polling/reconciliation interval, new connections or internal RPC callers may continue to use a disabled share. Existing open handles are not revoked by per-open authorization.

**Fix:** centralize `ResolveEnabledShareAsync`, use it in every bridge service, immediately close the share on disable, and document/implement active-handle revocation semantics.

### P1-11: Event delivery is lossy and has no retry-safe contract

VFS notifications are described as fire-and-forget. The VFS does not wait for a meaningful acknowledgement, the sidecar does not durably spool events, and gRPC status/reply values are ignored by event handlers.

**Impact:** version creation, owner stamping, metadata cleanup, ACL path updates, and search indexing can be silently missed.

**Fix:** use a durable local outbox/spool, event IDs, explicit acknowledgements, bounded retry with backoff, dead-letter handling, and idempotent server-side processing.

### P1-12: Close processing can version the wrong content or assign the wrong user

`NotifyExternalCloseAsync` reopens the file by path after the SMB close. Between the native close and the bridge read, another client may modify, rename, replace, or delete the path. Concurrent handles make the attribution problem worse.

**Fix options:**

- Create an immutable staging copy/reflink at the native close boundary and send its identity to the bridge.
- Introduce a coordinated file-version transaction/lock shared by SMB and the bridge.
- Move the version snapshot into a component that can read the exact closing file descriptor before it is released.

Whichever option is chosen must preserve the data-path goals while guaranteeing that version bytes correspond to the reported close event.

### P1-13: Rename events are not idempotent and can delete version history

`FileVersionRepository.RenamePathAsync()` removes destination versions not present in the source set. If the same rename event is delivered twice, the second call has an empty source set and can treat all destination versions as displaced.

**Fix:** make rename lifecycle processing idempotent by event ID and state transition. A repeated already-applied old→new rename must be a no-op, never a destructive destination cleanup.

### P1-14: Directory rename events are reported as file renames

The VFS hardcodes `is_directory = 0` in rename notifications. ACL and version path-prefix updates happen independently of this flag, but search lifecycle behavior chooses the file rename callback instead of the directory rename callback.

**Fix:** determine object type before the native rename using the source FSP/stat data and send the correct value.

### P1-15: NT hashes are written to an insecure predictable temporary file

`sync-users.sh` writes `/tmp/kaimo.smbpasswd` without `mktemp`, an explicit restrictive umask, safe ownership verification, locking, or cleanup. The file contains reusable NT hashes.

**Fix:** avoid disk completely if `pdbedit` supports a safe pipe/import method. Otherwise use a private runtime directory or `mktemp`, `umask 077`, `O_NOFOLLOW`-equivalent creation, cleanup traps, single-instance locking, and immediate deletion after a successful or failed import.

### P1-16: User reconciliation does not remove disabled/deleted users

The bridge filters inactive users from `ListUsers`, but `sync-users.sh` only imports/updates returned users. Old passdb and POSIX accounts remain.

**Impact:** stale credentials persist locally. Connect authorization normally blocks them, but this creates dangerous coupling with fail-open modes and future bridge failures.

**Fix:** perform desired-state reconciliation: enumerate managed Samba users, remove those absent from the bridge, disable/remove their passdb entries, and define a safe POSIX-account retention policy.

### P1-17: Synchronization scripts can report success after failed mutations

Examples include:

- `pdbedit` failure followed by a success message and final exit 0.
- Unchecked `net conf addshare`/`setparm` calls.
- `sync-config.sh` failing to apply a security setting without failing the sync.

**Fix:** capture every command result, fail the reconciliation when desired state was not applied, verify the resulting registry/passdb state, and expose health/metrics based on last successful convergence.

### P1-18: Text synchronization output is not safely validated

Usernames, share names, paths, and settings are transported as tab/newline-delimited text. The sync scripts do not independently enforce the Web UI's validation rules. Manually modified or legacy DB rows can therefore alter record boundaries or be interpreted as command options.

**Fix:** use protobuf/JSON with strict schema validation, reject control characters and reserved names, pass `--` before shell operands where supported, and validate share paths against the configured storage root.

## 7. Medium-severity and defense-in-depth findings

### P2-01: User/share context strings are silently truncated

The connection context stores user and share names in 128-byte arrays. The database username limit is character-based, so UTF-8 input can exceed 127 bytes. Silent truncation can cause denial, incorrect lookup, or prefix identity confusion.

**Fix:** dynamically allocate exact strings and enforce protocol byte-length limits at account/share creation and bridge ingress.

### P2-02: Share-relative canonicalization is inconsistent

`kaimo_share_rel()` is applied in selected snapshot and delete paths but not uniformly in create, readdir, close, rename, and mkdir. Some Samba call sites may produce connectpath-prefixed names.

Its prefix test also does not require a path-separator boundary after `connectpath`.

**Fix:** one canonical path routine, with boundary-aware connectpath stripping, called by every hook before authorization or event emission.

### P2-03: Bridge path validation is inconsistent

Delete checks `ShareRelativePath.IsValid()`, while Open and Snapshot primarily call `Normalize()` only. Snapshot materialization also uses `Path.Combine()` directly rather than the storage layer's containment-checked `ToAbsolutePath()`.

**Fix:** validate raw input before normalization, reject NUL/`..`/absolute/internal paths, then resolve through one containment-checked storage API. Validate persisted `FileVersion.FilePath` before using it as an output path.

### P2-04: Snapshot enumeration parsing trusts an unbounded decimal count

The native parser uses `atoi()` on a sidecar response and allocates based on the result. Invalid or overflowing input is not robustly handled.

**Fix:** use `strtol`/`strtoul` with full error checking, a maximum snapshot count, and a consistency check between count and received token records.

### P2-05: Snapshot response buffers impose undocumented truncation

The VFS uses a 64 KiB response buffer for enumeration. The header may advertise more tokens than fit, while only a subset is parsed. The protocol does not signal truncation or pagination.

**Fix:** add pagination/streaming or an explicit bounded count. Reject a response that exceeds the negotiated frame limit.

### P2-06: Snapshot cache validity uses size instead of content identity

A same-sized partial/corrupt/tampered file is accepted as a valid cache hit.

**Fix:** include version ID/content hash in the cache key or verify the stored hash before reuse.

### P2-07: Cleanup enumeration exception handling is incomplete

`SafeEnumerateDirectories()` returns a lazy enumerable from inside a `try`; exceptions may occur later during `foreach`, outside that local `try`.

**Fix:** materialize the directory list inside the protected block or use enumeration options with per-entry error handling.

### P2-08: Cache configuration is not validated

Negative, zero, NaN, or extreme TTL/sweep/cap values can cause unexpected deletion, service termination, or a tight failure loop.

**Fix:** bind validated options at startup with minimum/maximum values and fail configuration validation before serving requests.

### P2-09: RPC cancellation is not propagated

Repository, ACL, version, and file-copy work generally ignores `ServerCallContext.CancellationToken`.

**Fix:** add cancellation-aware interfaces where missing and pass the request token through database calls, loops, and stream copies.

### P2-10: Bulk user export is unbounded and sequential

`ListUsers` loads all users and calls `GetNtHashAsync` one at a time. One corrupt hash can abort the whole RPC, and a large response can exceed gRPC message limits.

**Fix:** validate hashes individually, isolate corrupt rows, paginate/stream users, enforce exactly 16 bytes, and avoid an N+1 lookup/decryption pattern.

### P2-11: Decrypted credentials are not minimized or cleared

NT hashes exist in managed byte arrays, protobuf copies, C++ strings, stdout capture, shell memory, and a temporary smbpasswd file.

**Fix:** minimize copies and lifetime, avoid shell text transport, zero mutable buffers where practical, and never persist hashes beyond the import transaction.

### P2-12: `authd` is not supervised independently

The entrypoint starts `kaimo_authd` in the background and then replaces itself with `smbd`. If `authd` exits, the container can remain nominally running while authorization fails closed indefinitely.

**Fix:** use a proper init/supervisor or merge process health into container health. Restart/fail the container when the sidecar is unavailable beyond a short threshold.

### P2-13: Disabled service/share behavior is polling-based

Configuration, share, and user sync run every 60 seconds. Existing open handles can continue even after desired state changes, and registry deletion alone does not revoke already-open file handles.

**Fix:** define a revocation SLA, add push/invalidation where needed, close affected shares/sessions immediately, and decide whether active file handles must be forcibly closed.

### P2-14: Hard-coded development credentials are present in operational paths

The entrypoint and health/audit probes use the default `kaimotest`/`Passw0rd!` credentials unless overridden. Passwords are also passed in command arguments.

**Fix:** remove production defaults, require secrets, avoid password-in-argv where possible, and use a dedicated health mechanism that does not depend on a reusable account.

### P2-15: Samba ABI pinning lacks a CI enforcement gate

The module is built against Samba 4.19.5/ABI 49. The source version is pinned, but the repository does not prove that changes or alternate Dockerfiles retain the same version and ABI expectations.

**Fix:** add CI checks for the pinned tarball/version, build the module and Samba together, run `testparm`, load the module, and execute a minimal SMB operation matrix on every relevant change.

## 8. Functional coverage matrix

| Area | Current coverage | Gaps / risks |
|---|---|---|
| Protobuf services | All 13 RPC methods have .NET implementations | No protocol-level auth/version capability negotiation |
| NTLM user sync | Active users and raw NT hashes are exported/imported | Insecure temp storage, no removals, no pagination, corrupt row can abort sync |
| TREE_CONNECT | SMB enabled flag, user, share lookup, root ACL | Disabled share not checked; bridge trust and identity spoofing |
| File read | `SEC_FILE_READ_DATA` → `ListReadData` | Attributes, EA, execute/traverse, and security descriptor reads missing |
| File write | Write and append bits combined → `CreateWriteData` | Append-only semantics and metadata writes missing |
| File create | Parent `CreateWriteData` check | Directory/add-subdirectory semantics need explicit mapping; fallback paths |
| Delete/rmdir | Pre-operation target/parent authorization | Fixed-path truncation, canonicalization, race semantics |
| Delete-on-open | `SEC_STD_DELETE` handled in AuthorizeOpen | Coupled to incomplete access-mask mapping |
| Rename/move | Native operation and post-event exist | No destination authorization; directory type hardcoded false |
| Directory listing | Entry-by-entry read authorization with short cache | Unbounded cache, synchronous RPC volume, canonicalization, internal cache namespace |
| Close lifecycle | Modified files emit close event | Lossy, reads content later by path, concurrent attribution races |
| Mkdir lifecycle | `create_file` primary event plus `mkdirat` fallback | Fallback authorization and duplicate-event semantics need proof |
| Delete lifecycle | Metadata/version/search cleanup event | Event loss; no durable reconciliation |
| Rename lifecycle | ACL/version/search path update event | Not idempotent; wrong directory flag; event loss |
| Dynamic shares | Enabled shares mirrored to registry | Errors ignored, disabled window, unvalidated path, active-session semantics |
| Protocol config | Dialect/signing/encryption/wsdd/audit synchronized | Apply failures can be hidden; polling delay; probe credentials |
| SMB enable/disable | Connect gate plus periodic close-share | Existing handles and delay; bridge methods do not all enforce state |
| Snapshot enumeration | GMT tokens returned through VFS | Buffer/count limits, directory flag, per-file visibility |
| Snapshot resolution | Version lookup and materialization | ACL leak, direct cache access, writable/raw open, races, unbounded work |
| Recycle bin | Deliberately absent for SMB | Product decision, not memory-safety issue |
| Share enumeration ABE | Hidden flag only | Per-user share visibility intentionally deferred |

## 9. Recommended target architecture

### 9.1 Local Samba-to-sidecar protocol

Use a versioned, length-prefixed binary message rather than tab-separated lines.

Suggested request envelope:

```text
version
request_id
operation
authenticated_session_identity
share_id_or_name
source_path
destination_path
object_type
samba_access_mask
create_disposition
operation_flags
```

Requirements:

- Fixed maximum frame length before allocation.
- Explicit UTF-8 validation and byte-length limits.
- No identity accepted solely from an untrusted text field.
- Full read/write loops and end-to-end deadlines.
- Structured error classes: deny, not found, invalid request, unavailable, timeout.
- A protocol version/capability handshake so VFS and sidecar upgrades cannot silently disagree.

### 9.2 Central authorization API

Prefer one normalized authorization engine over independent ad hoc RPCs. It should:

- Resolve only enabled users and enabled shares.
- Validate and canonicalize paths exactly once.
- Evaluate all requested access bits independently.
- Express multi-path operations such as rename atomically in one decision.
- Return a short machine-readable decision code in addition to a log message.
- Support decision-cache invalidation when ACLs, users, shares, or service state change.
- Keep authorization and post-operation lifecycle notification as separate channels.

### 9.3 Snapshot service

The target snapshot flow should be:

1. Validate enabled user/share and canonical logical path.
2. Enforce read ACL for the exact file or filter every file in a folder snapshot.
3. Reserve cache quota and acquire a keyed materialization lock.
4. Read the immutable version blob with cancellation and a byte limit.
5. Write a temporary file outside the client namespace or within a protected internal namespace.
6. Verify size and content hash.
7. Apply read-only ownership/mode and historical timestamps.
8. Atomically publish the cache entry.
9. Return an opaque internal handle/path that cannot be requested directly by a client.
10. Open through the VFS stack with read-only flags.
11. Hold a lease until the open/traversal completes so cleanup cannot remove active content.

### 9.4 Durable lifecycle pipeline

The target event flow should be:

```text
VFS operation succeeds
  -> append event to local durable spool
  -> acknowledge SMB operation independently
  -> sidecar sends event with stable event_id
  -> bridge transactionally/idempotently applies lifecycle effects
  -> bridge acknowledges event_id
  -> sidecar removes spool record
```

Each event handler must define duplicate behavior:

- Close/version: one version per operation ID.
- Mkdir/owner/index: repeated processing is a no-op/update.
- Delete: already absent is success.
- Rename: already at destination is success; never delete destination history merely because the source is absent.

Periodic reconciliation should still exist to repair missed or partially applied side effects.

## 10. Phased remediation plan

### Phase 0: Production containment

**Goal:** eliminate known direct bypasses while deeper work is in progress.

1. Keep fail-open disabled and reject configuration that enables it in production.
2. Disable snapshot advertisement and deny timewarp opens, not merely the current openat redirect, until snapshot isolation is fixed.
3. Temporarily deny rename if complete source/destination authorization cannot be added immediately.
4. Change the Unix socket to a private directory and mode `0660`.
5. Isolate the bridge on a dedicated network and restrict inbound clients.
6. Remove default test credentials from production configuration.
7. Mark the deployment unhealthy if `authd` is not responsive.

**Exit criteria:** no known unauthenticated path can retrieve hashes, access internal snapshot content, or perform an unmodeled rename.

### Phase 1: Memory/resource and local protocol hardening

1. Make connection-context allocation fail closed.
2. Replace fixed/truncating authorization paths with checked dynamic strings.
3. Implement framed messages, full I/O loops, deadlines, and field limits.
4. Add peer credential verification.
5. Replace detached threads with a bounded concurrency model.
6. Replace the unbounded cache with bounded LRU+TTL storage.
7. Add metrics for active connections, queue depth, cache entries, timeouts, denials, and sidecar failures.

**Exit criteria:** fuzz and fault-injection tests cannot cause authorization bypass, indefinite worker blocking, or unbounded memory/thread growth.

### Phase 2: Complete authorization coverage

1. Extend/replace the authorization protobuf contract.
2. Map the full Samba access mask to all Kaimo `FilePermission` values.
3. Implement multi-path rename authorization.
4. Cover mkdir fallback, metadata, EA, owner, security descriptor, link, symlink, and other mutating hooks actually reachable in Samba 4.19.5.
5. Enforce enabled share/service state in every bridge operation.
6. Centralize path validation/canonicalization.
7. Define TOCTOU behavior and use FSP/handle identity where possible.

**Exit criteria:** an automated matrix proves allow and deny behavior for every permission and every supported SMB mutation.

### Phase 3: Snapshot redesign

1. Introduce per-file ACL filtering for folder snapshots.
2. Protect or relocate the cache namespace.
3. Enforce read-only access and VFS stackability.
4. Implement atomic hash-verified materialization.
5. Add quota reservation, keyed locks, active leases, and cancellation.
6. Harden cleanup against symlinks/reparse points and concurrent activity.
7. Add cache invalidation on share/version deletion and ACL-sensitive access checks on every open.

**Exit criteria:** users cannot access denied historical files through enumeration, direct paths, prior cache entries, guessing, races, or ACL revocation.

### Phase 4: Durable lifecycle and synchronization

1. Add durable event spool, IDs, acknowledgement, retry, dead-letter handling, and reconciliation.
2. Make every event handler idempotent.
3. Solve stable close-content capture.
4. Correct directory rename detection.
5. Replace insecure hash/temp handling.
6. Reconcile stale users and shares.
7. Fail sync scripts on any unapplied desired state and verify final state.
8. Supervise all long-running processes.

**Exit criteria:** crash/restart and duplicate-delivery tests leave database, versions, ACLs, metadata, search, passdb, and Samba registry converged.

### Phase 5: Transport security and operational readiness

1. Enable mTLS/workload identity for bridge RPCs.
2. Apply per-service/RPC authorization.
3. Add rate limits, message limits, structured audit logs, and secret redaction.
4. Define SLOs for authorization latency, event lag, sync convergence, and revocation.
5. Add backup/restore procedures for passdb, registry, event spool, and version metadata.
6. Add a documented Samba upgrade/ABI process.

**Exit criteria:** security review verifies least privilege between containers and operations can detect and recover from all bridge/sidecar failure modes.

## 11. Required test plan

### 11.1 Native unit and sanitizer tests

- Build extracted protocol/path/cache helpers with `-Wall -Wextra -Wconversion -Werror`.
- Run ASan and UBSan for message parsing, path construction, snapshot count parsing, and error cleanup.
- Run TSan or targeted concurrency tests for the sidecar cache/worker model.
- Fuzz framed messages, invalid lengths, UTF-8, NULs, tabs/newlines, integer overflow, and truncated payloads.

### 11.2 Native fault-injection tests

- Context allocation fails.
- Socket creation/connect/read/write returns `EINTR`, `EAGAIN`, partial length, EOF, and timeout.
- Sidecar accepts and stalls.
- Bridge deadline expires.
- Cache reaches its entry/memory cap.
- Snapshot response count is negative, overflowing, inconsistent, or above limit.
- Paths are exactly below, at, and above every configured boundary.

### 11.3 .NET bridge integration tests

- Every RPC with unknown, disabled, and enabled users/shares.
- Every raw invalid path and normalized valid path.
- Full permission matrix including explicit deny precedence and inheritance.
- Rename source/destination/overwrite combinations.
- Folder snapshot with child-level denies.
- Direct internal cache path attempts.
- Concurrent materialization, cancellation, and cleanup.
- Corrupt NT-hash rows and large user sets.
- Duplicate, reordered, and retried lifecycle events.

### 11.4 Container/Samba end-to-end tests

- Real Samba 4.19.5 module build and load.
- `testparm` and ABI/version assertions.
- Login, share enumeration, connect, read, write, append, create, truncate, delete-on-close, unlink, rmdir, mkdir, and rename.
- Attribute, EA, permission, ownership, hardlink, symlink, and server-side copy behavior.
- Destination-denied and overwrite-denied rename.
- Share/user/service disable while sessions and handles are active.
- Authd crash, bridge crash, restart, network partition, and recovery.
- Windows Previous Versions plus `smbclient` snapshot enumeration/open/copy/restore.
- Snapshot ACL revocation and direct-cache-path negative tests.
- Event spool recovery and exactly-once lifecycle assertions.

### 11.5 Release gates

A release should be blocked when any of the following is true:

- Native sanitizer/fuzz suite fails.
- Any unsupported access-mask bit is silently allowed.
- A missing context, malformed path, timeout, or parser error can continue an operation.
- A denied live or historical file is observable through listing, direct open, cache path, rename, metadata operation, or snapshot restore.
- Synchronization reports success without converging passdb/registry/config state.
- Event retries can create duplicates or delete valid history.
- The bridge accepts unauthenticated hash or snapshot RPCs.

## 12. Implementation checklist

### Native VFS checklist

- [ ] Connection data uses Samba-owned lifetime and fail-closed allocation.
- [ ] One canonical checked path routine is used everywhere.
- [ ] No fixed buffer truncation can change the authorized target.
- [ ] Full access-mask mapping exists.
- [ ] Rename is authorized before mutation.
- [ ] All mutating VFS operations are inventoried and covered or explicitly denied.
- [ ] Snapshot client paths cannot reach the internal cache.
- [ ] Timewarp opens are read-only and use the VFS stack.
- [ ] All Unix-socket calls have deadlines and full I/O loops.

### Sidecar checklist

- [ ] Framed/versioned protocol with maximum frame size.
- [ ] Peer credentials and private socket permissions.
- [ ] Bounded worker and request queues.
- [ ] Bounded cache with expiry and invalidation.
- [ ] Durable event spool and acknowledgement.
- [ ] Sidecar health is supervised.

### Bridge checklist

- [ ] Authenticated/authorized transport.
- [ ] Enabled user/share/service checks are centralized.
- [ ] Raw path validation and containment are consistent.
- [ ] Cancellation and limits propagate through all expensive work.
- [ ] Snapshot folder results are filtered per file.
- [ ] Cache writes are atomic, hash-verified, read-only, and synchronized with cleanup.
- [ ] Lifecycle handlers are idempotent.

### Synchronization checklist

- [ ] NT hashes never remain in a predictable or world-readable file.
- [ ] User/share/config records have strict schema validation.
- [ ] Removed users and shares are reconciled.
- [ ] Every mutation error makes the sync fail.
- [ ] Final state is verified before recording success.
- [ ] Concurrent sync instances are locked out.
- [ ] Credentials are not exposed in process arguments.

## 13. Decisions that must be made explicitly

1. **Snapshot cache location:** outside the share versus protected in-share namespace.
2. **Stable close-content capture:** staging copy/reflink, descriptor-aware helper, or coordinated locking.
3. **Authorization contract:** full raw Samba access mask versus explicit Kaimo permission mask/operation enums.
4. **Revocation semantics:** maximum accepted delay and whether active handles are forcibly terminated.
5. **Event durability:** local spool technology, maximum retention, and dead-letter operations.
6. **User lifecycle:** remove versus disable stale POSIX accounts.
7. **Share ABE:** retain hidden-flag-only behavior or implement per-user share enumeration.
8. **Recycle behavior:** continue permanent SMB delete or align with the Web recycle-bin setting.

These should be recorded as architectural decisions before implementing the dependent phases.

## 14. Source evidence index

| Finding area | Primary source locations |
|---|---|
| Context allocation fail-open | `samba-vfs/module/vfs_kaimo_bridge.c:409-438`, `:488-504` |
| Path reconstruction/truncation | `vfs_kaimo_bridge.c:392-407`, `:561-567`, `:600-628` |
| Incomplete access mapping | `vfs_kaimo_bridge.c:488-503`, `Core/Security/FilePermissions.cs` |
| Rename authorization gap | `vfs_kaimo_bridge.c:632-656`, `Core/Services/File/FileService.cs:53-86`, `:694-719` |
| Unbounded sidecar threads/cache | `samba-vfs/module/authd.cpp:49-66`, `:183-225`, `:258-262` |
| World-writable socket | `authd.cpp:241-254` |
| Partial stream I/O | `vfs_kaimo_bridge.c:61-92`, `:181-213`; `authd.cpp:183-225` |
| Snapshot ACL leak | `SmbBridge/Services/SnapshotGrpcService.cs:181-237`; `Core/Services/File/FileService.cs:884-899` |
| Snapshot raw open | `vfs_kaimo_bridge.c:684-755` |
| Snapshot materialization races | `SnapshotGrpcService.cs:288-317`; `SnapshotCacheCleanupService.cs` |
| Disabled share lookup | `Infrastructure/Repositories/ShareRepository.cs:45-49`; bridge service share lookups |
| Event reliability/TOCTOU | `vfs_kaimo_bridge.c:156-174`, `:575-680`; `FileEventGrpcService.cs`; `FileService.cs:341-400` |
| Rename duplicate destruction | `Infrastructure/Repositories/FileVersionRepository.cs:142-178` |
| Insecure hash temp file | `samba-vfs/sync-users.sh:17-49` |
| Sync error handling | `sync-users.sh`, `sync-shares.sh`, `sync-config.sh` |
| Unauthenticated h2c | `SmbBridge/Program.cs:28-40`; `authd.cpp` and sync clients; `docker-compose.yml` |

## 15. Final assessment

The bridge covers the intended high-level migration phases, and the protobuf surface itself is fully wired. The remaining problem is not missing RPC registration; it is that the native operation model, error behavior, resource management, and snapshot/event trust boundaries do not yet preserve the full Kaimo security and lifecycle semantics.

The recommended implementation order is deliberate:

1. Contain direct bypasses.
2. Make the native protocol and memory/resource behavior deterministic.
3. Complete authorization coverage.
4. Redesign snapshots around per-file ACLs and an inaccessible cache.
5. Make events and synchronization durable and idempotent.
6. Add transport identity, operational controls, and release gates.

Completing only the snapshot fixes or only the memory fixes is insufficient. The system is safe only when the VFS hooks, sidecar protocol, bridge authorization, lifecycle events, cache, synchronization scripts, and container trust boundaries are treated as one end-to-end security mechanism.
