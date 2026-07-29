# Test Plan, Checklists, and Architecture Decisions

[← Table of contents](README.md)

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

The checklist reflects the repository state on 2026-07-29. Checked items are
implemented; their required release verification remains tracked in milestone 4.

### Native VFS checklist

- [x] Connection data uses Samba-owned lifetime and fail-closed allocation.
- [x] One canonical share-relative path routine is used everywhere.
- [x] No fixed request-path buffer truncation can change the authorized target.
- [x] User/share connection context cannot truncate or confuse identities;
  ingress is restricted to the bounded synchronization-safe ASCII syntax.
- [x] Full access-mask mapping exists in source; native/runtime verification remains pending.
- [x] Rename is authorized before mutation (native/runtime verification pending).
- [ ] All mutating VFS operations are inventoried and covered or explicitly denied.
- [x] Snapshot client paths cannot reach the isolated internal cache.
- [x] Timewarp opens are read-only and use the VFS stack.
- [x] Snapshot enumeration count and token records are bounded and validated
  before Samba label allocation.
- [x] Snapshot response capacity is a compile-time wire invariant; over-limit
  histories deterministically expose the newest 2,048 distinct labels.
- [x] All Unix-socket calls have deadlines and full I/O loops.

### Sidecar checklist

- [x] Framed/versioned protocol with maximum frame size.
- [x] Peer credentials and private socket permissions.
- [x] Bounded worker and request queues.
- [x] Bounded cache with expiry and documented maximum revocation delay.
- [ ] Push invalidation exists for decisions that must revoke before TTL expiry.
- [ ] Durable event spool and acknowledgement.
- [ ] Sidecar health is supervised.

### Bridge checklist

- [x] Authenticated/authorized transport with mTLS and RPC allow-lists.
- [ ] Enabled user/share/service checks are centralized. Share checks are now
  centralized; user and service checks still use separate paths.
- [x] Raw bridge paths are validated before normalization and resolved through
  the common containment-checked path API; persisted snapshot paths are
  independently revalidated.
- [ ] Cancellation and limits propagate through all expensive work.
- [x] Snapshot folder results are filtered per file.
- [x] Cache writes are atomic, hash-verified, read-only, and synchronized with
  cleanup; Linux-container and live-SMB behavior remain release gates.
- [ ] Lifecycle handlers are idempotent.

### Synchronization checklist

- [x] NT hashes never remain in a predictable or world-readable file.
- [x] User/share/config records use versioned JSON with strict schema validation.
- [x] Native command data is separated from diagnostics and validated before
  it can enter persistent reconciliation state.
- [x] Removed users and shares are reconciled.
- [x] Every mutation error makes the sync fail.
- [x] Final state is verified before recording success.
- [x] Concurrent sync instances are locked out.
- [ ] Credentials are not exposed in process arguments.

## 13. Decisions that must be made explicitly

1. **Snapshot cache location (decided 2026-07-22):** global cache outside every share, with overlap rejected at bridge and share-sync boundaries.
2. **Authorization contract (decided 2026-07-22):** transport the full raw Samba access mask, map it centrally, and return an attenuated granted mask.
3. **Stable close-content capture — decide before milestone 2:** staging copy/reflink, descriptor-aware helper, or coordinated locking.
4. **Revocation semantics — decide in milestone 1:** maximum accepted delay and whether active handles are forcibly terminated.
5. **Event durability — decide before milestone 2:** local spool technology, maximum retention, acknowledgement, and dead-letter operations.
6. **User lifecycle — decide in milestone 1:** remove versus disable stale POSIX accounts and define UID retention/reuse.
7. **Share ABE:** retain hidden-flag-only behavior or implement per-user share enumeration.
8. **Recycle behavior:** continue permanent SMB delete or align with the Web recycle-bin setting.
9. **Availability — decide in milestone 4:** whether the bridge remains an accepted fail-closed single point of failure or needs redundancy.

These should be recorded as architectural decisions before implementing the dependent phases.
