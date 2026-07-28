# P1 – High-Severity Findings

[← Table of contents](README.md)

## 6. High-severity findings

### P1-01: Unbounded detached threads allow memory and file-descriptor exhaustion

> **Remediation status (2026-07-23): Implemented and native-load-tested.**
> `authd` now uses a fixed worker pool and bounded accepted-client queue.
> Accepted sockets receive configurable send/receive deadlines; excess clients
> receive `ERROR` and are closed without allocating another thread.

`authd` accepts each Unix-socket connection and starts a detached `std::thread`. The first `read()` has no receive timeout. A local client can open connections without sending data, consuming one thread, stack, and file descriptor per connection.

**Fix:** use a bounded worker pool or event loop, enforce connection and request deadlines, cap concurrent clients, and reject excess load predictably.

### P1-02: The authorization cache grows without a global bound

> **Remediation status (2026-07-23): Implemented and native-tested.** The
> decision cache is now an LRU bounded by both entry count and a conservative
> accounted byte budget. Expired entries are removed on lookup and by periodic
> opportunistic sweeps. The configured TTL is the documented maximum
> revocation delay for a cached decision.

Expired cache entries are removed only when the exact key is requested again. Unique file paths therefore accumulate indefinitely even though the advertised TTL is three seconds.

**Fix:** implement a size-bounded LRU/clock cache, periodic expiry, metrics, and a hard maximum memory budget. ACL changes should also support explicit invalidation or a documented maximum revocation delay.

### P1-03: Unix stream framing and partial I/O are incorrect

> **Remediation status (2026-07-23): Implemented and native-tested.** The VFS
> and sidecar now share a versioned binary envelope with enum operations and
> statuses, fixed request/response limits, length-prefixed UTF-8 fields, exact
> schema validation, and complete read/write loops. Fragmented, truncated,
> oversized, and wrong-version runtime cases are covered.

The native module assumes one `write()` sends the entire request. The sidecar assumes one `read()` receives the entire request. Replies are also written once. Stream sockets do not preserve application messages and may return partial reads/writes.

The tab/newline protocol also has no escaping. User, share, path, token, or future fields containing delimiters change the parsed message.

**Fix:** use a length-prefixed binary envelope, `read_exact`/`write_all`, explicit maximum frame size, versioning, enum operation types, and validation before allocation.

### P1-04: The Unix socket is world-writable and trusts claimed identity

> **Remediation status (2026-07-23): Implemented and live-tested.** The socket
> now lives in a root-owned `0750` directory, is published as `0660` for the
> dedicated `kaimo-authd` group, and every accepted connection is authenticated
> with `SO_PEERCRED`. Non-root callers may only claim the passwd identity
> matching their kernel UID; only a verified root `smbd` worker may carry the
> Samba-authenticated session username. Unauthorized peers receive a structured
> response and fail closed even when infrastructure fail-open is enabled.

`chmod(..., 0666)` permits every local process to submit requests. The sidecar does not inspect peer credentials and accepts `username` from the payload.

**Fix:** mode `0660`, dedicated service group, private directory permissions, `SO_PEERCRED` verification, and a design where the peer cannot choose an arbitrary Kaimo identity independently of the authenticated Samba session.

### P1-05: Native calls can block `smbd` workers for infrastructure timeouts

> **Remediation status (2026-07-23): Implemented and native/live-tested.**
> VFS clients now use nonblocking Unix sockets and one absolute monotonic
> deadline across connect, complete frame transmission, and complete response
> reception. Authorization, snapshot, and best-effort event traffic have
> separate bounded budgets.

Authorization and notification paths perform synchronous `connect()`/`write()`/`read()` calls without socket-level deadlines. gRPC deadlines in `authd` limit some downstream work, but a full backlog, stalled sidecar, or local socket failure can still block before the gRPC deadline applies.

**Implemented fix:** nonblocking connect with `poll`, deadline-aware complete
send/receive loops, strict end-to-end budgets, and a separate short event
enqueue budget. Durable asynchronous event delivery remains P1-11.

### P1-06: Snapshot opens bypass the VFS stack and do not enforce read-only access

> **Remediation status (2026-07-23): Implemented and verified against the
> pinned Samba 4.19.5 build and a live SMB3 timewarp regression.**

The timewarp branch skips normal `AuthorizeOpen`, then calls raw `openat(AT_FDCWD, absolutePath, how->flags, how->mode)`. This can preserve write/truncate/create flags and bypass `full_audit` or later VFS modules.

**Fix:** reject all non-read access for timewarp paths, strip unsafe flags defensively, and redirect through the next VFS module using Samba-supported path/FSP handling. Verify behavior against Samba 4.19.5's own shadow-copy modules.

**Implemented fix:** timewarp CREATE requests now accept only `FILE_OPEN`
without mutating access, allocation, EA, security-descriptor, or
delete-on-close intent. Granted rights are intersected with both the expanded
client request and the snapshot read/execute mask. The low-level open rejects
write/create/truncate/append flags, defensively rebuilds an `O_RDONLY`
`vfs_open_how`, and calls `SMB_VFS_NEXT_OPENAT` with a validated synthetic
snapshot filename. Disabled redirects fail closed instead of serving the live
file. Timewarp delete, rename, and mkdir paths also return `EROFS`.

### P1-07: Snapshot materialization is non-atomic and only validates file size

> **Remediation status (2026-07-27): Implemented; focused and complete managed
> regression suites pass. Live SMB/container verification remains separate.**

The bridge writes directly to the final cache filename using `FileMode.Create`. Another reader can race with a partial write, and a crash can leave a same-sized corrupt file that is subsequently reused.

**Fix:** write to a unique temporary file on the same filesystem, stream with a byte cap, verify expected length and content hash, flush/fsync, apply safe mode/timestamps, and atomically rename into place.

**Implemented fix:** existing cache entries are reused only after exact length
and SHA-256 verification. New content is streamed into a unique sibling
temporary file with the declared uncompressed size as a hard cap, and both the
final length and stored content hash must match. The bridge then flushes the
file to durable storage, applies projection mode and historical mtime, and
atomically replaces the final entry. Every failure path removes the temporary
file and returns `Found=false`.

### P1-08: Snapshot materialization and cleanup are unsynchronized

> **Remediation status (2026-07-28): Implemented in bridge, local protocol,
> and native VFS source; managed tests pass. Pinned native build and live SMB
> regression remain pending because the Docker daemon was unavailable.**

The background cleanup service can delete a token directory while another RPC is materializing or while Samba is traversing it. Unix open file descriptors may survive deletion, but directory traversal and later child opens can fail inconsistently.

**Fix:** keyed locks/leases per share+token, atomic directory publication, cleanup that skips active leases, and tests that interleave materialization, enumeration, open, and eviction.

**Implemented fix:** a singleton keyed coordinator serializes each
share+token materialization. A protected `.kaimo-lease` file extends the
boundary across the bridge and `smbd`: materialization and eviction require an
exclusive `flock`, while native stat/open/directory handles hold shared locks.
`ResolveVersion` returns an opaque handoff id while retaining a shared bridge
lease; the VFS first acquires its native shared lock and then acknowledges the
handoff through a dedicated RPC/local-protocol operation. Unacknowledged
handoffs expire after 30 seconds. Cleanup uses nonblocking exclusive locks and
skips active tokens, including orphan-share cleanup.

### P1-09: Folder materialization is unbounded and ignores cancellation

> **Remediation status (2026-07-28): Implemented and managed-tested. Native/live
> SMB verification remains pending.**

A single folder snapshot request may decompress every historical file under a prefix. The per-share cap is enforced later by the background sweeper, not before or during materialization. gRPC cancellation tokens are not propagated.

**Fix:** request-level file/byte/time quotas, preflight capacity reservation, incremental/lazy materialization, cancellation propagation, concurrency limits, and cleanup of abandoned temporary output.

**Implemented fix:** the bridge now preflights the ACL-filtered projection
against configurable file and byte limits before changing cache content, then
reserves one slot from a process-wide bounded concurrency gate. A linked
request timer covers folder metadata lookup, ACL filtering, cache validation,
decompression, hashing, copying, and publication. Cancellation is propagated
through the EF query, file-version read/decompression, content verification,
and asynchronous file I/O. The eager projection required by Samba's relative
child opens remains, but it is streamed incrementally within strict bounds.
Aborted requests remove same-directory temporary files and any final projection
files newly created by that request.

### P1-10: Disabled shares remain valid in bridge authorization

> **Remediation status (2026-07-28): Implemented; managed tests and pinned
> native container build pass. Live active-session revocation verification
> remains pending.**

`ShareRepository.GetByNameAsync()` returns disabled definitions. Connect, Open, Delete, Event, and Snapshot services generally check only for `null`, not `IsEnabled`.

**Impact:** during the polling/reconciliation interval, new connections or internal RPC callers may continue to use a disabled share. Existing open handles are not revoked by per-open authorization.

**Fix:** centralize `ResolveEnabledShareAsync`, use it in every bridge service, immediately close the share on disable, and document/implement active-handle revocation semantics.

**Implemented fix:** every bridge authorization, event, and snapshot share
lookup now passes through one cancellation-aware enabled-share resolver.
Disabled shares therefore fail closed immediately after the database change is
visible to the bridge. The Samba reconciler runs on a configurable two-second
default interval; it removes disabled shares from the registry and forcibly
disconnects all active tree connections with `smbcontrol close-share`. The
bounded revocation semantics and bridge-outage limitation are documented in
the Samba VFS runbook.

### P1-11: Event delivery is lossy and has no retry-safe contract

> **Remediation status (2026-07-28): Implemented; managed tests and the pinned
> Samba 4.19.5 native container build pass. Live bridge-outage/restart and
> dead-letter operational regressions remain pending.**

VFS notifications are described as fire-and-forget. The VFS does not wait for a meaningful acknowledgement, the sidecar does not durably spool events, and gRPC status/reply values are ignored by event handlers.

**Impact:** version creation, owner stamping, metadata cleanup, ACL path updates, and search indexing can be silently missed.

**Fix:** use a durable local outbox/spool, event IDs, explicit acknowledgements, bounded retry with backoff, dead-letter handling, and idempotent server-side processing.

**Implemented fix**

1. `authd` generates a UUID per accepted lifecycle event and persists the exact
   bounded local-protocol payload under an owner-only spool using a `0600`
   temporary file, file `fsync`, atomic `rename`, and directory `fsync`.
2. The VFS receives `OK` only after this durable publication. gRPC delivery runs
   on a separate dispatcher and no longer consumes an `smbd` worker's event
   budget.
3. Delivery requires both a successful gRPC status and `NotifyReply.ok`.
   Failures use bounded exponential backoff; exhausted events move to a bounded
   dead-letter directory. Pending/dead capacities, attempts, and retry bounds
   are validated environment settings.
4. The spool is mounted as a dedicated Compose volume and recovered on `authd`
   restart. Unsafe ownership/modes, symlinks, malformed records, and incompatible
   record versions fail closed.
5. All event protobuf requests carry the stable ID. The bridge claims it through
   the migrated `samba_lifecycle_event_receipts` table with a crash-reclaimable
   lease, acknowledges completed duplicates without re-running effects, rejects
   cross-type ID reuse, and expires completed receipts after a configurable
   retention period.
6. External lifecycle helpers now surface ownership/version/search/ACL failures
   to the bridge instead of logging them as success, so failed effects keep the
   event unacknowledged.

P1-11 provides at-least-once delivery and post-completion deduplication. The
known crash window inside multi-effect handlers is intentionally not described
as exactly-once: P1-12 must bind close versions to exact bytes, P1-13 must make
rename effects independently idempotent, and the Milestone-B operation
journal/outbox remains the full cross-system recovery design.

### P1-12: Close processing can version the wrong content or assign the wrong user

> **Remediation status (2026-07-28): Implemented; managed tests and the pinned
> Samba 4.19.5 native/container build pass. Live concurrent-writer and outage
> verification remains pending.**

`NotifyExternalCloseAsync` reopens the file by path after the SMB close. Between the native close and the bridge read, another client may modify, rename, replace, or delete the path. Concurrent handles make the attribution problem worse.

**Fix options:**

- Create an immutable staging copy/reflink at the native close boundary and send its identity to the bridge.
- Introduce a coordinated file-version transaction/lock shared by SMB and the bridge.
- Move the version snapshot into a component that can read the exact closing file descriptor before it is released.

Whichever option is chosen must preserve the data-path goals while guaranteeing that version bytes correspond to the reported close event.

**Implemented fix**

1. Before `SMB_VFS_NEXT_CLOSE`, the VFS captures the regular file through
   `fsp_get_io_fd`; it never reopens the live pathname. `FICLONE` is preferred,
   with a `pread`/`pwrite` fallback whose pre/post inode, size, mtime, and ctime
   checks reject a source modified during copying.
2. Captures are flushed, made read-only, and atomically no-replace-published
   below the root-provisioned `.kaimo-close-captures` directory. Every
   `.kaimo-*` client path is denied by the VFS.
3. The version-3 local CLOSE payload binds authenticated user, share, logical
   path, and opaque capture ID before `authd` durably enqueues it.
4. The gRPC contract exposes only the opaque ID. The bridge derives its path
   below the resolved share root and uses a fresh stream over the immutable
   capture for both versioning and indexing; it no longer reads the live path.
5. Captures remain beside the durable event across retries and dead-lettering.
   The bridge deletes one only after completing the stable event receipt;
   completed duplicate delivery remains successful after that deletion.
6. The closing handle's Samba connection identity crosses the already
   peer-authenticated local channel and is resolved to the exact Kaimo user
   before ownership/version attribution.

### P1-13: Rename events are not idempotent and can delete version history

> **Remediation status (2026-07-28): Implemented and managed-tested. Live
> bridge-crash/retry verification remains pending.**

`FileVersionRepository.RenamePathAsync()` removes destination versions not present in the source set. If the same rename event is delivered twice, the second call has an empty source set and can treat all destination versions as displaced.

**Fix:** make rename lifecycle processing idempotent by event ID and state transition. A repeated already-applied old→new rename must be a no-op, never a destructive destination cleanup.

**Implemented fix:** the stable Samba lifecycle event ID now reaches the version
repository. For Samba renames, destination displacement, source-history movement,
and a `RenameVersionsCompletedAtUtc` receipt checkpoint commit in one serializable
database transaction. A retry that observes the checkpoint returns without
touching either path, including versions created at the destination after the
original rename. Non-Samba rename callers retain the existing behavior.

### P1-14: Directory rename events are reported as file renames

> **Remediation status (2026-07-28): Implemented, regression-tested, and built
> against pinned Samba 4.19.5. Live SMB/search verification remains pending.**

The VFS hardcodes `is_directory = 0` in rename notifications. ACL and version path-prefix updates happen independently of this flag, but search lifecycle behavior chooses the file rename callback instead of the directory rename callback.

**Fix:** determine object type before the native rename using the source FSP/stat data and send the correct value.

**Implemented fix:** P0-04 already introduced a fail-closed source `FSTATAT`
before the native rename and used its mode for both authorization and the
durable lifecycle payload. P1-14 adds a shared, native-tested mode-to-event
mapping plus managed regressions proving the directory flag survives the gRPC
boundary and selects the directory search lifecycle callback.

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
