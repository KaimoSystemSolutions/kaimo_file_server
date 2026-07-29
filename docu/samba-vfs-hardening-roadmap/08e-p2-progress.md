# Implementation Progress – P2 Defense in Depth

[← Implementation progress index](08-implementation-progress.md)

### 2026-07-28 — P2-01: Exact, bounded user/share connection context

**Status:** Implemented, regression-tested, and verified in the pinned Samba
4.19.5 `build-runtime` image.

The VFS connection context stored usernames and share names in fixed 128-byte
arrays. `strlcpy` silently shortened longer values after the connect decision,
so later open, listing, lifecycle, and snapshot calls could carry a different
identity or share prefix than the authorized TREE_CONNECT.

**Implemented**

1. `kaimo_conn_ctx` now owns separately allocated, exact username and share
   strings. Allocation is completed before `SMB_VFS_NEXT_CONNECT`; every
   partial-allocation, next-layer failure, handle-registration failure, and
   normal destruction path releases both strings and the context.
2. `local_protocol.h` is the native source of truth for the Samba-facing
   32-byte username and 64-byte share limits. Its validators enforce the same
   ASCII syntax, leading/trailing-dot, and reserved-section rules used by the
   P1-18 synchronization records.
3. The VFS rejects malformed context before the authorization roundtrip.
   `authd` validates it again after framed parsing and before peer lookup,
   caching, durable spooling, or gRPC.
4. Core account/share constructors enforce the same constraints. Share
   repository create/update and both administration view models retain
   defense in depth for creation and rename paths.
5. Auth, Authz, Event, and Snapshot RPC entry points reject malformed or
   oversized user/share context before repository, ACL, lifecycle, or snapshot
   work.

**Validation completed**

- The complete managed suite passes: 572 tests.
- New domain boundary tests cover exact 32/64-byte limits, overflow, non-ASCII,
  separators, leading/trailing dots, and reserved names.
- A managed ingress regression proves an oversized username is denied before
  any user, share, or ACL lookup.
- The native local-protocol test covers exact bounds and malformed name
  classes.
- The pinned Samba 4.19.5 `build-runtime` image builds successfully; this
  compiles `kaimo_authd`, runs the native protocol suite, and compiles/links
  the real `kaimo_bridge` module against ABI 49.
- `git diff --check` passes.

**Validation still required**

- Exercise exact-limit and over-limit login/share attempts through live SMB
  clients in the deployable Compose stack.
- Existing legacy database rows are intentionally not rewritten. P1-18
  synchronization remains unhealthy until an invalid legacy account/share is
  renamed or removed.

**Next planned finding:** P2-02 — route every VFS hook through one
boundary-aware share-relative canonicalization routine.

### 2026-07-28 — P2-02: Unified share-relative canonicalization

**Status:** Implemented, regression-tested, and verified in the pinned Samba
4.19.5 `build-runtime` image.

The bridge historically normalized only selected snapshot and delete paths.
P0-02 had already expanded the call coverage and added a component-boundary
check, but the behavior still lived inside the Samba-dependent VFS source
without focused regression tests. The root connectpath was also a missed edge
case: `/folder/file` was left absolute when the configured connectpath was `/`.

**Implemented**

1. Extracted the allocation-free canonicalization rules into `share_path.h`,
   with a thin VFS wrapper as the single entry point used by create,
   directory-listing, close, delete, rename, mkdir, snapshot enumeration,
   snapshot resolution, stat/lstat, openat, and reserved-namespace handling.
2. Preserved already-relative paths and stripped absolute connectpaths only on
   an exact component boundary. `/share-backup` therefore cannot be mistaken
   for a child of `/share`.
3. Canonicalized exact-share, `.`, and `./` root spellings to the empty path,
   removed leading `./` segments, trimmed trailing connectpath separators, and
   added explicit semantics for the `/` connectpath.
4. Added a standalone native regression suite covering relative and absolute
   inputs, exact roots, trailing and repeated separators, leading `./`,
   null/empty connectpaths, root shares, and prefix collisions.
5. Wired the regression into the pinned native `build-runtime` stage before
   compiling and linking the real VFS module.

**Validation completed**

- The focused `test-share-path` native suite passes in the generated
  `kaimo-samba-build-tests:p2-02` image.
- The pinned Samba 4.19.5 `build-runtime` image builds successfully, including
  the complete native helper suite and real `kaimo_bridge` ABI-49 module.
- `git diff --check` passes.

**Validation still required**

- Exercise live SMB operations whose Samba path arguments alternate between
  already-relative and connectpath-prefixed forms across create, listing,
  close, delete, rename, mkdir, and snapshots in the deployable Compose stack.
- P2-02 intentionally does not make arbitrary raw client paths trustworthy.
  Rejection of NUL, traversal, absolute/out-of-share, and internal paths plus
  containment-checked materialization remains P2-03.

**Next planned finding:** P2-03 — unify bridge path validation and route
snapshot materialization through containment-checked storage resolution.

### 2026-07-29 — P2-03: Unified bridge path validation and containment

**Status:** Implemented and regression-tested in the complete managed test
project. Live malformed-path probes through the deployable Samba stack remain
release verification.

The bridge previously applied different trust rules depending on the RPC.
`Normalize()` removed leading separators, so an absolute input could become an
apparently valid relative ACL path. Lifecycle events did not consistently
validate their paths before durable claiming, and snapshot projections used
local containment logic while persisted version paths were not checked at
every output boundary.

**Implemented**

1. Added `ShareRelativePath.TryNormalizeStrict()` as the bridge-facing source
   of truth. It validates the raw string before normalization and rejects null,
   rooted/drive-qualified, control-character, complete traversal-segment, and
   case-insensitive top-level `.kaimo-*` paths. It removes safe `.` components
   and duplicate separators only after validation.
2. Added `ShareRelativePath.ToContainedAbsolutePath()` as the shared lexical
   resolver. It fully qualifies the configured root, preserves filesystem-root
   semantics, applies platform-appropriate path comparison, and permits only
   the root or a descendant across a complete separator boundary.
3. Routed `FileSystemStorage.ToAbsolutePath()` through the shared resolver.
   Server-owned internal paths remain explicitly allowed there because
   `.kaimo-close-captures` is part of the durable close-event protocol.
   Client-facing RPCs reject all `.kaimo-*` namespaces before reaching storage.
4. Authz Open, Delete, and both Rename operands now use strict validation and
   containment resolution. Their filesystem existence/type cross-checks no
   longer use direct `Path.Combine()` calls on bridge input.
5. Close, Mkdir, Delete, and Rename lifecycle RPCs validate and canonicalize
   paths before resolving a share or claiming the event receipt. Handlers
   receive the validated canonical path rather than the original protobuf
   value.
6. Snapshot Enumerate/Resolve ingress uses the same strict rules. Every
   persisted `FileVersion.FilePath` is revalidated before constructing a
   cache-relative response, checking an existing projection, reconciling a
   folder, or writing content.
7. Snapshot user-scope resolution now delegates to the shared containment API
   instead of maintaining an independent `Path.Combine()`/prefix comparison.

**Validation completed**

- The focused managed project passes: 599 tests, 0 failures, 0 skipped.
- Strict helper regressions cover Unix-rooted, Windows-rooted,
  drive-qualified, traversal, NUL/control, internal namespace, sibling-prefix,
  safe-dot, duplicate-separator, root, and server-owned internal-path cases.
- Authz regressions prove invalid paths are denied before any ACL evaluation.
- Lifecycle regressions prove invalid paths are rejected before share
  resolution, event claiming, or file-service side effects.
- Snapshot regressions prove traversal ingress is rejected before version
  lookup and malformed persisted paths are never opened or materialized.
- `git diff --check` passes.

**Validation notes and remaining release work**

- A complete `dotnet test Kaimo_File_Server.slnx --no-restore` invocation built
  the Core, Infrastructure, Search, SmbBridge, Web, and test assemblies and
  reported all tests successful, but MSBuild returned failure for the
  `docker-compose.dcproj` because the sandbox account cannot read
  `C:\Users\Marco\AppData\Local\Microsoft SDKs`.
- The authoritative targeted command,
  `dotnet test tests/Kaimo_File_Server.Tests/Kaimo_File_Server.Tests.csproj --no-restore`,
  completed successfully and avoids that unrelated Docker tooling probe.
- Exercise malformed absolute, traversal, control-character, `.kaimo-*`, root,
  and valid dot-segment inputs through live SMB Open/Delete/Rename/Snapshot and
  lifecycle operations in the deployable Compose stack.
- Containment is lexical and intentionally matches the existing storage
  contract. Native Samba remains responsible for descriptor-relative
  mutation/TOCTOU checks and symlink policy.

**Next planned finding:** P2-04 — replace unbounded snapshot-count parsing with
strict overflow-aware parsing and a documented maximum.

### 2026-07-29 — P2-04: Bounded snapshot enumeration parsing

**Status:** Implemented, regression-tested, and verified in the pinned Samba
4.19.5 `build-runtime` image.

The audit finding referred to the original newline-delimited sidecar response,
where the VFS passed an unbounded decimal count through `atoi()` before
allocation. P1-03 removed that immediate integer-overflow primitive by replacing
the text protocol with a framed binary response and a big-endian `uint32`
count. The binary parser still lacked a documented semantic maximum and one
central proof that the declared count matched the complete received record set.

**Implemented**

1. Defined `KAIMO_LOCAL_MAX_SNAPSHOT_LABELS` as 2,048 and the wire token length
   as 24 bytes in `local_protocol.h`. A maximum response occupies 57,348 bytes:
   four bytes for the count plus 2,048 records of a four-byte length and a
   24-byte token. This leaves 8,188 bytes of headroom below the 65,536-byte
   framed-response limit.
2. Added the C/C++-compatible `snapshot_enumeration.h` helper. It validates the
   fixed `@GMT-yyyy.MM.dd-HH.mm.ss` wire shape and the complete enumeration
   payload without allocation.
3. The validator reads the unsigned binary count, rejects values above 2,048,
   prechecks that the remaining payload can contain the advertised minimum
   record bytes, validates every length-prefixed token, and requires the reader
   to finish exactly at the payload boundary.
4. `kaimo_get_shadow_copy_data()` now completes that validation before calling
   `talloc_zero_array()`. Only a proven count can influence Samba label
   allocation. A malformed, truncated, oversized, inconsistent, or trailing
   response is ignored as an empty snapshot list; no partial label set is
   published.
5. `kaimo_authd` enforces the same maximum and token shape on the gRPC result
   before reserving or serializing the local token vector. The response builder
   retains its existing all-or-error behavior if the framed payload cannot be
   encoded.
6. Added `test-snapshot-enumeration.cpp` to the native `build-runtime` gate and
   copied the shared header into both the sidecar and real Samba module build
   contexts.

**Validation completed**

- The standalone native regression passes at zero, one, and exactly 2,048
  labels.
- Negative cases pass for 2,049 advertised labels, fewer records than declared,
  additional records, truncated content, trailing bytes, malformed separators,
  invalid token text, absent payload, and a null output pointer.
- The Docker image `kaimo-samba-build-tests:p2-04` built successfully.
- That image compiled `kaimo_authd`, ran the complete native helper suite
  including the new enumeration regression, and compiled/linked the real
  `kaimo_bridge` module against pinned Samba 4.19.5 / ABI 49.
- The cumulative managed test project remains green at 599 tests.
- `git diff --check` passes.

**Design boundary and remaining release work**

- P2-04 establishes a safe count and exact parser semantics. P2-05 remains the
  explicit review of response-buffer behavior, producer-side framing guarantees,
  and the operational policy when more snapshots exist than can be represented.
- The current producer fails the enumeration response rather than silently
  truncating when more than 2,048 timestamps are returned. P2-05 must decide
  whether that behavior remains fail-closed or becomes an explicitly ordered
  newest-N/paginated contract.
- Run a live SMB enumeration at 0, 1, 2,048, and over-limit version timestamps
  to verify Windows/smbclient behavior at the operational boundary.

**Next planned finding:** P2-05 — make snapshot response-buffer capacity and
over-limit behavior explicit, consistent, and observable.

### 2026-07-29 — P2-05: Explicit snapshot response-capacity contract

**Status:** Implemented, regression-tested, and verified in the pinned Samba
4.19.5 `build-runtime` image.

P2-04 made the consumer parser bounded and exact, but an over-limit managed
history still reached `kaimo_authd`, where it became a generic enumeration
error. The 64-KiB response capacity and the desired behavior for histories
larger than the wire contract were not yet enforced at the source or expressed
as a compile-time invariant.

**Architecture decision**

Samba's `get_shadow_copy_data` callback returns one complete label array and
does not expose continuation state. Kaimo therefore does not add pagination
that the SMB-facing callback cannot consume. The supported contract is the
newest 2,048 distinct, second-resolution `@GMT` labels. Older history remains
stored and available through non-SMB application paths, but is omitted from
SMB Previous Versions enumeration once the bound is exceeded.

**Implemented**

1. Added `SnapshotGrpcService.MaxEnumerationLabels = 2_048`, explicitly tied
   by source comments and tests to `KAIMO_LOCAL_MAX_SNAPSHOT_LABELS`.
2. Enumeration now sorts timestamps descending before formatting, deduplicates
   the formatted 24-byte labels, and only then applies the limit. Sub-second
   database timestamps that map to the same Windows label do not consume
   multiple wire slots.
3. When the distinct label count exceeds the contract, the bridge returns the
   newest labels and logs the total available count, maximum returned count,
   and number omitted. This turns a previously generic sidecar failure into an
   observable, deterministic policy.
4. Added `KAIMO_LOCAL_MAX_SNAPSHOT_ENUMERATION_PAYLOAD`, derived from the count,
   per-record length prefix, and fixed token length. A preprocessor guard fails
   the C/C++ build if the calculated 57,348-byte maximum ever exceeds
   `KAIMO_LOCAL_MAX_RESPONSE_PAYLOAD` (65,536 bytes).
5. Retained layered defensive checks: the sidecar rejects an out-of-contract
   gRPC result, the builder publishes either a complete response or error, the
   frame reader rejects an oversized declared payload before reading it, and
   the VFS parser rejects incomplete or trailing records before allocation.

**Validation completed**

- Added a managed over-limit regression with 2,050 unique seconds plus one
  sub-second duplicate. It proves exactly 2,048 labels are returned, the newest
  label is first, the oldest retained boundary is correct, the oldest labels
  are absent, and the formatted duplicate does not consume a slot.
- The cumulative managed project passes: 600 tests, 0 failures, 0 skipped.
- The native snapshot regression asserts the derived maximum is exactly 57,348
  bytes and does not exceed the response-frame limit.
- The Docker image `kaimo-samba-build-tests:p2-05` built successfully,
  including the complete native helper suite and sidecar compilation.
- The real `kaimo_bridge` module compiled and linked successfully against
  pinned Samba 4.19.5 / ABI 49.
- `git diff --check` passes.

**Validation still required**

- Exercise a live SMB client against 2,048 and more than 2,048 distinct version
  timestamps, confirming Windows and `smbclient` receive the newest ordered
  subset without an enumeration error.
- Monitor the over-limit warning in production. Repeated occurrences may
  justify a product-level SMB retention setting, but do not require a larger
  local frame or an unsafe partial response.

**Next planned finding:** P2-06 — verify cache hits by immutable content
identity rather than file size alone.

### 2026-07-29 — P2-06: Content-verified snapshot cache reuse

**Status:** Implemented and regression-tested. The implementation was
introduced as part of the earlier P1-07 atomic-materialization remediation;
P2-06 is now independently verified and formally closed in the P2 roadmap.

The original cache-hit predicate accepted any existing file with the expected
length. Equal-length corruption, tampering, or a crash artifact could therefore
be exposed as historical content without reading the immutable version source.

**Implemented contract**

1. `FileVersion.ContentHash` is the authoritative immutable content identity.
   Version metadata must contain a non-negative length and a 64-character
   hexadecimal SHA-256 digest before it can influence cache reuse or
   publication.
2. A concrete-file cache hit requires both exact length and a streamed SHA-256
   match. A valid hit avoids decompression and blob access; size equality alone
   is never sufficient.
3. Complete folder projections apply the same per-file predicate and also
   require the actual file set to equal the ACL-filtered expected set. Missing,
   additional, malformed, or content-mismatched entries force reconciliation
   and rematerialization.
4. Replacement content is written to a unique same-directory temporary file.
   Streaming is capped by the declared length, the final length and SHA-256
   digest are verified, the file is flushed to durable storage, marked
   read-only, timestamped, and atomically moved into place.
5. Invalid metadata, short or oversized source streams, digest mismatches, and
   cancellation fail closed. The final path is not published from unverified
   data, and abandoned temporary files are removed.

**Validation completed**

- The focused `SnapshotGrpcServiceAclTests` suite passes: 21 tests, 0
  failures, 0 skipped.
- The cumulative managed test project passes: 602 tests, 0 failures, 0
  skipped.
- Added a positive cache-hit regression proving matching length and SHA-256
  reuse the existing projection without calling `ReadVersionAsync`.
- Retained the concrete-file regression proving a same-sized corrupt entry is
  not reused and is replaced with verified immutable content.
- Added the equivalent folder-projection regression, proving the directory
  fast path cannot accept a same-sized corrupt child and that exactly one
  verified rematerialization occurs.
- Existing negative tests continue to cover source digest mismatch, short
  source content, oversized source content, absence of a partial final file,
  and temporary-file cleanup.

**Validation still required**

- Run the focused suite inside the Linux bridge container to cover the deployed
  filesystem's async hashing, `fsync`, read-only mode, and atomic replacement
  behavior.
- Corrupt a same-sized cached projection in the deployable stack and verify
  Windows Previous Versions plus `smbclient` receive repaired historical
  content rather than the tampered entry.

**Next planned finding:** P2-07 — make cleanup directory enumeration
exception-safe by ensuring failures occur inside the protected boundary.

### 2026-07-29 — P2-07: Exception-safe cleanup enumeration

**Status:** Implemented and regression-tested.

The snapshot cleanup helper returned `Directory.EnumerateDirectories()` from
inside a `try`. That API is lazy: opening the filesystem enumerator and reading
entries can occur only when a caller starts its `foreach`. The apparent local
exception boundary therefore did not contain the failure it was intended to
handle. A transient disappearance, I/O error, or access failure could abort the
current share sweep and defer recovery until the background service's next
interval.

**Implemented**

1. `SafeEnumerateDirectories()` now returns `IReadOnlyList<string>` and calls
   `ToArray()` before leaving its exception boundary. Callers receive a stable
   point-in-time snapshot with no deferred filesystem work.
2. `DirectoryNotFoundException` is handled as a normal cleanup race. A root may
   disappear between `Directory.Exists()` and enumeration because another
   evictor or materializer changed the cache concurrently.
3. `IOException` and `UnauthorizedAccessException` are contained and logged
   with the affected directory before an empty snapshot is returned. The sweep
   can continue safely without acting on an incomplete directory set.
4. The former unqualified `catch` was removed. Unexpected exceptions such as
   resource exhaustion or programming errors remain visible to the hosted
   service's outer failure handler instead of being silently misclassified as
   an empty cache.
5. Both token enumeration under active shares and orphan-share cleanup consume
   the same materialized helper result. Other cleanup scans were reviewed:
   legacy-cache detection and recursive size calculation already perform their
   lazy iteration within their own protected blocks.

**Validation completed**

- Added a regression that captures two child directories, creates a third
  afterward, and proves the returned result remains the original materialized
  snapshot.
- Added a regression that passes a regular file where a directory is expected.
  The deferred enumeration I/O failure is contained by the helper and the
  caller receives an empty result.
- The focused `SnapshotCacheLeaseManagerTests` suite passes: 9 tests, 0
  failures, 0 skipped.
- The cumulative managed test project passes: 604 tests, 0 failures, 0
  skipped.

**Operational note**

An affected root is skipped for the current sweep after an I/O or access
failure. No deletion is attempted from a partial view. The background service
retries on its next configured sweep, while the warning identifies the root
that requires investigation if the condition persists.

**Next planned finding:** P2-08 — validate TTL, sweep interval, and per-share
cache-cap configuration at startup with explicit safe bounds.

### 2026-07-29 — P2-08: Startup-validated snapshot cache policy

**Status:** Implemented and regression-tested.

The cleanup service previously converted `TtlHours`, `SweepMinutes`, and
`MaxBytesPerShare` directly from `IConfiguration` during operation. The
settings had no upper bounds, and floating-point values were not required to be
finite. Zero or negative TTL could evict current projections immediately, zero
or negative sweep intervals could create a tight retry loop or terminate the
hosted service, and extreme retention/capacity values defeated the intended
operational bound.

**Architecture and bounds**

The cache policy now has explicit inclusive bounds:

| Setting | Minimum | Default | Maximum |
|---|---:|---:|---:|
| `TtlHours` | 1 minute (`1/60` hour) | 24 hours | 365 days (`8,760` hours) |
| `SweepMinutes` | 1 minute | 30 minutes | 24 hours (`1,440` minutes) |
| `MaxBytesPerShare` | 1 MiB (`1,048,576`) | 5 GiB (`5,368,709,120`) | 100 TiB (`109,951,162,777,600`) |

The minimum TTL and sweep interval prevent immediate deletion and tight loops.
The maxima prevent accidental effectively-unbounded retention or scheduling
while remaining well above expected production requirements. The capacity
maximum constrains arithmetic and configuration mistakes without imposing a
practical small-installation ceiling.

**Implemented**

1. Added `SnapshotCacheOptions` as the single typed contract for
   `Snapshots:Cache`, including defaults, range constants, and derived
   `TimeSpan` values.
2. Added `SnapshotCacheOptionsValidator`. It requires a non-empty, valid,
   absolute cache root; finite in-range TTL and sweep values; and an in-range
   signed 64-bit capacity.
3. Registered the validator with `ValidateOnStart()`. Invalid configuration
   now prevents bridge startup before gRPC requests or cleanup work are served.
   Diagnostics retain the exact configuration key for operational correction.
4. `SnapshotCacheCleanupService` now consumes a validated `IOptions` snapshot.
   It does not rebind or reinterpret configuration between sweeps.
5. Added Compose environment mappings for TTL, sweep interval, and per-share
   capacity. `.env.example`, bridge `appsettings.json`, and the Samba VFS
   operations guide document identical defaults and accepted ranges.

**Validation completed**

- Defaults and every exact minimum/maximum boundary validate successfully.
- Negative and zero TTL/sweep values are rejected.
- `NaN`, positive infinity, and above-maximum floating-point values are
  rejected before `TimeSpan` conversion or `Task.Delay`.
- Empty and relative cache roots are rejected.
- Capacity values below 1 MiB and above 100 TiB are rejected.
- A DI-bound configuration regression proves multiple invalid settings produce
  an `OptionsValidationException` containing all affected keys.
- The focused cache options plus cleanup/lease suites pass: 26 tests, 0
  failures, 0 skipped.
- The cumulative managed test project passes: 621 tests, 0 failures, 0
  skipped.

**Operational note**

Deployments that override cache cleanup settings must use values inside the
documented inclusive ranges. An invalid override is intentionally a hard
startup failure; silently substituting a default would conceal an operator
error and could violate the expected retention or disk-usage policy.

**Next planned finding:** P2-09 — complete cancellation-token propagation
through remaining RPC repository, ACL, and file-work paths.

### 2026-07-29 — P2-09: Complete bridge RPC cancellation boundaries

**Status:** Implemented and regression-tested. Live native deadline/cancellation
probes remain part of the release gate.

P1-09 already propagated cancellation through expensive snapshot folder
queries, concurrency reservations, decompression, hashing, copying, flushing,
and materialization. Other bridge RPCs still awaited authentication,
configuration, share, user, and ACL tasks without observing the gRPC request
token. Authz loops could continue evaluating permissions after the caller and
native deadline had gone away, and snapshot enumeration formatted a complete
history without an interruption point.

**Implemented**

1. `GetNtHash` and `ListUsers` bind authentication and user-repository waits to
   the request token. Bulk export checks cancellation between users and before
   each hash lookup. P2-10 still owns response bounding, batching, corrupt-row
   isolation, and removal of the sequential N+1 pattern.
2. `ListShares` binds its repository query to the request and checks
   cancellation while translating definitions into protobuf records.
3. `GetProtocolSettings` binds both the fresh protocol-settings read and the
   service-enabled read to the same request token. Cancellation after the first
   read prevents the second query and reply construction.
4. Connect, Open, Delete, and Rename authorization now use one captured request
   token for user, share, service-state, access, and ACL decisions.
   Traversal, specific-right, maximum-access, delete-parent, and rename-source/
   destination loops check or await that token between permission rules.
5. Snapshot enumeration binds user resolution, share resolution, version
   lookup, ACL evaluation, and folder timestamp lookup to the request. Ordered
   label formatting now uses an explicit loop with a cancellation check per
   timestamp while retaining the P2-05 newest/distinct/2,048 contract.
6. Snapshot resolution retains the deeper P1-09 contract: request or budget
   cancellation reaches folder/version queries, lease waits, materialization
   reservation, content reads, hashing, writes, and cleanup.
7. Lifecycle user/share resolution and durable receipt claiming observe the
   request token. After a claimed non-cooperative handler has committed its
   filesystem/version/index side effects, receipt completion intentionally uses
   a non-cancelled token. Releasing or abandoning that receipt merely because
   the client disconnected could allow the sidecar retry to duplicate a
   mutation.

**Cancellation contract**

- A cancelled RPC stops awaiting a blocked legacy dependency through
  `Task.WaitAsync(requestToken)` and does not continue its bridge-side loop or
  reply construction.
- A legacy single-read interface may have already dispatched a provider query
  that finishes inside its own scope after the RPC wait is released. These
  reads are bounded and do not publish side effects.
- Expensive snapshot queries and all content streaming/copy work use native
  cancellation-aware overloads, so cancellation reaches the actual operation.
- A lifecycle mutation already past its durable claim is a correctness
  boundary: it completes or fails and records that result; client cancellation
  cannot turn an in-flight mutation into an uncoordinated retry.

**Validation completed**

- Added a reusable cancellation-aware `ServerCallContext` test double.
- Blocking authentication lookup cancellation passes for `GetNtHash`.
- Blocking share repository cancellation passes for `ListShares`.
- Blocking configuration lookup cancellation passes and proves the second
  configuration read is not started.
- Blocking user-context resolution cancellation passes for Authz Open.
- Blocking snapshot user-context resolution cancellation passes for snapshot
  enumeration.
- Cancellation after a claimed lifecycle mutation proves receipt completion
  uses a non-cancelled token and is not released for a duplicate retry.
- The focused cancellation/Authz/lifecycle/snapshot suite passes: 87 tests, 0
  failures, 0 skipped.
- The cumulative managed test project passes: 627 tests, 0 failures, 0
  skipped.
- The SmbBridge project builds successfully.

**Validation still required**

- Cancel and deadline-expire every native local operation while its
  corresponding bridge dependency is delayed, then confirm `smbd` workers and
  bridge requests return within the configured deadline.
- Interrupt snapshot enumeration, folder materialization, and version copying
  in the deployable Linux stack and confirm no temporary projection or lease is
  stranded.
- Interrupt a lifecycle delivery before claim and after handler completion,
  proving the first is retried and the second remains exactly-once complete.

**Next planned finding:** P2-10 — replace unbounded sequential NT-hash export
with a bounded, batched contract and corrupt-row isolation.

### 2026-07-29 — P2-10: Bounded, batched NT-hash export

**Status:** Implemented, regression-tested, and verified in the pinned Samba
4.19.5 `build-runtime` image. Live multi-page reconciliation remains a release
gate.

**Problem**

The original `ListUsers` method materialized every user entity, including
fields irrelevant to Samba, then issued one additional username lookup and
decryption per user. This produced three coupled risks:

1. database work and the unary gRPC response grew without a per-message bound;
2. the N+1 lookup pattern made synchronization latency proportional to both
   user count and database round trips; and
3. one malformed encrypted/legacy NT hash threw out of `ListUsers`, preventing
   every otherwise valid account from converging.

The native client did impose 100,000-record and 16-MiB post-response checks, but
those checks ran only after the bridge had already materialized and serialized
the unbounded protobuf response. They therefore did not protect bridge memory,
gRPC message size, or database query shape.

**Contract and policy decisions**

1. `ListUsersRequest` now carries `offset` and `page_size`;
   `ListUsersReply` carries `next_offset`, `has_more`, and the count of rejected
   source rows. Page size is required and limited to 1-1,000; offsets at or
   above 100,000 are rejected. Rejecting the protobuf zero default ensures an
   older, non-paginating client fails closed during a mixed-version rollout
   instead of importing only the first page and revoking the remainder.
2. The bridge fetches one extra source row as look-ahead. This establishes
   `has_more` without a second count query and keeps each data query bounded to
   at most 1,001 minimal rows.
3. The existing fixed-window rate limit is charged on offset zero, which is one
   permit per logical export. Every continuation requires a short-lived
   HMAC-authenticated token bound to the authenticated client identity and the
   exact next offset. Continuation pages remain bounded, allow-listed,
   cancellable, and audited without allowing an arbitrary offset to bypass the
   rate limiter.
4. More than 100,000 active source rows is a hard synchronization failure. The
   last allowable page returns `ResourceExhausted` if look-ahead proves another
   page exists, so the native importer cannot mistake a truncated account set
   for authoritative desired state and revoke omitted accounts.
5. Offset pagination is an eventual-convergence contract rather than a
   cross-request database snapshot. Concurrent account changes may shift a
   later page; the periodic reconciler repairs the view on its next run. The
   tighter disable/revocation guarantee remains explicitly owned by P2-13.

**Implemented**

1. Added `SambaCredentialSource`, a repository projection containing only
   username and protected NT hash. `UserRepository` filters disabled users,
   orders deterministically by username and identity, applies `Skip`/`Take`,
   uses `AsNoTracking`, and passes the RPC cancellation token directly into
   `ToListAsync`.
2. Added the bounded `SambaCredentialBatch` authentication contract.
   `AuthenticationLookup` decrypts only the current page, filters the
   well-known empty-password hash, validates the common Samba username
   contract, decodes strict hexadecimal, and accepts only 16 decoded bytes.
   Invalid names, malformed legacy hex, malformed/tampered ciphertext, and
   wrong-length values are rejected independently while valid neighbors remain
   exportable.
3. Tightened the single-user `GetNtHash` boundary to the same exact 16-byte
   requirement.
4. `AuthGrpcService` enforces page and total limits before serialization,
   validates internal batch metadata, copies only exact 16-byte records to
   protobuf, and logs page/rejection counts without logging hashes or
   usernames.
5. Reworked `kaimo_authsync` into a bounded continuation loop. It requests
   1,000-row pages, requires monotonic offsets, validates per-page and
   cumulative limits, and accumulates only validated username/hash pairs.
   Standard output remains empty until the final page succeeds. It also
   requires a continuation token exactly when `has_more` is true. Any failed
   RPC, malformed/expired continuation, invalid record, over-limit source set,
   or over-limit JSON estimate terminates without producing a partial version-1
   document for `sync-users.sh`.
6. Kept the reconciler's independent JSON schema, uniqueness, record-count, and
   byte-size validation unchanged. Pagination is therefore additive
   defense-in-depth and does not weaken the pre-mutation validation boundary.

**Regression coverage**

- `AuthenticationLookupNtHashTests` now proves exact-length rejection, one
  page projection rather than per-user lookup, look-ahead semantics,
  empty-password filtering, corrupt-row isolation, invalid username rejection,
  and independent wrong-length rejection.
- `SambaCredentialRepositoryTests` uses the production EF model on relational
  in-memory SQLite to prove deterministic paging, query bounds, protected-hash
  projection, and disabled-user exclusion.
- `AuthGrpcServiceUserExportTests` covers required/max page behavior,
  continuation metadata, rejection accounting, missing/oversized/`uint32`
  boundary rejection before lookup, and both crossing forms at the
  100,000-source-row boundary.
- The updated cancellation regression verifies the simplified authentication
  service dependency boundary still interrupts single-hash lookup.

**Validation completed**

- The focused managed P2-10/security selection passes: 20/20 tests.
- The complete managed test project passes: 641/641 tests, zero skipped.
- `dotnet build tests/Kaimo_File_Server.Tests/Kaimo_File_Server.Tests.csproj
  --no-restore` succeeds. Existing repository warnings remain unchanged.
- The pinned Samba 4.19.5 `build-runtime` image
  `kaimo-samba-build-tests:p2-10` builds successfully. This regenerates the C++
  protobuf/gRPC stubs, compiles and links the paginated `kaimo_authsync`,
  executes the native helper tests, and compiles the real ABI-49 VFS module.
- `git diff --check` passes.

**Validation still required**

- Run a live bridge/Samba reconciliation with 0, 1, 1,000, 1,001, and a
  representative large user set; confirm `tdbsam`, POSIX group membership, and
  private managed-user state converge exactly.
- Inject one malformed protected hash between valid rows in a deployment-shaped
  database and confirm the count-only warning, valid-neighbor import, and
  absence of credential material or usernames from logs.
- Change enable/delete state while a multi-page export is in flight and measure
  convergence on the next periodic run. P2-13 must define whether the resulting
  polling window is acceptable for credential revocation.
- Verify an over-100,000 source set leaves the prior passdb desired state
  untouched and marks synchronization unhealthy.

P2-10 bounds where credentials are fetched and transported, but it does not
claim zero-copy or secure clearing. Managed strings/arrays, protobuf storage,
native strings, captured JSON, and the private import file remain the explicit
scope of P2-11.

**Next planned finding:** P2-11 — minimize credential copies and lifetime, clear
mutable buffers where practical, and remove avoidable text/file exposure from
the import path.
