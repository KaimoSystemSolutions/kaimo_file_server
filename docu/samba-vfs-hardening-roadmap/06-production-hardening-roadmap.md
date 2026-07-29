# Production Hardening Roadmap

[← Table of contents](README.md)

## 10. Production-hardening roadmap

### 10.1 Target state and definition of done

Samba is ready to become Kaimo's production security boundary only when all of
the following statements are continuously true:

1. Every live and historical access is authorized against the exact target and
   fails closed on malformed input, missing context, timeout, or infrastructure
   failure.
2. Disabling a service, share, or user converges within a documented revocation
   SLA; stale credentials and active sessions cannot silently outlive it.
3. Reusable NT hashes never remain in predictable files, logs, process
   arguments, or unmanaged long-lived buffers.
4. Version, ownership, ACL-path, metadata, and search events survive crashes and
   retries without duplication, misattribution, or loss.
5. Snapshot materialization is atomic, content-verified, quota-bounded,
   race-safe, cancellation-aware, and inaccessible outside an authorized
   read-only timewarp operation.
6. Every long-running security component is supervised and observable.
   Operations can detect auth failures, queue pressure, event lag, failed
   convergence, certificate expiry, and version/ABI drift.
7. CI and release tests prove the supported SMB operation matrix against the
   pinned Samba build. A release cannot bypass these gates.

The release rule is simple: milestones 1–3 close the remaining correctness and
security blockers. Milestone 4 supplies the evidence and operational controls
required for production approval. Deferred product features such as recycle-bin
behavior and full per-user share hiding are not production blockers as long as
their current behavior remains explicit and tested.

### 10.2 Completed security baseline — maintain, do not reimplement

The following work is complete in source and is no longer part of the active
implementation backlog:

- Fail-closed connection context and default authorization behavior.
- Exact dynamically allocated request paths and bounded/versioned local frames.
- Complete SMB open-mask mapping and source/destination/replacement rename
  authorization.
- Fixed worker pool, bounded queue, strict I/O deadlines, and bounded LRU+TTL
  authorization cache.
- Private Unix socket with `SO_PEERCRED`, executable, UID, and claimed-user
  verification.
- Isolated mTLS control plane with RPC allow-lists and audited/rate-limited hash
  export.
- Private, locked NT-hash import using mode-0600 random files in a validated
  mode-0700 runtime directory with unconditional cleanup.
- Desired-state `tdbsam` reconciliation that revokes absent users while
  retaining locked POSIX identities and stable UIDs.
- Per-file ACL-filtered folder snapshots in an isolated per-share/per-user cache.
- Strictly read-only timewarp opens through `SMB_VFS_NEXT_OPENAT`.
- Bounded snapshot cleanup, fail-safe audit-module activation, and immediate
  share close when the global SMB service is disabled.

These controls still require regression coverage in milestone 4. Any regression
in this baseline is a release blocker.

### 10.3 Milestone 1 — Credential lifecycle and revocation

**Goal:** remove the shortest remaining paths to stale or exposed credentials
and make disabled identities converge predictably.

1. **Completed 2026-07-28:** removed `/tmp/kaimo.smbpasswd`; the file-backed
   `pdbedit` import now uses a locked, validated private runtime directory,
   `mktemp`, `umask 077`, ownership checks, cleanup traps, and immediate
   deletion.
2. **Completed 2026-07-28:** reconcile `tdbsam` and managed POSIX users to
   desired state. Deleted, disabled, or absent users lose passdb credentials
   and Kaimo group membership; their locked `nologin` POSIX identity and UID
   remain to preserve file ownership and permit safe reactivation.
3. **Completed 2026-07-28:** user/share/config synchronization fails on
   unapplied or unverifiable mutations, reads back passdb/registry/config
   state, rejects concurrent component runs, and exposes last-success/failure
   convergence through container health.
4. **Completed 2026-07-28:** replaced tab/newline synchronization records with
   versioned JSON envelopes independently validated by exporter and reconciler.
   Control characters, reserved/duplicate names, malformed hashes, invalid
   protocol values/ranges, unknown fields, and canonical paths outside the
   configured storage root fail before mutation.
5. Centralize enabled service/share/user resolution and use it in every Authz,
   Event, Snapshot, and synchronization RPC.
6. Define the revocation SLA and close affected shares/sessions when a service,
   share, or user is disabled. Explicitly decide how already-open handles are
   treated.
7. Remove reusable test credentials from production entrypoints and health
   probes. Reject `KAIMO_AUTHZ_FAILOPEN=1` in production configuration.
8. Paginate or stream bulk user export, isolate corrupt rows, enforce exactly
   16-byte NT hashes, and minimize/clear decrypted credential copies.

**Exit criteria:**

- No reusable hash is recoverable from predictable files, logs, command lines,
  or after a completed synchronization.
- Delete/disable tests remove or disable local credentials and block new SMB
  access within the stated SLA.
- Injected `pdbedit`, `net conf`, validation, bridge, and partial-sync failures
  make reconciliation unhealthy instead of reporting success.
- Every bridge operation rejects disabled service/share/user state.

### 10.4 Milestone 2 — Durable and correct lifecycle processing

**Goal:** make file history and secondary state correct across crashes,
concurrency, duplicate delivery, and reordering.

This milestone implements the detailed design in
`file-lifecycle-reconciliation-outbox-roadmap.md`.

1. Add durable local event spooling/outbox semantics with operation IDs,
   acknowledgement, bounded retry/backoff, dead-letter handling, retention, and
   observable lag.
2. Make all event handlers idempotent. A duplicate close creates no duplicate
   version; repeated delete succeeds; repeated rename never deletes valid
   destination history.
3. Capture the exact bytes associated with a closing SMB handle. Choose and
   implement staging copy/reflink, descriptor-aware capture, or a coordinated
   file-version transaction; path re-open after close is not sufficient.
4. Determine directory/file type before rename and send the correct lifecycle
   event.
5. Define concurrent-open and rapid-write semantics so version attribution is
   deterministic.
6. Add periodic reconciliation for missed or partially applied version,
   ownership, ACL-path, metadata, and search side effects.

**Exit criteria:**

- Crash/restart, network partition, duplicate, reorder, and retry tests converge
  database, versions, ownership, ACL paths, metadata, and search state.
- A recorded version always matches the bytes and user of the corresponding
  operation ID.
- Backlog, retry, dead-letter, and reconciliation state are observable and
  bounded.

### 10.5 Milestone 3 — Atomic and bounded snapshots

**Goal:** preserve the completed ACL and isolation model under concurrency,
corruption, cancellation, and resource pressure.

1. **Completed 2026-07-27:** materialize into a unique same-filesystem temporary file, enforce a byte
   limit while streaming, verify expected length and content hash, flush, and
   publish with an atomic rename.
2. **Completed in source 2026-07-28:** coordinate materialization, open,
   invalidation, and cleanup with keyed locks/leases. Cleanup must skip active
   projections. Pinned native/live SMB verification remains a release gate.
3. **Completed 2026-07-28:** enforce request-level file, byte, time, and
   concurrency quotas before and during folder materialization; remove
   abandoned output.
4. **Completed 2026-07-28:** propagate gRPC cancellation through repository
   calls, ACL filtering, decompression, copying, and materialization.
5. **Completed 2026-07-29:** harden snapshot response parsing with bounded
   counts, overflow checks, consistent framing, and deterministic newest-2,048
   truncation behavior.
6. Validate cache configuration at startup. **Content-identity cache
   validation completed 2026-07-29:** individual-file and folder cache hits
   require the immutable version SHA-256 digest as well as the expected length.
7. Invalidate projections on version/share deletion and ACL revocation, while
   retaining an authorization check on every open.

**Exit criteria:**

- Concurrent materialize/open/cleanup tests never expose partial, stale,
  corrupt, cross-user, or unauthorized content.
- Oversized, cancelled, malformed, or resource-exhausting requests fail closed
  within documented limits and leave no published partial projection.
- Windows Previous Versions and `smbclient` browse/copy/restore tests pass,
  including child denies and post-cache ACL revocation.

### 10.6 Milestone 4 — Coverage, supervision, and production release gate

**Goal:** turn implemented controls into a repeatable, observable release
guarantee.

1. Centralize path validation and canonicalization across every VFS hook and
   bridge service. Remove remaining fixed user/share context truncation.
2. Inventory every mutation reachable through Samba 4.19.5—including
   attributes, EAs, owner/security descriptor changes, hardlinks, symlinks,
   server-side copy, delete-on-close, and mkdir fallbacks—and authorize or
   explicitly deny it.
3. Add native parser/path unit tests, ASan/UBSan, targeted concurrency tests,
   fuzzing, OOM/fault injection, deep UTF-8 paths, and exact boundary tests.
4. Build and load the module against the pinned Samba version in CI. Assert
   Samba 4.19.5/ABI 49, run `testparm`, and execute the minimum SMB operation
   matrix for every relevant change.
5. Supervise `authd`; fail/restart the container when it is unavailable. Extend
   health checks to validate the authorization path without reusable
   credentials.
6. Add metrics and alerts for workers, queue pressure, cache use, timeouts,
   denials, bridge failures, sync convergence, revocation delay, event
   backlog/dead letters, certificate expiry, and snapshot cleanup.
7. Define SLOs, backup/restore and certificate-rotation procedures, and a
   documented Samba upgrade/ABI process. Exercise recovery.
8. Run the full container matrix: Samba/client interoperability, bridge/authd
   crash and recovery, network partitions, active-session revocation, and
   multi-user snapshot isolation.
9. Decide whether bridge redundancy is required by the availability SLO and
   implement it if a single fail-closed bridge outage is unacceptable.

**Exit criteria:**

- All section 11 release gates run automatically and pass against the exact
  deployable images.
- Operations can detect, diagnose, and recover every tested bridge, sidecar,
  synchronization, certificate, and event-pipeline failure.
- Security review signs off the complete live/historical operation matrix and
  the documented residual product decisions.

### 10.7 Recommended execution order

| Order | Workstream | Why now | Production blocker |
|---|---|---|---|
| 1 | Credential lifecycle and revocation | Removes reusable-hash residue and stale principals first | Yes |
| 2 | Durable lifecycle processing | Prevents silent loss or corruption of versions and secondary state | Yes |
| 3 | Atomic bounded snapshots | Closes concurrency and resource-exhaustion gaps in historical access | Yes |
| 4 | Coverage and operations | Proves the controls and makes failures detectable/recoverable | Yes |

Milestones may overlap only where their contracts are already decided. In
particular, lifecycle implementation must not begin before operation-ID,
stable-close-capture, and stale-user policies are recorded; snapshot work can
run independently after its lock/lease and quota contracts are fixed.
