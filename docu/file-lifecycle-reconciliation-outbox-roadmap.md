# File Lifecycle Hardening: Operation Journal, Transactional Outbox, and Reconciliation

> **Status:** Proposed roadmap
> **Created:** 2026-07-19
> **Scope:** Crash recovery and eventual consistency between the filesystem, PostgreSQL,
> version blob storage, and Elasticsearch
> **Related:** [`smb-samba-vfs-migration.md`](smb-samba-vfs-migration.md),
> `FileService`, `FileVersionService`, Samba VFS event notifications

---

## Contents

1. [Context and Problem Statement](#1-context-and-problem-statement)
2. [Goals and Non-Goals](#2-goals-and-non-goals)
3. [Failure Model](#3-failure-model)
4. [Target Architecture](#4-target-architecture)
5. [Persistence Model](#5-persistence-model)
6. [Operation State Machine](#6-operation-state-machine)
7. [Central Lifecycle Coordinator](#7-central-lifecycle-coordinator)
8. [Outbox Worker](#8-outbox-worker)
9. [Reconciliation Worker](#9-reconciliation-worker)
10. [Samba VFS Integration](#10-samba-vfs-integration)
11. [Idempotency, Ordering, and Concurrency](#11-idempotency-ordering-and-concurrency)
12. [Retry, Dead-Letter, and Repair Policy](#12-retry-dead-letter-and-repair-policy)
13. [Observability and Operations](#13-observability-and-operations)
14. [Security and Data Handling](#14-security-and-data-handling)
15. [Implementation Phases](#15-implementation-phases)
16. [Test Strategy](#16-test-strategy)
17. [Deployment and Rollback](#17-deployment-and-rollback)
18. [Definition of Done](#18-definition-of-done)
19. [Open Decisions](#19-open-decisions)
20. [Critical Review — Prioritized Concerns](#20-critical-review--prioritized-concerns)

---

## 1. Context and Problem Statement

Kaimo stores one logical file across several independently durable systems:

| System | Stored State |
|---|---|
| Filesystem | Live files, directories, recycle-bin entries |
| PostgreSQL | Ownership, ACL metadata, file version rows, shares |
| Version storage | Content-addressed compressed version blobs under `.versions` |
| Elasticsearch | Searchable file and directory projection |
| Snapshot cache | Rebuildable materialized `@GMT` content |

The lifecycle cleanup implemented in `FileService` now updates these systems from one
central method during normal execution. However, no database transaction can atomically
commit a filesystem rename/delete and the related PostgreSQL changes. The same is true for
Elasticsearch and physical version-blob deletion.

Examples of the remaining crash windows:

1. The filesystem rename succeeds, then the process exits before metadata and version paths
   are updated.
2. A permanent delete succeeds, then PostgreSQL is temporarily unavailable.
3. PostgreSQL commits the cleanup, but a version blob is locked and cannot be deleted.
4. Samba completes native I/O, but the gRPC completion notification never reaches .NET.
5. An outbox handler updates PostgreSQL or Elasticsearch and crashes before marking its
   message complete, causing the handler to run again.

The system therefore needs two complementary mechanisms:

- A durable **operation journal** that records lifecycle intent and progress across the
  filesystem/database boundary.
- A transactional **outbox** that reliably executes retryable side effects.
- A periodic **reconciliation worker** that detects state changes for which no usable journal
  or completion event exists.

The target guarantee is **at-least-once processing with idempotent handlers and eventual
consistency**. Exactly-once execution across these systems is neither possible nor required.

---

## 2. Goals and Non-Goals

### Goals

- Recover automatically after a process, container, host, network, or database interruption.
- Prevent indefinitely stale metadata, version rows, search documents, and orphaned blobs.
- Use the same lifecycle semantics for Web, in-process file handles, and Samba VFS.
- Preserve source ownership and ACLs across rename/move operations.
- Preserve deduplication safety: a blob may only be removed when no version row references it.
- Make every handler safe to execute more than once.
- Expose backlog, retry, age, and dead-letter metrics.
- Support multiple worker replicas without processing the same message concurrently.
- Roll out without pausing file access or requiring a one-time full migration.

### Non-Goals

- Distributed ACID transactions between PostgreSQL, the filesystem, and Elasticsearch.
- Replacing the filesystem with object storage.
- Capturing every byte write in the outbox; only lifecycle boundaries are journaled.
- Keeping Elasticsearch available when the Elasticsearch cluster itself is down.
- Inferring a perfect rename after an unjournaled offline filesystem change. Without a stable
  cross-platform file identity, an unknown rename is reconciled conservatively as delete plus
  create.
- Per-file journaling of bulk operations. `ArchiveAsync`, `UnzipAsync`, and folder restore can
  create or remove thousands of entries in one call. These are journaled as a **single** bulk
  operation (or not journaled at all and left to the filesystem->database scan); they are never
  expanded into one journal row per produced file.

---

## 3. Failure Model

The design must handle failures at every boundary below.

| Failure Point | Observable Result | Required Recovery |
|---|---|---|
| Before filesystem mutation | Journal exists, old path still exists | Cancel/no-op or retry mutation |
| After filesystem mutation, before DB update | New filesystem state, stale DB state | Complete metadata/version/search effects |
| During DB transaction | No partial DB commit | Retry transaction |
| After DB commit, before outbox acknowledgement | Side effect may already be applied | Re-run idempotently |
| During Elasticsearch request | DB/filesystem correct, search stale | Retry search projection |
| During blob delete | Version rows removed, blob remains | Retry blob GC/reconciliation |
| Samba event lost | Filesystem changed, no journal completion | Periodic reconciliation discovers drift |
| Worker dies while handling message | Message remains leased until timeout | Another worker reclaims it |
| Duplicate/reordered Samba event | Same operation observed more than once | Deduplicate by operation ID and sequence |

The implementation should include explicit failure injection tests for every row in this table.

---

## 4. Target Architecture

```text
 Web / FileHandle / Samba VFS
              |
              v
     FileLifecycleCoordinator
       |                   |
       |                   +---- filesystem mutation / observed native mutation
       v
 PostgreSQL transaction
   - file_operations journal
   - outbox_messages
   - metadata/version state where appropriate
              |
              v
       OutboxWorkerService
       |       |        |
       v       v        v
  metadata  versions  search index
                 |
                 v
            version blob GC

       ReconciliationWorkerService
       |-- resumes stale journal operations
       |-- compares filesystem -> metadata/search
       |-- compares metadata/version rows -> filesystem/blobs
       `-- emits repair operations into the same outbox
```

### Process Placement

- Every process may **write** journal/outbox records through shared Core/Infrastructure APIs.
- The long-running worker should run in `Kaimo_File_Server.Host`, not in a Blazor request
  scope or the Samba VFS module.
- Multiple Host replicas are allowed. PostgreSQL leases and `SKIP LOCKED` prevent duplicate
  concurrent claims.
- `Kaimo_File_Server.SmbBridge` remains a producer. It should not be the only worker because a
  bridge restart must not stop recovery.

### Core Components

| Component | Responsibility |
|---|---|
| `IFileLifecycleCoordinator` | Starts and advances durable file operations |
| `IFileOperationRepository` | Persists operation journal state |
| `IOutboxRepository` | Enqueues, leases, completes, retries, and dead-letters messages |
| `IFileLifecycleEffectHandler` | Idempotently applies one effect type |
| `OutboxWorkerService` | Dispatches ready outbox messages |
| `ReconciliationWorkerService` | Detects and repairs missing or contradictory state |
| `IVersionBlobGarbageCollector` | Deletes unreferenced blobs safely |

---

## 5. Persistence Model

### 5.1 `file_operations`

This is the durable operation journal. It describes what the filesystem operation was expected
to do and what was observed afterward.

| Column | Type | Purpose |
|---|---|---|
| `id` | `uuid` PK | Operation/idempotency ID |
| `operation_type` | integer/string | Create, WriteClosed, Mkdir, Rename, Delete, RecycleMove, ShareDelete |
| `state` | integer/string | State machine value |
| `share_id` | `uuid` | Share boundary |
| `source_path` | text nullable | Normalized share-relative source |
| `destination_path` | text nullable | Requested or actual normalized destination |
| `is_directory` | boolean | File versus directory semantics |
| `initiator_id` | `uuid` nullable | User when known |
| `transport` | integer/string | Web, FileHandle, Samba, Reconciler |
| `expected_facts` | `jsonb` | Optional precondition: existence, size, mtime, hash |
| `observed_facts` | `jsonb` | Filesystem facts after mutation |
| `created_at_utc` | timestamp | Audit and stale-operation detection |
| `updated_at_utc` | timestamp | Progress timestamp |
| `completed_at_utc` | timestamp nullable | Terminal success time |
| `last_error` | text nullable | Last coordination error, length limited |
| `row_version` | bigint | Optimistic concurrency token |

Recommended indexes:

- Unique primary key on `id`.
- `(state, updated_at_utc)` for stale-operation recovery.
- `(share_id, source_path)` and `(share_id, destination_path)` for diagnostics.
- Partial index for non-terminal states.

Journal rows should be retained for an operational window, for example 30 days, then removed by
a maintenance job. Terminal rows are audit metadata, not permanent file history.

### 5.2 `outbox_messages`

| Column | Type | Purpose |
|---|---|---|
| `id` | `uuid` PK | Message identity |
| `operation_id` | `uuid` FK | Parent journal operation |
| `effect_type` | integer/string | MetadataRename, VersionDelete, SearchUpsert, etc. |
| `sequence` | integer | Required order inside one operation |
| `aggregate_key` | text | Initial ordering key, normally `share:{shareId}` |
| `payload` | `jsonb` | Versioned handler input |
| `status` | integer/string | Pending, Processing, Completed, DeadLetter |
| `attempt_count` | integer | Retry count |
| `next_attempt_at_utc` | timestamp | Backoff scheduling |
| `locked_by` | text nullable | Worker instance ID |
| `locked_until_utc` | timestamp nullable | Crash-safe lease |
| `created_at_utc` | timestamp | Queue age metric |
| `completed_at_utc` | timestamp nullable | Completion time |
| `last_error` | text nullable | Sanitized failure detail |

Required constraints and indexes:

- Unique `(operation_id, effect_type, sequence)` to make enqueue idempotent.
- Index `(status, next_attempt_at_utc)` for claims.
- Index `(aggregate_key, sequence, status)` for ordered processing.
- Foreign key to `file_operations` with restrictive delete until message retention completes.

> **Ordering gap:** `sequence` is per-operation, so two operations on the same `aggregate_key`
> both start at sequence 1. That index alone does **not** define which operation goes first, yet
> causal order across operations matters — e.g. rename `A->B` then `B->C`; if the second operation's
> effect runs first, the metadata rename `B->C` fails because `B` does not exist yet. Add a
> monotonic per-aggregate ordinal (e.g. `aggregate_seq bigserial`, or order by `created_at_utc`
> then `id`) and make the claim "ready" predicate require that no earlier-ordinal, non-terminal
> operation exists for the same `aggregate_key`. See [§11 Ordering](#11-idempotency-ordering-and-concurrency).

### 5.3 Optional `reconciliation_checkpoints`

Store an incremental cursor per share and reconciliation direction:

- `FilesystemToDatabase`
- `DatabaseToFilesystem`
- `VersionRowsToBlobs`
- `BlobsToVersionRows`
- `SearchProjection`

This avoids scanning every share from the beginning on every interval.

### 5.4 Payload Versioning

Every outbox payload must contain `schemaVersion`. Handlers must support at least the current and
previous deployed version during rolling upgrades. Do not serialize domain objects directly;
use small explicit contracts such as:

```json
{
  "schemaVersion": 1,
  "shareId": "...",
  "oldPath": "docs/old.txt",
  "newPath": "docs/new.txt",
  "isDirectory": false
}
```

---

## 6. Operation State Machine

```text
 Prepared
    |
    | filesystem mutation succeeds or external mutation is observed
    v
 FilesystemApplied
    |
    | DB transaction creates ordered outbox effects
    v
 EffectsPending
    |
    | all effects complete
    v
 Completed

 Any non-terminal state
    | retryable failure       -> RetryScheduled
    | contradictory state    -> NeedsReconciliation
    | retry budget exhausted -> DeadLetter
    | confirmed no-op         -> Cancelled
```

### State Rules

- State transitions use optimistic concurrency (`row_version`) and are monotonic.
- `Completed`, `Cancelled`, and `DeadLetter` are terminal.
- Repeating the same transition is a no-op.
- A worker must never infer success only from an expired lease.
- `FilesystemApplied` records the actual destination path returned by storage, including recycle
  collision suffixes.
- External Samba operations may enter directly as `FilesystemApplied` because Samba already
  performed the mutation.

### Operation-specific Effect Plans

| Operation | Ordered Effects |
|---|---|
| Create/Mkdir | Ensure metadata/owner -> Search upsert |
| WriteClosed | Ensure metadata/owner -> Create version -> Search upsert |
| Rename/Move | Rename metadata subtree -> Rename version subtree -> Search rename |
| RecycleMove | Same as Rename/Move using the actual recycle destination |
| Permanent Delete | Delete metadata subtree -> Delete version subtree -> Blob GC -> Search delete |
| Share Delete | Delete share versions -> Blob GC -> Delete metadata/ACL -> scoped-role cleanup -> share deletion/search cleanup |

Effect order should be explicit even when handlers are individually idempotent. Later effects
must not run while an earlier effect for the same operation is pending or failed.

---

## 7. Central Lifecycle Coordinator

Introduce `IFileLifecycleCoordinator` in Core and move orchestration out of `FileService` helper
methods. `FileService` continues to own authorization and storage access, but delegates durable
lifecycle progress.

Suggested API:

```csharp
public interface IFileLifecycleCoordinator
{
    Task<Guid> PrepareAsync(FileOperationIntent intent, CancellationToken ct = default);

    Task MarkFilesystemAppliedAsync(
        Guid operationId,
        FileOperationObservation observation,
        CancellationToken ct = default);

    Task ObserveExternalOperationAsync(
        FileOperationIntent intent,
        FileOperationObservation observation,
        CancellationToken ct = default);

    Task MarkFilesystemFailedAsync(
        Guid operationId,
        Exception error,
        CancellationToken ct = default);
}
```

### Web/In-process Flow

1. Normalize paths and authorize in `FileService`.
2. Persist `Prepared` journal row.
3. Execute filesystem mutation.
4. Persist the observed result and enqueue all effects in one PostgreSQL transaction.
5. Optionally run latency-critical handlers inline through the same idempotent handler API.
6. Return success after the durable outbox exists; non-critical effects may finish asynchronously.

If step 3 succeeds and step 4 fails, the `Prepared` record remains. Reconciliation probes the
filesystem and advances it to `FilesystemApplied`.

### External/Samba Flow

Samba has already performed native I/O when the completion event arrives:

1. gRPC event contains a stable `operation_id`.
2. `ObserveExternalOperationAsync` inserts or loads that ID.
3. It records `FilesystemApplied` and enqueues effects transactionally.
4. Duplicate events return success without duplicate messages.

### Inline versus Deferred Effects

Authorization remains synchronous and is never put into the outbox. Lifecycle side effects may be
split as follows:

- Metadata/version path correctness: enqueue durably, then optionally execute inline to preserve
  current immediate behavior.
- Elasticsearch: always outbox-driven.
- Blob GC: outbox-driven.
- Snapshot cache cleanup: outbox-driven or handled by its existing bounded cache worker.

Inline execution must still complete the corresponding outbox message. It must not use a separate
code path.

> **Correctness constraint — version content must be captured at close time, not at handle time.**
> Today `FileSession.DisposeAsync` snapshots content from the still-open stream
> (`GetReadableSnapshot`), so the version reflects exactly what was closed. A version-create effect
> whose handler re-reads the *live* file when it runs would snapshot whatever content is on disk at
> handling time — if another writer touched the file in between, the intermediate version is
> silently lost. (`NotifyExternalCloseAsync` already re-reads after close and carries this race; the
> outbox must not widen it.) Therefore **version creation is not a pure deferred effect.** Either:
> 1. keep version creation inline/synchronous at close and defer only search + GC; **or**
> 2. at close, synchronously hash and write the content-addressed compressed blob, then put the
>    resulting `contentHash`, `size`, and close timestamp in the outbox payload. The deferred
>    handler only inserts the version row (idempotent by hash) and applies retention.
>
> Option 2 preserves point-in-time fidelity while still deferring the DB write and retention. The
> same rule applies to any reconciliation-emitted "missed close" — it can only re-create a version
> from a blob that was captured at close time, never by re-reading current content.

> **Race — inline execution vs. the worker claiming the same message.** A message created `Pending`
> and then executed inline can be claimed by a worker concurrently (handlers are idempotent, so it
> is *safe* but does duplicate work and can reorder effects). Inline execution must lease the
> message the same way the worker does: create it as `Processing` with `locked_by = "inline"` /
> `locked_until_utc`, run the handler, then mark it `Completed`. On an inline crash the lease
> expires and the worker reclaims it — no special-casing.

---

## 8. Outbox Worker

### Claim Algorithm

Use a short PostgreSQL transaction and `FOR UPDATE SKIP LOCKED`:

1. Select ready `Pending` messages whose predecessors are complete.
2. Set `Processing`, `locked_by`, and `locked_until_utc`.
3. Commit the claim transaction.
4. Execute handlers outside the transaction.
5. Mark complete in a second short transaction.

A worker that dies in step 4 leaves an expired lease. Another worker returns the message to
`Pending` and executes it again.

### Initial Concurrency Model

Start conservatively:

- Maximum 4-8 concurrent operations globally, configurable.
- Only one active operation per `aggregate_key`.
- Use `share:{shareId}` as the initial aggregate key. This serializes side effects per share and
  makes rename/delete ordering straightforward.
- After production measurements, replace share-wide serialization with sorted path locks for
  unrelated subtrees.

The worker should use `PeriodicTimer`, wake early when new work is signaled, and honor application
shutdown cancellation. A graceful stop stops claiming, finishes current handlers within a timeout,
then leaves leases to expire if necessary.

### Handler Registry

```csharp
public interface IOutboxEffectHandler
{
    string EffectType { get; }
    Task HandleAsync(OutboxEnvelope message, CancellationToken ct);
}
```

Handlers should be small adapters over existing services:

- `MetadataLifecycleEffectHandler` -> `IAclRepository` / metadata repository
- `VersionLifecycleEffectHandler` -> `IFileVersionService`
- `VersionBlobGcEffectHandler` -> `IVersionBlobGarbageCollector`
- `SearchProjectionEffectHandler` -> `ISearchService`
- `ShareCleanupEffectHandler` -> share and role repositories

### Retry Backoff

Recommended schedule with jitter:

- Attempts 1-3: 1 s, 5 s, 15 s
- Attempts 4-8: 1 min, 5 min, 15 min, 30 min, 1 h
- Attempts 9+: every 6 h until dead-letter threshold or operator action

Use separate retry budgets by effect. Elasticsearch downtime should tolerate a longer budget than
an invalid path payload.

---

## 9. Reconciliation Worker

The outbox recovers known operations. Reconciliation covers unknown, incomplete, or historically
inconsistent state.

### 9.1 Fast Journal Recovery

Run at startup and every minute:

- Find non-terminal operations whose `updated_at_utc` is older than a safety threshold.
- Probe source and destination filesystem state.
- Advance the operation, retry the mutation only when safe, or mark `NeedsReconciliation`.
- Recreate missing outbox messages from the operation's deterministic effect plan.

Example rename recovery:

| Source Exists | Destination Exists | Action |
|---|---|---|
| Yes | No | Filesystem mutation likely not applied; retry if preconditions still match |
| No | Yes | Treat as applied; enqueue missing effects |
| Yes | Yes | Ambiguous/replace conflict; dead-letter for policy or operator review |
| No | No | Missing data; dead-letter and alert |

### 9.2 Filesystem -> Database Scan

Run incrementally per share:

- Enumerate live files/directories, excluding `.versions`, `.dp-keys`, snapshot cache, and other
  internal folders.
- Ensure metadata exists for filesystem entries.
- Preserve an existing owner. For an unjournaled new entry, use a designated system owner or no
  explicit owner according to a product decision; never invent a user's identity.
- Enqueue search upserts when the indexed projection is missing or stale.
- Do not hash every large file on every scan. Use size and mtime as the first comparison and hash
  only when required.

### 9.3 Database -> Filesystem Scan

- Find metadata paths with no filesystem entry.
- Apply a grace period before deletion to avoid racing an in-flight rename or temporarily
  unavailable mount.
- Check active journal operations before declaring a row stale.
- Delete stale metadata recursively through the same idempotent lifecycle handler.
- Version-history removal follows the configured product policy. The current policy is permanent
  cleanup on confirmed delete.

### 9.4 Version Row -> Blob Scan

- Verify that every distinct `StoragePath` referenced by a version row exists.
- Missing referenced blobs are data-loss incidents: mark the version unavailable and alert.
  (`FileVersion` has no "unavailable" state today — this needs an additive schema column, e.g.
  `blob_missing_at_utc`, before the scan can record the incident non-destructively.)
- Do not silently delete the database row unless an explicit repair policy says so.

### 9.5 Blob -> Version Row Scan

- Enumerate `*.bin.gz` under `.versions`.
- Ignore `.read-cache` and temporary files younger than a grace period.
- Delete blobs with no database reference after the grace period.
- Use the same bounded blob lock and reference re-check immediately before deletion.
- Remove empty hash fan-out directories.

### 9.6 Search Reconciliation

Elasticsearch is a projection and may be rebuilt:

- Compare by stable path-derived document ID and modified timestamp.
- Upsert missing/stale documents.
- Remove documents whose filesystem path no longer exists after the grace period.
- Provide an admin-triggered full rebuild in addition to incremental repair.

### Scan Safety

- A missing/unmounted share root must never be interpreted as "all files deleted".
- Before destructive reconciliation, verify that the configured storage root and share root exist
  and are on the expected volume.
- Stop and alert when a scan would remove more than a configurable percentage or absolute count.
- Default destructive repairs to dry-run during the first rollout phase.

---

## 10. Samba VFS Integration

The current Samba bridge reports close, mkdir, delete, and rename after native operations. Harden
the protocol as follows.

### Protocol Changes

Add to every mutation notification:

- `operation_id` (UUID generated once by the VFS module/sidecar)
- `event_sequence`
- `observed_at_utc`
- normalized old/new path
- `is_directory`
- optional resulting size and mtime

For delete/rename/mkdir, the preferred flow is two-phase:

1. The existing pre-operation authorization request also creates or returns an `operation_id`.
2. Samba performs native I/O.
3. Completion notification reports success and observed destination.
4. On native failure, a failure notification cancels the prepared operation.

If Samba or the bridge dies after step 2, the prepared journal record allows recovery. If no
prepared record exists, periodic reconciliation remains the fallback.

### Event Delivery

- The sidecar should retry completion events locally with bounded exponential backoff.
- A small durable sidecar spool is optional but recommended if bridge outages must not rely on a
  later full filesystem scan.
- Duplicate delivery is expected and safe.
- The bridge acknowledges only after the journal/outbox transaction commits, not after merely
  accepting the gRPC request in memory.

### Write-close Events

Write content remains native and is not journaled per write. On close:

- Emit a stable close operation ID.
- Record observed size and mtime.
- Enqueue ownership, version creation, and search indexing.
- Reconciliation may re-emit a missed close based on mtime drift, but version creation remains
  content-hash idempotent.

---

## 11. Idempotency, Ordering, and Concurrency

### General Rule

Every handler must produce the same final state when called once, twice, or after partial success.

### Metadata

- Ensure/create uses an upsert on `(ShareId, Path)`.
- Delete removes the exact path plus the segment-bounded descendant prefix.
- Rename checks both old and new state. If the source is already absent and the expected source
  metadata is at the destination, return success.
- `Name` is always derived from the final normalized path.
- Replace-style rename keeps source identity, owner, and ACL, and removes displaced destination
  metadata.

### Versions and Blobs

- Version create is idempotent by share, path, and content hash for the latest content.
- Version rename is segment-bounded and safe when the history is already at the destination.
- Version delete returns affected blob paths.
- Blob delete re-checks references while holding a bounded per-blob lock.
- Blob-not-found during GC counts as success.

### Search

- Upsert uses the stable document ID.
- Delete-by-ID/path is successful when the document is already absent.
- Rename can be implemented as destination upsert followed by source delete.
- Search effects execute after metadata/version path effects for the same operation.

### Operation Deduplication

- `operation_id` is the primary idempotency key.
- Reconciliation-generated repairs derive a deterministic key from repair type, share, path, and
  observed generation/checkpoint to prevent endless duplicate messages.

### Ordering

Initial implementation serializes lifecycle effects per share. When moving to path-level
parallelism:

- Lock rename source and destination keys in ordinal sorted order.
- A directory key conflicts with every descendant key.
- Do not process a later sequence for an operation until the previous sequence is complete.
- Do not allow a newer operation on the same aggregate to overtake a retrying older operation.

The last rule needs a concrete mechanism, not just intent. `sequence` orders effects *within* one
operation but resets per operation, so it cannot order *between* operations. Enforce cross-operation
order with a per-aggregate monotonic ordinal (see [§5.2](#52-outbox_messages)): the claim query
must skip any message whose operation has an earlier-ordinal, non-terminal sibling on the same
`aggregate_key`. Without this, a per-share `aggregate_key` still lets two operations interleave and
break causal renames.

> **Throughput caveat of `aggregate_key = share:{shareId}`.** Every file close enqueues version +
> search effects, so on a busy SMB share the entire write hot path funnels through one serialized
> aggregate slot. Keep the volume-heavy, latency-sensitive effects (metadata, version) inline in the
> initial phases and reserve the outbox for the rebuildable/deferrable effects (search, blob GC).
> Only move version/metadata fully behind the worker once path-level locking replaces share-wide
> serialization, otherwise the outbox becomes the write bottleneck.

---

## 12. Retry, Dead-Letter, and Repair Policy

### Error Classification

| Class | Examples | Action |
|---|---|---|
| Transient | DB timeout, ES unavailable, temporarily locked blob | Retry with backoff |
| Concurrency | row-version conflict, lease lost | Reload and retry quickly |
| Already applied | missing source after successful rename, blob already absent | Mark success after verification |
| Invalid payload | path traversal, unknown schema version | Dead-letter immediately |
| Contradictory state | both rename paths missing, unexpected destination identity | Needs reconciliation/operator review |
| Permanent authorization/config | share removed unexpectedly, invalid root | Dead-letter and alert |

### Dead-Letter Operations

Provide an admin/CLI workflow to:

- list dead-letter messages and parent operations;
- inspect sanitized payload, attempts, and last error;
- retry after correcting infrastructure;
- mark ignored with an operator reason;
- trigger path/share reconciliation;
- export a diagnostic bundle without file contents.

Never silently drop a message after exceeding retries.

---

## 13. Observability and Operations

### Metrics

- `file_outbox_pending_total`
- `file_outbox_oldest_pending_seconds`
- `file_outbox_processing_total`
- `file_outbox_retry_total{effect_type}`
- `file_outbox_dead_letter_total{effect_type}`
- `file_operation_nonterminal_total{state}`
- `file_operation_oldest_nonterminal_seconds`
- `file_reconciliation_drift_total{drift_type}`
- `file_reconciliation_repairs_total{result}`
- `version_blob_orphan_bytes`
- `version_blob_missing_referenced_total`
- `search_projection_lag_seconds`

### Logging

Every log entry should include:

- operation ID and outbox message ID;
- share ID;
- transport;
- effect type and attempt;
- source/destination path using the existing path privacy policy;
- worker instance ID.

Use one informational completion log per operation, not one noisy log per polling cycle.

### Alerts

Suggested initial alerts:

- oldest pending message > 15 minutes;
- any missing referenced version blob;
- any dead-letter message;
- reconciliation destructive safety threshold reached;
- worker has not completed a poll for 5 minutes;
- orphan blob bytes grow across three consecutive scans.

### Health Endpoints

- **Liveness:** worker process and polling loop are alive.
- **Readiness:** database reachable and worker can renew leases.
- Elasticsearch failure does not make the file service unready, but it degrades search health.

---

## 14. Security and Data Handling

- Validate and normalize every path again in the handler, even if the producer already did so.
- Never allow a payload path to escape its configured share root.
- Do not place file content, NT hashes, passwords, or JWTs in journal/outbox payloads.
- Limit `last_error` length and sanitize connection strings and credentials.
- Restrict outbox/dead-letter admin operations to explicit management permissions.
- Treat operation IDs as correlation identifiers, not authorization tokens.
- Encrypt PostgreSQL connections and protect database backups because paths and user IDs are
  operationally sensitive.

---

## 15. Implementation Phases

| Phase | Work | Exit Criterion |
|---|---|---|
| **0 - Baseline and decisions** | Record current drift/backlog metrics; decide owner fallback, version-on-delete policy, retention windows | Decisions documented; no behavior change |
| **1 - Schema and repositories** | Add journal/outbox/checkpoint migrations, repositories, leasing, optimistic concurrency | Repository integration tests pass on PostgreSQL-compatible semantics |
| **2 - Idempotent effect handlers** | Extract metadata/version/blob/search/share handlers from current lifecycle helpers | Every handler passes duplicate and partial-success tests |
| **3 - Coordinator for Web/FileHandle** | Journal before filesystem mutations; enqueue effects after observed success | Crash-window tests recover create/rename/delete/recycle |
| **4 - Outbox worker** | Host service, leases, backoff, dead-letter, metrics | Multiple worker instances process safely; restart recovery verified |
| **5 - Samba operation IDs** | Extend proto/VFS/sidecar; persist before or immediately after native mutations | Duplicate/lost/reordered notification tests pass |
| **6 - Fast journal reconciliation** | Resume stale prepared/applied operations | Forced process exits at every boundary recover automatically |
| **7 - Full drift scanners** | Filesystem/DB/version/blob/search scans with checkpoints and safety limits | Seeded orphan/stale/missing cases are detected and repaired |
| **8 - Production rollout** | Shadow mode, canary shares, enable repair gradually, operational runbook | Backlog remains bounded; no destructive false positives |

### Recommended Delivery Slices

1. Deliver outbox for Elasticsearch first. It is rebuildable and the lowest-risk proving ground.
2. Add blob GC messages next, including orphan scan and safety tests.
3. Move metadata/version rename and delete effects behind the coordinator.
4. Add Samba operation IDs and lost-event recovery.
5. Enable destructive reconciliation only after at least one week of dry-run observations.

---

## 16. Test Strategy

### Unit Tests

- Deterministic effect-plan generation for every operation type.
- State transition validation and optimistic concurrency.
- Retry classification and backoff calculation.
- Payload schema-version dispatch.
- Idempotency of every handler.
- Segment-boundary paths (`folder` must not match `folder-other`).
- Rename source/destination lock ordering.

### Database Integration Tests

- Enqueue operation and outbox effects in one transaction.
- Unique constraints reject duplicate enqueue without losing the original.
- Two workers using `SKIP LOCKED` never claim the same row concurrently.
- Expired lease is reclaimed.
- Earlier sequence blocks later sequence.
- Dead-letter and manual retry lifecycle.
- PostgreSQL transaction rollback leaves neither half-created operation nor messages.

The application targets **PostgreSQL only** (`Npgsql.EntityFrameworkCore.PostgreSQL`); there is no
second production provider. Lease, `SKIP LOCKED`, and `bigserial` ordinal semantics do not exist in
SQLite, so all leasing/ordering/claim tests must run against real PostgreSQL. Use SQLite (if at all)
only for provider-agnostic repository logic — never to validate concurrency behavior.

### Filesystem Integration Tests

- Crash after `Prepared`, before filesystem mutation.
- Crash after filesystem rename/delete, before DB/outbox commit.
- Crash after one effect, before message acknowledgement.
- Recycle collision returns and persists the actual path.
- Directory operations update/delete complete subtrees.
- Blob referenced by multiple versions survives until the last reference is gone.
- Active snapshot read does not block blob cleanup.
- Unmounted share root triggers safety stop, never mass deletion.

### Samba End-to-End Tests

- Duplicate completion notification.
- Completion notification delivered after worker restart.
- Bridge unavailable during rename/delete/close.
- VFS/sidecar killed after native mutation.
- Reordered events on the same path.
- Rapid write-close cycles remain version-idempotent.

### Chaos/Soak Tests

- Repeatedly kill Host, Web, SmbBridge, Samba, PostgreSQL, and Elasticsearch at randomized
  lifecycle boundaries.
- Run a mixed workload of create/write/rename/recycle/delete for several hours.
- After services stabilize, assert zero non-terminal operations, zero unreferenced old blobs,
  matching filesystem/metadata paths, and a rebuild-equivalent search index.

---

## 17. Deployment and Rollback

### Feature Flags

Suggested settings:

```json
{
  "FileLifecycle": {
    "JournalEnabled": false,
    "OutboxWorkerEnabled": false,
    "InlineEffectsEnabled": true,
    "Reconciliation": {
      "Enabled": false,
      "DryRun": true,
      "DestructiveRepairsEnabled": false
    }
  }
}
```

### Rollout Sequence

1. Deploy additive database migration.
2. Enable journal writes while current synchronous effects remain authoritative.
3. Enable worker in shadow mode; handlers report intended changes without mutating.
4. Move Elasticsearch and blob GC to authoritative outbox handling.
5. Move metadata/version effects to outbox, retaining optional inline dispatch.
6. Enable reconciliation dry-run and inspect drift reports.
7. Enable non-destructive repair.
8. Enable destructive repair for canary shares, then globally.

### Rollback

- Disable workers and reconciliation through configuration.
- Keep writing journal/outbox records during short rollback windows if possible.
- The migration is additive; old code ignores the tables.
- Do not drop tables during rollback. Pending work must remain available for a later redeploy.
- Re-enable current synchronous lifecycle helpers if outbox handling is disabled.

---

## 18. Definition of Done

The hardening is complete when all of the following are true:

- Every mutating Web/FileHandle operation has a durable operation ID before filesystem mutation.
- Every Samba mutation notification has a stable operation ID and commit-backed acknowledgement.
- Every lifecycle effect is represented by an idempotent handler.
- Outbox enqueue and journal advancement are transactional.
- Multiple workers safely use leases and `SKIP LOCKED`.
- Stale operations recover after forced process termination at every boundary.
- Periodic reconciliation detects filesystem, metadata, version, blob, and search drift.
- Destructive reconciliation has grace periods, mount checks, and mass-delete safety limits.
- Blob GC never deletes referenced content and eventually removes unreferenced content.
- Dead-letter messages are visible and manually retryable.
- Metrics and alerts cover queue age, failures, drift, and missing version blobs.
- PostgreSQL integration, filesystem failure-injection, Samba E2E, and soak tests pass.
- An operator runbook documents backlog diagnosis, dead-letter repair, full reconciliation, and
  safe worker shutdown.

---

## 19. Open Decisions

Resolve these before Phase 2 because they affect handler semantics:

1. **Deleted-file version policy:** permanently remove all history, or retain tombstoned history
   for a configured recovery period? Current behavior is permanent cleanup.
2. **Unjournaled owner fallback:** system owner, inherited-only ACL, or quarantine until reviewed?
3. **Samba durability:** rely on reconciliation after a lost event, or add a durable local sidecar
   spool for faster recovery?
4. **Initial ordering scope:** share-wide serialization as recommended, or path-level locks from
   the first release?
5. **Inline effects:** should metadata/version effects finish before the client receives success,
   or is durable enqueue sufficient?
6. **Historical orphan cleanup:** delete immediately after the first verified scan, or retain for
   a manual review window?
7. **Administration surface:** CLI only for the first release, or expose dead-letter/reconciliation
   controls in the Web UI?

The recommended defaults are: permanent version cleanup, inherited-only metadata for unknown
owners, reconciliation plus a small Samba spool, share-wide ordering, inline metadata/version
effects, a seven-day historical-orphan quarantine, and CLI-first administration.

---

## 20. Critical Review — Prioritized Concerns

This section captures a review pass against the current `FileService` / `FileVersionService` /
Samba bridge implementation. The corrections are already folded into the relevant sections above;
this is the meta-view and priority order.

### Must fix before building the effect handlers

1. **Version content must be captured at close time, not re-read by a deferred handler.**
   `FileSession.DisposeAsync` snapshots the open stream today. A deferred version-create effect that
   re-reads the live file will version the wrong content if another writer intervened. Keep version
   creation inline, or stage the content-addressed blob synchronously at close and defer only the DB
   row + retention. See [§7](#7-central-lifecycle-coordinator) and [§10](#10-samba-vfs-integration).

2. **Cross-operation ordering has no mechanism.** `sequence` only orders effects inside one
   operation. Without a per-aggregate monotonic ordinal, two operations on the same share can
   interleave and break causal renames (`A->B->C`). Add the ordinal and make it part of the claim
   predicate. See [§5.2](#52-outbox_messages) and [§11](#11-idempotency-ordering-and-concurrency).

3. **Inline execution and the worker can double-claim the same message.** Idempotency makes this
   safe but wasteful and reorder-prone. Inline paths must lease the message exactly like the worker.
   See [§7](#7-central-lifecycle-coordinator).

### Should address in the design

4. **`aggregate_key = share` is a throughput bottleneck for the write hot path.** Every close
   enqueues version + search effects through one serialized slot. Keep volume-heavy effects inline
   until path-level locking exists. See [§11](#11-idempotency-ordering-and-concurrency).

5. **Bulk operations (`ArchiveAsync`, `UnzipAsync`, folder restore) are unmodeled.** They are not in
   the operation-type list or effect plans and can each touch thousands of files. Journal them as one
   bulk operation or defer them entirely to the FS->DB scan — never one journal row per file. Added
   to [Non-Goals](#2-goals-and-non-goals).

6. **The "missing referenced blob → mark unavailable" repair needs a schema field.** `FileVersion`
   has no such state today; add an additive column before [§9.4](#9-reconciliation-worker) can
   record the incident.

7. **PostgreSQL-only.** The repo has a single Npgsql provider; the earlier SQLite phrasing was
   misleading. All concurrency/lease/ordinal tests must run on real PostgreSQL. See
   [§16](#16-test-strategy).

### Scope / sequencing opinion

The design is sound and appropriately conservative on the dangerous part (destructive
reconciliation safety in [§9](#9-reconciliation-worker) is the strongest section). The main risk is
**scope**: this is a multi-month build layered onto a service whose side effects are currently
best-effort and mostly working. The recommended delivery order in [§15](#15-implementation-phases)
is right — **ship the Elasticsearch outbox first** (rebuildable, lowest blast radius), then blob GC,
and treat everything else as demand-driven by the drift/backlog metrics from Phase 0. Do not build
the Samba two-phase protocol, path-level locking, or full drift scanners until Phase 0 numbers show
they are actually needed; the fast journal recovery ([§9.1](#9-reconciliation-worker)) plus the
existing best-effort handlers may already cover the observed failure rate.

One caveat on the Samba two-phase flow ([§10](#10-samba-vfs-integration)): correlating a pre-op
authorization request with the later completion event requires a stable ID threaded through the VFS
layer, which SMB does not provide natively. Confirm the bridge/sidecar can actually carry that
correlation before committing to two-phase; otherwise reconciliation remains the honest fallback.
