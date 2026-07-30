# Implementation Progress – P1-07 through P1-12

[← Progress overview](08-implementation-progress.md) · [Main table of contents](README.md)

### 2026-07-27 — P1-07: Atomic, content-verified snapshot materialization

**Status:** Implemented; focused and complete managed regression suites
verified. Live SMB/container verification remains deliberately separate.

**Solution implemented**

1. Existing cache files are reused only when both their uncompressed length and
   SHA-256 digest match the immutable `FileVersion` metadata.
2. New content is written to a unique sibling temporary file, keeping the
   publication rename on the same filesystem.
3. Streaming is capped at the declared uncompressed size. Short content,
   oversized content, negative sizes, malformed hashes, and digest mismatches
   fail closed.
4. The temporary file is flushed through `Flush(true)` before publication.
   Projection mode and historical mtime are applied before an atomic
   overwrite/rename exposes the completed file.
5. A `finally` cleanup removes abandoned temporary files after read, hash,
   flush, timestamp, mode, or rename failures.

**Validation completed**

- Focused `SnapshotGrpcServiceAclTests`: 11 passed, 0 failed, 0 skipped.
- Regression coverage proves a same-sized corrupt cache entry is not reused,
  matching replacement content is published, hash mismatches and both short
  and oversized streams return `Found=false`, no final partial file appears,
  and temporary files are removed.
- Full managed solution suite: 533 passed, 0 failed, 0 skipped.
- `Kaimo_File_Server.SmbBridge` builds successfully.
- `git diff --check` reports no whitespace errors.

**Validation still required / deliberately separate**

- Run the snapshot regression in the Linux bridge/Samba containers so the
  exact filesystem's rename and `fsync` behavior is covered.
- Re-run Windows Explorer and `smbclient` browse/copy/restore against the
  materialized cache path.
- P1-08 was completed in source immediately afterward with keyed bridge locks,
  cross-process leases, and an explicit bridge-to-VFS handoff.

**Next planned finding:** P1-08 — coordinate snapshot materialization and
cleanup with keyed locks/leases. Completed in source immediately afterward.

### 2026-07-28 — P1-08: Cross-process snapshot token leases

**Status:** Implemented in managed and native source. Managed regression suite
verified; native compilation and live SMB verification remain pending because
Docker Desktop's engine was not running.

**Solution implemented**

1. Added `SnapshotCacheLeaseManager`, keyed by immutable share id plus @GMT
   token. Its per-key gate serializes materialization and projection
   reconciliation without retaining inactive keys indefinitely.
2. Added a protected `.kaimo-lease` file to every token root. Bridge
   materialization takes an exclusive Unix `flock`; cleanup can delete only
   after obtaining the same lock exclusively and nonblocking.
3. Extended `ResolveVersionReply` with a cryptographically random, opaque
   handoff id and added the allow-listed `ReleaseVersionLease` RPC plus framed
   local operation 11. The bridge downgrades its exclusive lock to shared
   before replying and retains it until acknowledgement or a bounded 30-second
   expiry.
4. The VFS validates and opens the token lease with `O_NOFOLLOW`, verifies a
   regular single-link file, obtains `LOCK_SH`, and only then acknowledges the
   bridge handoff. This closes the resolve-to-open deletion window.
5. Native shared leases are attached to Samba `files_struct` extensions,
   inherited by relative child opens, and released immediately before the
   underlying handle closes. Path-based stat/lstat holds a scoped shared lease
   around the downstream VFS call.
6. TTL, size-cap, and orphan-share eviction now all acquire a token-exclusive
   lease. Active tokens are skipped and retried by a later sweep.
7. Updated the native module marker to
   `2026-07-28a leased snapshot cache`.

**Validation completed**

- Focused snapshot/lease/access-policy suite: 16 passed, 0 failed, 0 skipped.
- Full managed solution suite: 538 passed, 0 failed, 0 skipped.
- Tests cover same-token serialization, independent tokens, cleanup during
  materialization, post-release eviction, handoff blocking, idempotent
  acknowledgement, and the existing snapshot ACL/materialization matrix.
- The adapted native live-test harness passes Python syntax validation.
- The FSP extension macros and const signatures were checked against the exact
  official Samba 4.19.5 source.
- `git diff --check` reports no whitespace errors.

**Validation still required**

- Build and link `vfs_kaimo_bridge.so` and `kaimo_authd` against the pinned
  Samba 4.19.5/protobuf toolchain.
- Run the updated live SMB3 snapshot read-only regression and add an
  interleaving test that holds an SMB directory/file handle while forcing TTL
  and size eviction.
- Re-run Windows Explorer and `smbclient` browse/copy/restore. The Docker
  daemon was unavailable in this session, so these are not claimed as passed.

**Next planned finding:** P1-09 — bound folder snapshot materialization and
propagate cancellation.

### 2026-07-28 — P1-09: Bounded, cancellation-aware folder materialization

**Status:** Implemented and managed-tested; native/live SMB verification
remains pending.

**Solution implemented**

1. Added a singleton `SnapshotMaterializationLimiter` with configurable
   per-request file, byte, and duration limits plus a process-wide concurrency
   semaphore.
2. The complete ACL-filtered folder result is preflighted with checked byte
   arithmetic before projection reconciliation or file creation.
3. A linked request budget now covers repository lookup, ACL filtering,
   existing-cache verification, lease acquisition, decompression, hashing,
   copying, flushing, and publication.
4. Cancellation tokens now reach the EF Core folder query and the
   file-version decompression/copy path; hash validation and all asynchronous
   cache I/O are cancellation-aware.
5. Cancellation is no longer swallowed by per-file resilience handling.
   Same-directory temporary files and final files newly created by an
   abandoned folder request are removed before its exclusive lease is
   released.
6. Compose exposes production overrides while retaining conservative defaults:
   10,000 files, 1 GiB, two concurrent requests, and 25 seconds.

**Validation completed**

- Focused snapshot service suite: 15 passed, 0 failed, 0 skipped.
- Full managed solution suite: 544 passed, 0 failed, 0 skipped.
- New tests cover file-count rejection, byte-count rejection, concurrency
  saturation cancellation, deadline cancellation, and abandoned-output
  cleanup.

**Validation still required**

- Run the folder browse/copy regression through live Samba with deliberately
  over-limit projections and a bridge-side timeout.
- Confirm operational limits against representative production folder sizes
  and storage throughput.

**Next planned finding:** P1-10 — reject disabled shares consistently and
define active-handle revocation semantics.

### 2026-07-28 — P1-10: Central disabled-share gate and bounded revocation

**Status:** Implemented; managed tests and pinned native container build pass.
Live active-session revocation verification remains pending.

**Original problem**

`GetByNameAsync` intentionally returns disabled definitions for management
workflows, but the bridge treated any non-null definition as usable. During
share reconciliation, new internal requests could still authorize or process
a disabled share, while already-open handles remained usable until Samba
eventually removed the registry entry.

**Solution implemented**

1. Added the single `ResolveEnabledShareAsync` gate and routed Connect, Open,
   Delete, Rename, Event, EnumerateSnapshots, and ResolveVersion through it.
2. Disabled and unknown shares now have identical fail-closed external
   behavior; no ACL, file-service, version, or snapshot work starts afterward.
3. Propagated the gRPC cancellation token through the centralized lookup.
4. Made the share reconciliation interval configurable with
   `KAIMO_SHARE_SYNC_INTERVAL_SECONDS` and changed the production default from
   60 seconds to 2 seconds. Invalid zero/non-numeric values stop startup.
5. Retained the existing safe revocation order: delete the registry share,
   then call `smbcontrol smbd close-share` to terminate every active tree
   connection.
6. Documented the explicit semantics: bridge RPCs reject once the committed
   state is visible; under healthy operation, existing handles are forcibly
   disconnected after the configured interval plus reconciliation runtime.
   A bridge outage cannot safely infer a new desired state and therefore delays
   active-session revocation beyond that operational target.

**Validation completed**

- Focused disabled-share, authorization, and snapshot suites: 62 passed,
  0 failed, 0 skipped.
- New managed tests cover all four authorization RPCs, all four lifecycle
  event RPCs, both share-dependent snapshot RPCs, and prove downstream ACL,
  file-service, and version work is not invoked.
- Existing `sync-shares.sh` regression coverage verifies that removed/disabled
  shares invoke `smbcontrol smbd close-share` while stable shares do not.
- Complete managed suite: 548 passed, 0 failed, 0 skipped.
- `docker compose build kaimo_smb_bridge kaimo_samba` passed, including the
  pinned Samba 4.19.5 native module/client compilation and image assembly.

**Validation still required**

- In a live Compose environment, keep an SMB file handle open, disable its
  share, and verify the client is disconnected within the configured SLA.
  This run could not start the standalone stack because Visual Studio already
  owned the fixed `kaimo_file_server_db` container name.
- Alert on consecutive share reconciliation failures because a bridge outage
  necessarily postpones registry and active-handle revocation.

**Next planned finding:** P1-11 — make lifecycle event delivery durable,
acknowledged, retry-safe, and idempotent. Completed immediately afterward.

### 2026-07-28 — P1-11: Durable, acknowledged lifecycle event delivery

**Status:** Implemented; complete managed suite and pinned native container
build pass. Live outage/restart verification remains pending.

**Solution implemented**

1. Added a versioned native event spool with stable UUIDs, restrictive
   ownership/modes, atomic `fsync` publication, restart recovery, capacity
   limits, bounded exponential retry, and a bounded dead-letter directory.
2. Changed the local contract from "gRPC attempted" to "event durably
   enqueued": `smbd` waits only for local persistence, while a dedicated
   dispatcher verifies gRPC status plus the application acknowledgement.
3. Added protobuf event IDs and a persistent bridge receipt/lease repository,
   EF migration, duplicate/type-conflict handling, crash lease recovery, and
   30-day configurable completed-receipt retention.
4. Changed external lifecycle effects to propagate failures so the bridge never
   acknowledges a partially failed attempt as successful.
5. Added the durable spool volume and documented operational capacity/retry/
   retention settings in Compose, `.env.example`, and the Samba runbook.

**Validation completed**

- Complete managed suite: 552 passed, 0 failed, 0 skipped.
- Repository regressions cover claim, busy lease, release/reclaim, completion,
  duplicate acknowledgement, type conflict, and receipt retention.
- gRPC regressions cover required stable IDs and completed-duplicate suppression.
- The pinned Samba 4.19.5 image compiles the protobuf client, `authd`, and VFS
  module; deterministic native spool tests cover restart recovery, retry timing,
  dead-letter transition, delivery removal, and capacity rejection.
- `docker compose config --quiet` passes.

**Validation still required / deliberately separate**

- Interrupt the bridge and database during live SMB close/mkdir/delete/rename,
  recreate both containers, and verify pending spool drain without lost events.
- Exercise retry exhaustion, dead-letter alerting/repair, volume pressure, and
  receipt cleanup under representative production load.
- P1-12 remains next for exact close-content/user attribution. P1-13 remains
  necessary for independently idempotent rename side effects across the
  handler-to-receipt crash window.

**Next planned finding:** P1-12 — bind close processing to the exact content and
authenticated user associated with the closing handle.

### 2026-07-28 — P1-12: Descriptor-bound immutable close captures

**Status:** Implemented; focused managed tests, Compose validation, native
sidecar tests, and the pinned Samba 4.19.5 module/container build pass. Live
concurrent-writer and outage/restart verification remains pending.

**Solution implemented**

1. The VFS captures bytes from `fsp_get_io_fd` before native close, preferring a
   same-filesystem CoW reflink and otherwise copying without changing the Samba
   file offset. A changing source is detected and rejected.
2. Captures are durable, read-only, atomically published under a root-provisioned
   reserved namespace, and removed if native close or local durable enqueue
   fails.
3. Local protocol v3 binds the capture to the peer-authenticated Samba user,
   share, and logical close path. `authd` validates it before accepting the
   durable event.
4. The bridge accepts only a 128-bit lowercase opaque capture ID, derives the
   path beneath the resolved share, and supplies independent capture streams to
   versioning and search. The mutable live path is never reopened for content.
5. Capture lifetime follows the P1-11 event: retained during retry/dead-letter,
   deleted by the bridge after receipt completion, and compatible with a
   completed duplicate after cleanup.
6. Updated the native marker to
   `2026-07-28g descriptor-bound close captures`.

**Validation completed**

- Focused managed FileService/event suites pass, including regressions proving
  live storage is never read, both versioning and indexing receive capture
  bytes, invalid IDs are rejected before lookup, and a completed retry succeeds
  after capture deletion without repeating effects.
- Complete managed suite: 555 passed, 0 failed, 0 skipped.
- `docker compose config --quiet` passes.
- `docker compose build kaimo_samba` passes. This compiles/links the real VFS
  module against Samba 4.19.5, compiles the revised protobuf client and
  `authd`, and runs the native decision-cache, event-spool, and local-protocol
  tests.

**Validation still required**

- Exercise two concurrent SMB writers and prove each close versions the bytes
  captured at its own close boundary, including the checked-copy fallback.
- Stop the bridge/database after local enqueue, restart them, and verify the
  capture survives until acknowledgement and is then removed.
- Force dead-lettering and validate that the associated capture remains
  available for repair and that capacity monitoring covers both records and
  capture bytes.

**Next planned finding:** P1-13 — make rename lifecycle side effects
independently retry-idempotent.
