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

### 2026-07-29 — P2-11: Minimized and cleared decrypted credentials

**Status:** Implemented, regression-tested, and verified in the pinned Samba
4.19.5 `build-runtime` image. Live memory inspection and deployment tmpfs
verification remain release gates.

**Problem**

P2-10 bounded each credential export, but every accepted NT hash still crossed
several avoidable representations:

1. AES-GCM decryption produced a managed plaintext hexadecimal string and then
   decoded that string into a second raw byte array.
2. The lookup-owned raw array remained reachable after gRPC had copied it into
   protobuf. Bulk arrays were likewise retained by the batch object until
   garbage collection, including error and cancellation paths.
3. `kaimo_authsync` retained every raw hash in `std::string`, converted each one
   into a second hexadecimal `std::string`, and relied on ordinary allocator
   release rather than clearing its owned memory.
4. `sync-users.sh` captured the complete JSON document in the shell variable
   `OUT`, then held JSON, TSV, and `smbpasswd` file representations until the
   final process-level cleanup even after a phase no longer needed them.
5. Although staging files were random, private, and mode 0600, the default
   container path was not explicitly a tmpfs and could therefore enter the
   container's writable image layer.

An NT hash is password-equivalent for NTLM. Garbage collection, allocator
release, or eventual process exit is not a sufficient lifetime boundary when
the application directly owns a mutable credential buffer.

**Architecture and limits**

The implementation uses explicit clearing only where it is technically
meaningful and under Kaimo's ownership:

- Managed `byte[]` plaintext and raw-hash arrays are cleared with
  `CryptographicOperations.ZeroMemory`.
- C++ raw hashes use fixed mutable storage and a volatile write loop before
  storage release.
- Transient files are unlinked at the end of the phase that consumes them and
  live on a private tmpfs in the deployable Compose stack.
- Credentials are not placed in process arguments, environment variables,
  diagnostics, audit logs, or durable synchronization state.

The implementation does not make an unverifiable zero-copy claim. Immutable
.NET strings already loaded from the database, protobuf/serializer internals,
kernel pipe buffers, `jq`, `pdbedit`, and Samba's own passdb importer may retain
their operational copy until that bounded operation or process releases it.
Those buffers are not exposed through a Kaimo persistence or logging boundary.

**Implemented**

1. Extended `INtHashProtector` with `UnprotectToBytes()`. AES-GCM ciphertext is
   decrypted into a mutable ASCII buffer and decoded directly into the exact
   16-byte result without creating another plaintext hash string. Legacy
   plaintext rows are decoded directly from their existing stored string.
2. The protector clears decrypted ASCII, decoded encryption blobs,
   key-derivation input bytes, encryption plaintext, and assembled encryption
   blobs in `finally` blocks. Tamper, format, and wrong-length failures retain
   the established fail-closed behavior.
3. `AuthenticationLookup` keeps the well-known empty-password hash as raw
   bytes, uses a fixed-time comparison, and clears every empty/wrong-length
   rejected buffer. Accepted arrays transfer to the bridge as the explicit
   next owner.
4. `AuthGrpcService.GetNtHash` copies the exact value into `ByteString` and
   clears the lookup array in `finally`. `ListUsers` wraps metadata validation,
   response construction, cancellation, continuation generation, and return in
   one `try/finally`, clearing every batch-owned hash on every exit path.
5. `kaimo_authsync` replaces the `pair<string,string>` credential collection
   with a move-only credential type containing a fixed 16-byte array. Copying
   is disabled; moves clear their source; destruction wipes the retained bytes.
   The protobuf field is cleared immediately after the fixed buffer is filled.
6. Native hexadecimal emission writes digits directly to the output stream.
   It no longer allocates a second hash-bearing `std::string`.
7. `sync-users.sh` redirects exporter output into a random private file rather
   than command-substituting it into shell memory. The independent 16-MiB
   boundary and complete JSON/schema/uniqueness validation remain unchanged.
8. The JSON staging file is removed immediately after successful validation.
   The TSV record file is removed immediately after the private `smbpasswd`
   import is assembled. The `smbpasswd` file is removed immediately after a
   successful `pdbedit` import, before revocation/read-back/state publication.
   The existing `EXIT` trap remains the fail-safe for every error or signal.
9. `validated_hash`, `nthash`, and temporary size variables are explicitly
   unset after their final use. No hash is interpolated into a command argument
   or exported environment variable.
10. Compose mounts `/run/kaimo-user-sync` as root-owned mode 0700 tmpfs with
    `noexec`, `nosuid`, and `nodev`. The durable
    `/var/lib/kaimo-user-sync/managed-users` file continues to contain only
    usernames and never credential material.

**Regression coverage**

- `AesGcmNtHashProtectorTests` verifies raw-byte decode for both encrypted and
  legacy rows plus malformed legacy input rejection.
- `AuthGrpcServiceUserExportTests` proves that single-user and paged export
  replies retain the correct protobuf value after their source arrays have
  been zeroed.
- The synchronization regression records the runtime directory at the exact
  `pdbedit` import boundary and proves JSON and TSV credential staging files
  have already been removed. Existing success and failure checks continue to
  prove random mode-0600 import, end-of-run cleanup, pre-mutation schema
  rejection, and state preservation after failed import.

**Validation completed**

- The focused managed credential selection passes: 31/31 tests.
- The complete managed test project passes: 647/647 tests, zero skipped.
- The pinned Samba 4.19.5 `build-runtime` image
  `kaimo-samba-build-tests:p2-11` builds successfully. It regenerates and
  compiles the C++ protobuf/gRPC client, runs the hardened synchronization
  shell regression and all native helper tests, and compiles the real ABI-49
  VFS module.
- `docker compose config --quiet` succeeds. The fully resolved configuration
  retains `/run/kaimo-user-sync:rw,noexec,nosuid,nodev,mode=0700,uid=0,gid=0`.
- `git diff --check` passes.

**Validation still required**

- Inspect a live Samba container during a deliberately paused large import:
  verify `/run/kaimo-user-sync` is a tmpfs, owner/mode are 0/0700, all transient
  files are 0600, no hash appears in `/proc/*/cmdline` or `/proc/*/environ`, and
  JSON/TSV/import files disappear at their documented phase boundaries.
- Capture bridge and Samba logs during successful, corrupt-row, failed-import,
  cancellation, and bridge-unavailable runs; scan for known test hashes and
  confirm only count-level diagnostics are present.
- Perform controlled process-memory inspection around bridge serialization,
  native JSON output, `jq`, and `pdbedit` to document the unavoidable bounded
  copies and confirm Kaimo-owned buffers do not outlive their handoff boundary.
- Run the deployment-shaped multi-page reconciliation cases retained from
  P2-10 and confirm the lifetime changes do not alter exact `tdbsam`, group, or
  managed-user convergence.

**Next planned finding:** P2-12 — supervise `kaimo_authd` independently and make
loss of the authorization sidecar transition the container to an unhealthy or
restarted state within a bounded interval.

### 2026-07-29 — P2-12: Fail-fast `authd`/`smbd` supervision

**Status:** Implemented, regression-tested, and verified in the pinned Samba
4.19.5 `build-runtime` image. Live container crash/restart timing remains a
release gate.

**Problem**

The former entrypoint launched `kaimo_authd` with a background `&`, waited
briefly for its socket, and then replaced PID 1 with foreground `smbd`. This
created an asymmetric lifetime:

1. `smbd` determined container lifetime, while `authd` had no owner observing
   its exit status.
2. An `authd` startup failure or later crash could leave the container running
   indefinitely. Authorization would fail closed, but every new connect/open,
   lifecycle delivery, and snapshot operation would remain unavailable until
   an operator noticed and restarted the container.
3. The existing healthcheck covered synchronization freshness and a local SMB
   login/list probe, but it did not prove that the expected `authd` process
   owned a live authorization socket.
4. Container shutdown signals reached `smbd` as PID 1 but did not provide an
   explicit bounded shutdown/reaping contract for the sidecar.

Fail-closed VFS behavior prevents an authorization bypass, but permanent silent
degradation is still an availability and operations defect. `authd` also owns
event-spool delivery and snapshot bridge operations, so supervision must treat
it as part of the Samba runtime rather than an optional helper.

**Architecture decision**

`kaimo_authd` and `smbd` are now one coupled container unit. The supervisor does
not attempt an in-place sidecar restart while preserving `smbd`:

- Existing SMB worker processes and handles may have observed the missing
  sidecar. Restarting only `authd` would preserve an ambiguous partially
  degraded session state.
- Restarting the complete container closes sessions, recreates the local socket
  trust boundary, recovers the durable lifecycle spool, and reruns user/share/
  protocol reconciliation.
- Compose's `restart: unless-stopped` supplies the restart policy. The
  supervisor's responsibility is deterministic failure, signal forwarding,
  partner termination, and child reaping.

This intentionally favors a short explicit outage and clean security-unit
restart over indefinite fail-closed service degradation.

**Implemented**

1. Added `supervise-samba.sh` as the final PID-1 process. The entrypoint
   completes storage preparation and initial synchronization, creates the
   protected authd runtime directory, then `exec`s the supervisor.
2. Before launch, the supervisor removes only a genuine stale Unix socket and
   refuses a symlink or non-socket object at the configured path.
3. `kaimo_authd` starts first. The supervisor checks process liveness while
   waiting for the Unix socket and refuses to start `smbd` unless readiness is
   reached within 50 100-ms attempts (five seconds by default).
4. Once ready, the supervisor atomically publishes the authd PID through a
   same-directory temporary file and rename. The readiness file is mode 0600
   under the root-owned, non-group-writable runtime directory.
5. `smbd` starts only after socket readiness and PID publication. Both
   long-running PIDs are passed explicitly to `wait -n`; unrelated periodic
   reconciliation children cannot be mistaken for a supervised-process exit.
6. Any `authd` or `smbd` exit terminates the peer. A non-zero child status is
   propagated; a zero status is converted to failure because a clean exit is
   still unexpected for this long-running container unit.
7. HUP, INT, QUIT, and TERM traps forward termination to both children. A
   watchdog enforces the bounded shutdown grace and sends SIGKILL after five
   seconds by default. Both child statuses are reaped before PID 1 exits.
8. Socket and PID readiness state are removed after shutdown or peer failure,
   preventing a stale health success during restart.
9. Readiness attempts are validated in the inclusive range 1-600. Shutdown
   grace is validated in the inclusive range 1-30 seconds. Invalid values fail
   before either runtime process starts.
10. Added `authd-health.sh`. It requires:
    - a live Unix socket;
    - a regular, non-symlink, root-owned mode-0600 supervisor PID file;
    - a positive numeric live PID; and
    - an exact canonical `/proc/<pid>/exe` match to the installed
      `kaimo_authd` executable.
11. The Compose healthcheck evaluates authd identity before synchronization
    freshness and the existing SMB login/list probe.
12. The Samba service now declares `restart: unless-stopped`. An unexpected
    supervised-process exit therefore restarts the entire Samba security unit;
    an explicit operator stop remains stopped.

**Regression coverage**

`test-authd-supervisor.sh` runs the production supervisor and health script
against stateful authd/smbd test processes and proves:

- ready socket + protected PID state + matching executable pass health;
- terminating `authd` fails PID 1, terminates `smbd`, removes readiness, and
  makes health fail;
- an `authd` status 23 before readiness is propagated and `smbd` never starts;
- a clean `smbd` exit is converted to container failure, terminates `authd`,
  and removes its PID state; and
- SIGTERM sent to PID 1 reaches both children, both are reaped, and the
  supervisor returns the conventional status 143.

The test uses a one-second grace to keep failure coverage fast; production
retains the validated five-second default.

**Validation completed**

- The supervisor/health shell regression passes all four lifecycle scenarios.
- The existing user/share/config synchronization regressions continue to pass
  in the same image build.
- The pinned Samba 4.19.5 `build-runtime` image
  `kaimo-samba-build-tests:p2-12` builds successfully. The final runtime package
  contains the supervisor and health scripts alongside the real authd, smbd,
  helpers, and ABI-49 VFS module.
- The slim production stage builds as
  `kaimo-samba-runtime-tests:p2-12`. A direct container assertion confirms
  `supervise-samba.sh`, `authd-health.sh`, `kaimo_authd`, and the self-built
  `smbd` are all present and executable in that final image.
- `docker compose config` succeeds. The resolved Samba service retains both
  `restart: unless-stopped` and the `authd-health.sh`-first health command.
- The complete managed test project remains green: 647/647 tests, zero
  skipped.
- `git diff --check` passes.

**Operational behavior**

- Persistent bridge unavailability does not itself kill `authd`; requests
  continue to fail closed and event delivery retries through the durable spool.
  P2-12 supervises process availability, not downstream bridge health.
- A crashing/misconfigured `authd` may enter an intentional Compose restart
  loop. Logs retain the child status or readiness failure that caused each
  restart. This is preferable to presenting a nominally running Samba service
  that cannot authorize or deliver lifecycle work.
- Complete-unit restart disconnects active SMB sessions. This is an explicit
  consequence of restoring a clean authorization boundary and must be included
  in availability/runbook expectations.
- Docker restart backoff and deployment-orchestrator alerting remain platform
  concerns; the container now emits an unambiguous non-zero failure for them to
  act on.

**Validation still required**

- In the deployable stack, send SIGKILL to the real `kaimo_authd`; measure time
  until PID 1 exits, confirm `smbd` and active sessions terminate, observe
  Compose restart, and verify the service returns healthy after initial
  reconciliation.
- Repeat with real `smbd` failure and confirm `authd` is reaped and the durable
  event spool is recovered after restart.
- Stop the complete container normally and confirm signal forwarding finishes
  within the five-second grace without SIGKILL or orphan processes.
- Replace/remove the authd socket and PID file independently and verify health
  transitions on the next probe even before a process exit is observed.
- Force persistent authd configuration failure and verify restart-loop logging,
  operator alerting, and recovery after configuration correction.

**Next planned finding:** P2-13 — define the accepted user/service/share
revocation SLA, reduce or eliminate polling windows, and specify how already
open sessions and handles are terminated.

### 2026-07-29 — P2-13: Bounded polling and active-handle revocation

**Status:** Implemented and regression-tested. Deployment-shaped timing and
real SMB handle verification remain release gates.

**Problem**

The three desired-state reconcilers did not share one defensible revocation
contract. Share polling had already been reduced to two seconds by P1-10 and
closed a removed or moved share, while user and configuration polling still
ran every 60 seconds. User removal revoked the reusable credential and local
groups but left existing authenticated sessions and open handles alive.
Initial reconciliation also exhausted its retries and then continued to start
Samba, potentially exposing stale passdb, registry, or global configuration.

A failed periodic reconciliation was recorded by health state, but the loop
ignored the failure. Docker does not restart a merely unhealthy container by
default, so active sessions could remain attached to an uncertain effective
state indefinitely.

**Architecture decision and SLA**

Polling is retained for these three small, bounded desired-state exports. A
second push/invalidation transport would need its own authenticated durable
delivery, replay, ordering, and restart semantics and would not remove the need
for full reconciliation.

The production default is two seconds for shares and global configuration,
with an enforced one-to-five-second range. User sync is fixed at 60 seconds
because it is the hash-bearing credential
export protected by the separate two-per-60-second rate limit. Under healthy
operation, the next reconciliation starts within the relevant interval after
a committed change becomes visible to the bridge; revocation completes after
the bounded RPC/export and verified local mutation runtime. This is an
operational target rather than a database-to-Samba real-time transaction.

Already-open handles must be terminated when the global SMB service, a share,
or a managed user is explicitly disabled or deleted. Share removal/path change
uses a targeted `close-share`; service disable closes all shares. Samba 4.19
does not provide a reliable username-selective close operation, so user
revocation deliberately closes every registry share. The availability impact
to unaffected clients is accepted in favor of deterministic identity
revocation.

General ACL edits remain outside this desired-state polling contract.
Subsequent VFS authorization observes them subject to the bounded authd cache
TTL, while already-open handles are not selectively invalidated without a
future ACL-revision push/index capable of identifying affected sessions.
A user-revocation target below 60 seconds likewise requires a hash-free
identity revision/invalidation feed; increasing the NT-hash export frequency
would defeat its abuse limit and unnecessarily process credentials.

**Implemented**

1. Added validated `KAIMO_USER_SYNC_INTERVAL_SECONDS`,
   `KAIMO_SHARE_SYNC_INTERVAL_SECONDS`, and
   `KAIMO_CONFIG_SYNC_INTERVAL_SECONDS`. User values other than 60 seconds and
   share/config values outside 1-5 seconds stop the entrypoint. Compose and
   `.env.example` expose the 60/2/2-second production defaults. The shared
   `validate-sync-interval.sh` helper makes the exact bounds independently
   regression-testable.
2. Refactored startup reconciliation into a common required gate. Samba no
   longer starts after merely exhausting 30 bridge/local retries: users,
   shares, and configuration must each publish successful convergence.
3. Added `revoke-samba-sessions.sh`. It enumerates registry shares, excludes
   `global`, and closes every active share through `smbcontrol smbd
   close-share`. Absence of `smbd` during initial convergence is a successful
   no-op; enumeration or close failure is fatal.
4. User reconciliation invokes the global revoker after passdb credential,
   storage/authd group, and POSIX-lock convergence whenever one or more managed
   users disappear.
5. The new managed-user ownership boundary is published only after session
   revocation succeeds. A partial close therefore leaves stale identities
   owned by the next idempotent retry.
6. Added `sync-cycle.sh` around every periodic component run. A failed export,
   validation, mutation, or verification closes all active registry shares
   because the effective local state is uncertain. The runner's failure state
   remains visible to `sync-health.sh`; a later successful cycle restores it.
   Successful high-frequency cycles stay quiet. Per-cycle output is captured
   in a private temporary file, emitted only on failure, and always removed.
7. If fail-closed session termination cannot be proven, the periodic entrypoint
   worker sends SIGTERM to PID 1. The P2-12 supervisor terminates and reaps
   `authd`/`smbd`; Compose's `unless-stopped` policy restarts the complete
   security unit.
8. Added a focused shell regression covering accepted/rejected interval
   boundaries, exact registry-share closure, exclusion of `global`, pre-smbd
   no-op behavior, no revocation on a successful cycle, global containment on
   sync failure, and fatal escalation when the close action fails.
9. Extended the user-sync regression to prove a removed managed identity
   invokes active-session revocation and that a close failure preserves the
   former managed boundary until a retry repeats and completes the action.

**Validation completed**

- The focused P2-13 revocation-policy regression passes under Bash 5.2.
- `docker compose config --quiet` succeeds with the 60/2/2-second defaults.
- The uncached pinned Samba 4.19.5 `build-runtime` stage passes. It executes the
  complete shell/native helper suite, rebuilds and links the real ABI-49 VFS
  module, and packages the current runtime scripts.
- The complete managed test project passes: 647 tests, 0 failures, 0 skipped.
- `git diff --check` passes.

**Validation still required**

- With a file held open over SMB, disable its user and measure commit-to-handle
  failure. Repeat for share disable/delete, share path change, and global
  service disable at interval values 1, 2, and 5 seconds, and user disable at
  60 seconds.
- Inject bridge unavailability, malformed exporter output, registry failure,
  and `smbcontrol` failure during active sessions. Confirm the first three
  close all shares and recover health after convergence; confirm an unprovable
  close terminates/restarts the complete container unit.
- Verify clients receive an expected disconnect/reconnect experience and that
  durable close-event processing remains correct when revocation terminates
  handles abruptly.

**Next planned finding:** P2-14 — remove hard-coded development credentials
from operational startup and health paths.

### 2026-07-29 — P2-14: Removed development credentials from operational paths

**Status:** Implemented and regression-tested. A live authenticated SMB
operation matrix with operator-provisioned, short-lived test credentials
remains a release gate.

**Problem**

The production VFS entrypoint created a fixed test account by default. The
container healthcheck and the runtime audit guard reused that account and
placed its password directly in `smbclient` command arguments. The user
reconciler also treated the same default identity as implicitly unmanaged.
Consequently, a development credential could survive into an operational
deployment, evade desired-state cleanup, and be observable through process
inspection.

The legacy Phase-0 entrypoint and the manual self-test repeated the same
defaults. Although those paths are diagnostic rather than production
orchestration, keeping functional fallback credentials in executable artifacts
made accidental insecure use too easy.

**Architecture decision**

Liveness/readiness must not depend on any reusable SMB identity. The health
contract is therefore split by responsibility:

1. authd health proves the protected socket, PID-file trust properties,
   liveness, and executable identity;
2. smbd health proves equivalent PID-file/process identity and a successful
   `smbcontrol smbd ping`; and
3. synchronization health proves that the local Samba desired state is recent
   and successfully converged.

This combination verifies the supervised security unit without maintaining a
credential solely for health. An authenticated SMB request remains valuable
as an end-to-end release test, but its identity must be explicitly provisioned
for that test and must not become a runtime dependency.

The former audit login guard is not retained in a different credential form.
On probe failure it removed `full_audit`, silently weakening the configured
audit policy to preserve availability. Audit operation-name compatibility is
instead owned by the pinned Samba 4.19/ABI-49 build and the P2-15 live operation
matrix. Invalid audit configuration must fail release validation rather than
trigger a production downgrade.

**Implemented**

1. Removed test-user creation, password defaults, and `smbpasswd` invocation
   from `entrypoint.vfs.sh`. The production runtime now receives Samba
   identities only through the managed user reconciliation path.
2. Changed `sync-users.sh` so the unmanaged-user set is empty by default.
   Operators can still declare exceptional pre-existing identities explicitly
   with `KAIMO_UNMANAGED_SAMBA_USERS`; no development identity is implicitly
   protected from reconciliation.
3. Removed the credential-bearing `smbclient` audit probe and its automatic
   `full_audit` rollback from `sync-config.sh`.
4. Added `smbd-health.sh`. It rejects a missing, symlinked, non-regular,
   non-root-owned, or group/other-accessible PID file; rejects a malformed or
   dead PID; compares `/proc/<pid>/exe` with the expected smbd executable; and
   requires `smbcontrol smbd ping`.
5. Extended `supervise-samba.sh` to publish the smbd PID atomically as a
   root-owned mode-0600 readiness file after process start and to remove both
   authd and smbd PID files on startup cleanup, signal handling, peer failure,
   and normal exit.
6. Replaced the Compose health login with the accountless authd, smbd, and
   synchronization checks.
7. Changed `selftest.sh` to require `KAIMO_SELFTEST_AUTH_FILE`. It rejects
   missing, symlinked, non-regular, foreign-owned, or group/other-accessible
   files, extracts only a validated username needed for local ownership setup,
   and invokes every SMB operation through `smbclient -A`.
8. Changed the Phase-0 `entrypoint.sh` to require an explicit
   `KAIMO_SPIKE_USER` and `KAIMO_SPIKE_PASSWORD_FILE`. The protected secret is
   read only for the bounded import operation and reaches `smbpasswd` through
   standard input, never an argument or exported environment variable.
9. Updated the Samba operational guide so examples use protected
   authentication files, describe secret provisioning and permissions, and
   distinguish accountless container health from explicit end-to-end protocol
   testing.
10. Added `test-operational-credentials.sh` to reject the former literal
    credential/default variables in operational scripts, enforce `smbclient
    -A`, and prove that the manual self-test refuses absent or over-permissive
    authentication files. Extended the supervisor regression with smbd PID,
    identity, control-ping failure, and cleanup assertions.

**Security properties**

- The production image contains no automatically created reusable SMB health
  or audit identity.
- No operational health, audit, or documented client command places a password
  in a process argument or environment variable.
- Health binds readiness to the exact authd and smbd processes started by PID 1
  and to successfully converged synchronization state.
- Manual authenticated checks are opt-in, use an owner-only file, and fail
  closed when that file is absent or unsafe.
- Explicit unmanaged Samba identities remain possible for controlled
  migrations, but require a visible operator configuration decision.

**Validation completed**

- Shell syntax validation passes for every changed and added shell script.
- The focused operational-credential and supervisor regressions pass under
  Bash 5.2, including smbd control-ping failure and unsafe authentication-file
  rejection.
- `docker compose config --quiet` accepts the accountless composed healthcheck.
- The uncached pinned Samba 4.19.5 `build-runtime` stage passes the complete
  shell/native helper suite, rebuilds and links the ABI-49 VFS module, and
  packages the new health script.
- The complete managed test project passes: 647 tests, 0 failures, 0 skipped.
- `git diff --check` passes.

**Validation still required**

- In the deployable stack, kill and replace smbd independently and prove the
  protected PID identity check, control ping, supervisor exit, Compose restart,
  mandatory initial convergence, and health recovery behave as documented.
- Run positive and negative authenticated SMB enumeration/open/write/rename/
  unlink tests with a short-lived release account supplied only through an
  owner-only authentication file. Inspect `/proc/*/cmdline`, container
  environment, logs, and the final passdb to confirm the password and any
  undeclared test identity are absent.
- Enable every supported audit operation against the pinned runtime and execute
  the corresponding SMB operation matrix. A renamed/invalid operation must
  block release; production configuration must never strip the audit module
  automatically.
- Verify the target secret-management mechanism creates the Phase-0 password
  and release-test authentication files with the documented owner and mode
  inside the container namespace.

**Next planned finding:** P2-15 — enforce the pinned Samba source version and
VFS ABI expectations in CI and add the audit/VFS operation compatibility gate.

### 2026-07-29 — P2-15: Enforced Samba source/version/VFS ABI compatibility gate

**Status:** Implemented and verified in the pinned Samba 4.19.5
`build-runtime` image. The complete deployable-stack release matrix remains a
separate milestone-4 gate.

**Problem**

The production Dockerfile downloaded a version-named Samba archive and built
the stub, runtime, and real module from that tree, but the assumption remained
implicit. The archive content was not checksum-pinned, ABI 49 was documented
rather than asserted, `testparm` was not a build gate, and the existing live
VFS probes were copied into the image without being executed. A future
Dockerfile edit, source substitution, ABI change, or renamed `full_audit`
operation could therefore survive ordinary .NET CI and fail only after
deployment.

**Architecture decision**

The deployable image build is the compatibility authority. A source-only
workflow cannot prove that the installed `smbd`, its private libraries, the
real Kaimo module, and Samba's runtime operation table agree. The CI workflow
therefore builds the same `build-runtime` graph used by production and makes
the live module/audit probe an image-build dependency.

`samba-build.env` is the single review point for version, archive digest, and
VFS interface. Upgrades are intentional multi-file compatibility changes, not
automatic substitutions: the patch, callback signatures, operation names,
access constants, live matrix, rollback image, and rollout plan must be
reviewed together.

**Implemented**

1. Added `samba-build.env` with Samba 4.19.5, the pinned upstream tarball
   SHA-256, and `SMB_VFS_INTERFACE_VERSION=49`.
2. Changed both `Dockerfile.vfs` and `Dockerfile.src` to consume that contract,
   verify the downloaded archive with `sha256sum` before extraction, and use a
   version-independent `/build/samba-source` path. Alternate source-cache and
   production builds can no longer drift through duplicated version literals.
3. Added `verify-samba-build.sh`. It validates the pin-file syntax, requires
   exact `smbd --version` equality, checks ABI 49 in Samba's source header,
   locates the installed VFS module, requires the real Kaimo build marker, and
   runs `testparm` against the installed configuration.
4. Added `test-vfs-operation-compatibility.py`. It creates isolated Samba
   private/state/cache/lock directories, an ephemeral POSIX/passdb identity,
   and a mode-0600 smbclient authentication file. No reusable operational
   credential or host state is used.
5. The test starts the real self-built `smbd` with
   `kaimo_bridge full_audit` and a protocol-v3 local authorization double. It
   performs mkdir, upload/write, download/read, rename, delete, rmdir, and list
   through SMB3, verifies downloaded content and final filesystem state, and
   requires CONNECT, OPEN, DELETE_AUTH, and RENAME_AUTH to reach the VFS
   authorization boundary.
6. The same test requires successful audit records for `connect`,
   `disconnect`, `openat`, `close`, `renameat`, `unlinkat`, and `mkdirat`.
   A module-load failure, invalid/renamed audit operation, missing record,
   denied SMB operation, content mismatch, or leftover object fails the build.
7. Wired the static assertion and live matrix into `build-runtime` after the
   existing native and shell suites. The test account and isolated state are
   removed before the Docker layer is published.
8. Added the path-filtered `.github/workflows/samba-vfs-compatibility.yml`.
   Pushes and pull requests affecting `src/samba-vfs/**` build the pinned
   `build-runtime` target on Ubuntu 24.04 with a dedicated BuildKit cache whose
   layers are invalidated by the pin-file content. The workflow also supports
   explicit manual execution.
9. Documented the mandatory Samba upgrade procedure and clarified that this
   minimum gate complements rather than replaces the complete milestone-4
   deployable-stack matrix.

**Security and release properties**

- The downloaded source is content-pinned, not trusted solely because its file
  name contains `4.19.5`.
- The exact runtime version and source VFS ABI are executable assertions.
- The module is proven loadable alongside the installed private Samba
  libraries from the same build.
- Every audit operation used by production configuration is resolved by the
  pinned runtime and observed during a real SMB request.
- Relevant Samba-VFS changes cannot pass CI by running only managed or
  source-level tests.
- The compatibility test credential is ephemeral, file-supplied, isolated to
  the build, and never becomes a production health or startup dependency.

**Validation completed**

- Python syntax compilation passes for the new live compatibility test.
- The workflow file limits execution to the intended Samba-VFS change surface
  plus manual dispatch.
- The checksum-pinned Samba 4.19.5 source build completes and compiles the real
  `kaimo_bridge` module against VFS ABI 49.
- `verify-samba-build.sh` confirms runtime version, source ABI, real module
  marker, and `testparm`.
- The live SMB/full_audit compatibility matrix passes every configured
  operation name and verifies authorization call coverage, transferred bytes,
  and cleanup.
- All pre-existing native and shell build-runtime regressions continue to
  pass.
- The complete managed test project remains green.
- `git diff --check` passes.

**Remaining release work**

- Run the full deployable Compose stack with the real bridge and
  operator-provisioned short-lived credentials. Cover append, truncate,
  delete-on-close, attributes, EAs, permissions, ownership, hardlinks,
  symlinks, server-side copy, negative rename cases, snapshots, and concurrent
  revocation.
- Add Windows client coverage for Explorer enumeration and Previous Versions;
  the Linux `smbclient` compatibility gate does not prove Windows UX behavior.
- Exercise the documented Samba upgrade and rollback process once against a
  candidate version before relying on it operationally.
- Configure branch protection so `Samba VFS Compatibility` is a required
  status check. The workflow is present in source, but repository policy is an
  external control.

**Next planned workstream:** complete the remaining milestone-4 live release
matrix and reconcile its evidence with the older P0/P1 runtime-verification
notes and section 11 checklists.

### 2026-07-29 — P2-16: Corrected canonical Samba registry verification

**Status:** Implemented and regression-tested. The defect was identified by
the first deployable-stack startup check after P2-15 and corrected without
weakening mandatory initial convergence.

**Observed failure**

The Samba container repeatedly restarted before `smbd` supervision became
ready. User and share synchronization succeeded once the bridge was available,
but configuration synchronization emitted:

- `smb encrypt: '<unset>' -> 'default'`;
- `FAILED to read back 'smb encrypt'`;
- `config did not converge; refusing to start Samba`.

The composed healthcheck concurrently reported that the authorization socket
was unavailable. That message was a downstream consequence: the entrypoint
correctly refused to hand control to the authd/smbd supervisor after the
mandatory configuration phase failed.

**Root cause**

The registry mutation itself succeeded. In the pinned Samba 4.19.5 runtime:

- `net conf setparm global "smb encrypt" default` is accepted;
- `net conf list` exposes `server smb encrypt = default`;
- `net conf getparm global "smb encrypt"` fails because that exact registry
  key does not exist;
- `net conf getparm global "server smb encrypt"` and `testparm
  --parameter-name="smb encrypt"` both return `default`.

The generic `apply` helper assumed that Samba's accepted input name and
persisted registry name were always identical. Its read-after-write check
therefore classified a correct effective setting as divergence. Compose's
restart policy made the deterministic startup failure appear as repeated new
errors even though no operator action occurred.

**Implemented**

1. Extended `apply` with an optional canonical read parameter. Existing
   callsites remain same-name set/get operations by default.
2. Changed encryption reconciliation to write `smb encrypt` and compare/read
   `server smb encrypt`.
3. Applied the canonical name both before mutation and during final
   verification. A converged state no longer causes a redundant write or
   reload, while a missing, incorrect, failed, or ignored mutation still fails
   closed.
4. Improved failure diagnostics so future alias problems name both the
   requested setting and the registry key used for verification.
5. Changed the focused `net` test double to reproduce Samba 4.19.5
   canonicalization rather than storing the input key verbatim.
6. Added an explicit assertion that the resulting test registry contains
   `server smb encrypt = required` and no noncanonical `smb encrypt` entry.
7. Added `test-sync-config-samba-registry.sh` to run the reconciler against the
   real pinned Samba `net conf` binary and isolated registry directories. It
   verifies the canonical key, rejects the alias lookup, and requires a
   no-change second pass.
8. Wired that integration regression into the `build-runtime` gate so CI and
   production-image builds exercise real Samba canonicalization.
9. Updated the operational guide, release gates, synchronization checklist,
   production roadmap, and source-evidence index.

**Preserved security properties**

- Initial user, share, and configuration synchronization remains mandatory
  before Samba starts.
- A genuine mutation or verification failure still prevents startup.
- The effective value is verified from Samba's persistent registry state, not
  inferred from a successful command exit or log line.
- The change is limited to parameter-name canonicalization; it does not relax
  signing, encryption, health, restart, or authorization policy.

**Validation**

- Shell syntax validation passes for the changed reconciler.
- The focused synchronization regression passes with the Samba-compatible
  registry test double, including the pre-existing mutation-failure,
  ignored-mutation, invalid-schema, and size-limit cases.
- The isolated integration regression passes against the real Samba 4.19.5
  `net conf` implementation and proves idempotent canonical read-back.
- The complete pinned `build-runtime` test graph passes, including the existing
  shell/native suites, version/ABI/module assertions, `testparm`, and live
  SMB/full_audit matrix.
- The corrected Compose image was built and the Samba container was recreated.
  No `FAILED to read back 'smb encrypt'` message occurred. Full initial
  convergence could not complete because the Visual Studio Bridge container
  contained only its debug-helper wait process at verification time; no Bridge
  application was listening. This separate development-runtime prerequisite
  must be restored before composed health can be asserted.

**Next planned workstream:** continue the remaining milestone-4 deployable
release matrix. Treat every accepted Samba configuration alias as distinct
from its persistent registry representation until verified against the pinned
runtime.

### 2026-07-29 — P2-17: Prevented reconciliation-lock inheritance

**Status:** Implemented, regression-tested, and verified in the pinned
build-runtime image. The deployable stack exposed the defect after P2-16
allowed initial configuration convergence to complete. Final post-rebuild
Compose observation is pending a running Visual Studio Bridge application.

**Observed failure**

Samba, authd, and the Bridge started successfully and Docker reported the
Samba container as healthy. Immediately afterward, every two-second config
cycle logged:

- `config reconciliation is already running`;
- `config reconciliation failed (rc=1); closing active sessions`;
- `closed 6 active share boundary/boundaries`.

The result was continuous disconnection of all registry shares despite no
configuration change or operator action.

**Root cause and evidence**

`run-sync.sh` opened `/run/kaimo-sync/config.lock` as descriptor 9 and held a
nonblocking `flock` while calling `sync-config.sh`. When configuration enabled
WS-Discovery, that script launched `wsdd` in the background. The child and its
daemon descendant inherited every open runner descriptor.

Live process inspection proved the ownership leak:

```text
/proc/<wsdd-pid>/fd/9 -> /run/kaimo-sync/config.lock
```

The runner exited normally and published `config.last-success`, but the
reparented `wsdd` process kept the same open file description and therefore
the lock. All later runner instances failed before executing config export.
Because the failure occurred at lock acquisition, it did not publish
`config.last-failure`. The healthcheck consequently remained green until the
180-second maximum age elapsed, while `sync-cycle.sh` treated each lock miss as
uncertain state and revoked every share.

**Implemented**

1. Kept descriptor 9 open in the runner itself until command completion and
   atomic last-success/last-failure publication.
2. Executed every reconciliation command with descriptor 9 explicitly closed.
   This applies generically to users, shares, and config; all child and
   background descendant processes inherit the closed state.
3. Reserved exit status 75 (`EX_TEMPFAIL`) for runner-owned nonblocking lock
   contention.
4. Changed `sync-cycle.sh` to recognize status 75 as a skipped overlap. It
   neither publishes a synthetic failure nor invokes session revocation.
5. Preserved stale-owner detection: a running owner must publish a new success;
   otherwise `sync-health.sh` fails after the configured freshness window.
6. Prevented status ambiguity by mapping a reconciler-originated exit 75 to a
   normal published failure before returning to the cycle.
7. Extended `test-sync-runner.sh` with a live long-running descendant. The test
   proves the descendant stays alive without the lock descriptor and that an
   immediate following config reconciliation can acquire the lock.
8. Added assertions that genuine contention returns exactly 75 without a
   failure marker and that command-originated 75 cannot masquerade as
   contention.
9. Extended `test-revocation-policy.sh` to prove a skipped overlapping cycle
   does not call the revoker, while real reconciliation failures retain the
   existing fail-closed containment behavior.
10. Updated the operational guide, release gates, checklist, production
    roadmap, and source-evidence index.

**Security and availability properties**

- Serialization still covers mutation plus durable result publication.
- Reconcile descendants cannot outlive the command while retaining its lock.
- Actual export, validation, mutation, read-back, or publication failures
  still close active sessions.
- Mere lock contention is no longer misrepresented as uncertain Samba state.
- A stuck lock owner cannot remain healthy indefinitely; health freshness,
  rather than repeated destructive revocation, is the escalation mechanism.
- The special contention status cannot be forged accidentally by a failing
  reconciler command.

**Validation**

- Focused runner/health regression passes, including contention, reserved
  status translation, descendant lifetime, descriptor inspection, lock
  reacquisition, failure publication, recovery, and stale-state rejection.
- Focused revocation-policy regression passes for success, overlap, genuine
  failure, successful containment, and unprovable containment.
- The complete pinned `build-runtime` graph passes, including the new runner
  and revocation regressions, real Samba registry integration, module/version/
  ABI checks, and the live SMB/full_audit operation matrix.
- The corrected Compose image was built and the Samba container was recreated.
  Initial reconciliation correctly published `users.last-failure` while the
  Bridge was absent. After the bounded retry window the container restarted
  once under its declared policy. The Visual Studio Bridge container contained
  only its debug-helper wait process, so initial convergence did not reach
  config or start `wsdd`.
- Once the Bridge application is running, deployable-stack observation must
  confirm that the running `wsdd` process
  has no descriptor for `/run/kaimo-sync/config.lock`, periodic config
  reconciliations complete, no repeated share closures occur, and Docker
  health remains green for a fresh successful state.

**Next planned workstream:** continue the milestone-4 live release matrix and
apply the same descendant-descriptor audit to any future background process
started from a serialized reconciliation context.
