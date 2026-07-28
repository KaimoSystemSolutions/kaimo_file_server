# Samba Bridge and VFS Security, Memory-Safety, and Coverage Audit

[← Table of contents](README.md)

> **Audit date:** 2026-07-22  
> **Scope:** `src/samba-vfs`, `Kaimo_File_Server.SmbBridge`, the relevant Core/Infrastructure lifecycle code, Samba synchronization scripts, container wiring, and the shared gRPC contract
> **Audit type:** Static code and architecture review, supported by the available .NET test suite  
> **Repository state:** No production source files were changed as part of the audit  
> **Roadmap revision:** 2026-07-27, reordered against the implemented remediations and remaining production risks
> **Overall result:** Direct authorization and transport bypasses are largely contained; production approval remains blocked by credential synchronization, durable lifecycle handling, snapshot consistency, and release verification

## 1. Executive summary

The Samba migration is functionally substantial: all RPCs in the protobuf contract have server implementations, the native VFS module covers the main SMB data-path seams, and the bridge reuses the existing Kaimo repositories, ACL service, version service, ownership logic, and search lifecycle hooks.

However, the current implementation is **not yet complete or safe enough to act as the sole enforcement boundary for Kaimo ACLs**. The audit found several issues that can cause authorization bypasses, disclosure of historical file content, silent state divergence, unbounded resource consumption, or incorrect lifecycle records.

The most important conclusions are:

1. No obvious direct stack overflow, heap overflow, use-after-free, or double-free was found during the static review. This is not a formal proof of C/C++ memory safety.
2. The native connection sidecar now has bounded worker/descriptor growth,
   receive/send deadlines, and a count/byte-bounded authorization LRU.
   VFS-side connect, complete request, and complete response I/O now also share
   strict end-to-end deadlines.
3. The complete open access mask and rename source/destination/replacement policy are now mapped to Kaimo permissions in source. Native runtime verification and several metadata/security operation checks remain open.
4. Folder snapshot materialization now uses the existing per-file ACL filter and a reconciled per-user projection. Materialized bytes are stored in an isolated global cache outside every share; overlap is rejected by both the bridge and share synchronizer.
   Timewarp access is read-only, mutation attempts fail with read-only
   filesystem semantics, and cache opens pass through the remaining VFS stack.
   Cache files are content-verified and atomically published after a durable
   same-filesystem temporary write.
5. The local sidecar protocol is versioned and completely framed. Both ends
   handle partial I/O, and VFS-side connect/send/read now share strict monotonic
   end-to-end deadlines.
6. The gRPC control plane is now isolated on a dedicated internal network and
   protected by mTLS, per-workload RPC allow-lists, and audited/rate-limited
   hash export. Native/container runtime verification remains pending.
7. Lifecycle-event transport is now durably spooled, explicitly acknowledged,
   retried, dead-lettered, and deduplicated by persistent event ID. Close
   versioning/indexing consumes immutable content captured from the exact
   closing descriptor and attributed to its authenticated Samba identity.
   Credential/configuration synchronization and full multi-effect
   reconciliation remain open.

The system should be treated as a **working migration prototype with critical hardening work remaining**, not as a completed production security boundary.

## 2. Audit scope

### 2.1 Native Samba components

- `src/samba-vfs/module/vfs_kaimo_bridge.c`
- `src/samba-vfs/module/authd.cpp`
- `src/samba-vfs/module/authsync.cpp`
- `src/samba-vfs/module/sharesync.cpp`
- `src/samba-vfs/module/configsync.cpp`
- `src/samba-vfs/protos/kaimo_smb_bridge.proto`

### 2.2 Synchronization and container components

- `src/samba-vfs/sync-users.sh`
- `src/samba-vfs/sync-shares.sh`
- `src/samba-vfs/sync-config.sh`
- `src/samba-vfs/entrypoint.vfs.sh`
- `src/samba-vfs/conf/smb.conf.vfs`
- `src/samba-vfs/Dockerfile.vfs`
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
