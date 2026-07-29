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
