# P2 – Medium Severity and Defense in Depth

[← Table of contents](README.md)

## 7. Medium-severity and defense-in-depth findings

### P2-01: User/share context strings are silently truncated

> **Remediation status (2026-07-28): Verified in managed tests and the pinned
> Samba 4.19.5 build-runtime image. Live long-name rejection remains part of
> the release operation matrix.**

The connection context stores user and share names in 128-byte arrays. The database username limit is character-based, so UTF-8 input can exceed 127 bytes. Silent truncation can cause denial, incorrect lookup, or prefix identity confusion.

**Fix:** dynamically allocate exact strings and enforce protocol byte-length limits at account/share creation and bridge ingress.

**Implemented fix:** the VFS connection context now owns exact heap copies of
the authenticated Samba username and connected share, with complete cleanup on
connect failure, handle destruction, and partial allocation failure. The common
local protocol defines and tests the same 32-byte username and 64-byte share
limits introduced by P1-18; both the VFS and `authd` reject invalid contexts
before authorization or gRPC. The Core domain, share repository, administration
UI, and Auth/Authz/Event/Snapshot bridge entry points independently enforce the
same ASCII syntax, reserved-name rules, and byte limits.

### P2-02: Share-relative canonicalization is inconsistent

> **Remediation status (2026-07-28): Verified in focused native tests and the
> pinned Samba 4.19.5 build-runtime image. Live SMB path-shape coverage remains
> part of the release operation matrix.**

`kaimo_share_rel()` is applied in selected snapshot and delete paths but not uniformly in create, readdir, close, rename, and mkdir. Some Samba call sites may produce connectpath-prefixed names.

Its prefix test also does not require a path-separator boundary after `connectpath`.

**Fix:** one canonical path routine, with boundary-aware connectpath stripping, called by every hook before authorization or event emission.

**Implemented fix:** all authorization, lifecycle, listing, reserved-namespace,
and snapshot paths now use `kaimo_canonical_share_path()`, backed by the
independently testable `share_path.h` implementation. It accepts already
share-relative and connectpath-prefixed Samba names, maps root spellings to the
empty share-relative path, strips leading `./`, and requires a complete
component boundary before removing a non-root connectpath. Root connectpaths
are handled explicitly, so `/folder` correctly becomes `folder` without
weakening the prefix-collision rule.

### P2-03: Bridge path validation is inconsistent

> **Remediation status (2026-07-29): Verified in the complete managed test
> project. Live malformed-path probes through Samba remain part of the release
> operation matrix.**

Delete checks `ShareRelativePath.IsValid()`, while Open and Snapshot primarily call `Normalize()` only. Snapshot materialization also uses `Path.Combine()` directly rather than the storage layer's containment-checked `ToAbsolutePath()`.

**Fix:** validate raw input before normalization, reject NUL/`..`/absolute/internal paths, then resolve through one containment-checked storage API. Validate persisted `FileVersion.FilePath` before using it as an output path.

**Implemented fix:** `ShareRelativePath.TryNormalizeStrict()` now performs
raw-input validation before canonicalization. It rejects null input, rooted and
drive-qualified paths, control characters including NUL, complete `..`
segments, disallowed roots, and every case-insensitive top-level `.kaimo-*`
namespace. Safe `.` segments and duplicate separators are canonicalized only
after those rejection checks.

`ShareRelativePath.ToContainedAbsolutePath()` is the common lexical containment
resolver used by `FileSystemStorage`, Authz filesystem state checks, and
snapshot cache materialization. It resolves against a fully qualified root and
accepts only the root itself or a descendant on a complete separator boundary.
The storage layer explicitly permits server-owned internal paths because the
durable close-capture workflow requires them; all client-facing Authz, Event,
and Snapshot RPCs reject the same namespaces at ingress.

Open, delete, rename source/destination, close, mkdir, lifecycle delete/rename,
snapshot enumeration, and snapshot resolution now consume the normalized value
returned by the strict validator. Invalid lifecycle events are rejected before
share resolution and before an idempotency receipt is claimed. Persisted
`FileVersion.FilePath` values are independently revalidated before cache-path
construction, reuse checks, projection reconciliation, or materialization.

### P2-04: Snapshot enumeration parsing trusts an unbounded decimal count

> **Remediation status (2026-07-29): Verified in focused native tests and the
> pinned Samba 4.19.5 build-runtime image.**

The native parser uses `atoi()` on a sidecar response and allocates based on the result. Invalid or overflowing input is not robustly handled.

**Fix:** use `strtol`/`strtoul` with full error checking, a maximum snapshot count, and a consistency check between count and received token records.

**Implemented fix:** P1-03 had already replaced the original decimal text
response and `atoi()` call with an unsigned, big-endian `uint32` field in the
framed local protocol. P2-04 completes the remaining semantic hardening through
the shared `snapshot_enumeration.h` contract:

- Snapshot enumeration is limited to 2,048 labels. At the fixed 24-byte
  `@GMT-yyyy.MM.dd-HH.mm.ss` representation, the complete payload is 57,348
  bytes and therefore remains below the 65,536-byte local response limit.
- `kaimo_authd` rejects oversized gRPC results and malformed token shapes before
  reserving/copying the local response vector.
- The VFS validates the complete response before allocating Samba label
  storage: count bound, minimum record bytes, every token's length/shape,
  exact record count, and absence of trailing payload must all hold.
- Malformed or oversized enumeration replies fail closed to an empty snapshot
  list, preserving the existing behavior for an unavailable snapshot service
  without exposing partially parsed labels.

### P2-05: Snapshot response buffers impose undocumented truncation

> **Remediation status (2026-07-29): Verified in managed tests, focused native
> tests, and the pinned Samba 4.19.5 build-runtime image. Live SMB boundary
> coverage remains part of the release operation matrix.**

The VFS uses a 64 KiB response buffer for enumeration. The header may advertise more tokens than fit, while only a subset is parsed. The protocol does not signal truncation or pagination.

**Fix:** add pagination/streaming or an explicit bounded count. Reject a response that exceeds the negotiated frame limit.

**Implemented fix:** Samba's shadow-copy enumeration callback is not paginated,
so Kaimo now uses an explicit newest-2,048 contract across the managed bridge,
sidecar, local wire format, and VFS:

- The managed bridge orders timestamps newest-first, formats and deduplicates
  the final `@GMT` labels, and returns at most 2,048. When older labels are
  omitted, it emits a warning with the available, returned, and omitted counts.
- The protocol declares the maximum enumeration payload as 57,348 bytes and
  fails compilation if that value ever exceeds the 65,536-byte response frame.
- `kaimo_authd` retains independent maximum/token checks and the local builder
  changes the entire response to error if serialization cannot complete.
- Frame readers reject declared response lengths above 65,536 before reading
  payload bytes. The P2-04 validator then requires the count and complete token
  records to consume the payload exactly, so a partial subset is never exposed.

The selected policy preserves the newest versions, which are the operationally
most relevant in Windows Previous Versions, while keeping a fixed-memory,
non-paginated Samba contract. Pagination is intentionally not introduced into a
callback that cannot communicate continuation state to SMB clients.

### P2-06: Snapshot cache validity uses size instead of content identity

> **Remediation status (2026-07-29): Implemented and regression-tested. Linux
> container and live SMB verification remain release gates.**

A same-sized partial/corrupt/tampered file is accepted as a valid cache hit.

**Fix:** include version ID/content hash in the cache key or verify the stored hash before reuse.

**Implemented fix:** immutable `FileVersion.ContentHash` SHA-256 metadata is the
content identity. Both individual-file and complete-folder cache-hit paths
require the expected length and SHA-256 digest to match before reuse. A missing,
short, oversized, or same-sized corrupt projection is rematerialized through a
unique sibling temporary file; the streamed source is independently checked
against the same length and digest before durable flush and atomic publication.
Invalid version metadata and source-content mismatches fail closed without
publishing a partial final file. Focused regressions cover a verified cache hit
without blob access and same-sized corruption repair for both concrete-file and
folder resolution.

### P2-07: Cleanup enumeration exception handling is incomplete

> **Remediation status (2026-07-29): Implemented and regression-tested.**

`SafeEnumerateDirectories()` returns a lazy enumerable from inside a `try`; exceptions may occur later during `foreach`, outside that local `try`.

**Fix:** materialize the directory list inside the protected block or use enumeration options with per-entry error handling.

**Implemented fix:** `SafeEnumerateDirectories()` now returns an
`IReadOnlyList<string>` materialized with `ToArray()` inside the protected
boundary. A directory that disappears concurrently is treated as an empty
snapshot. I/O and access failures are contained, logged with the affected root,
and likewise produce an empty snapshot, allowing the remaining sweep to
continue. Unexpected process-level and programming exceptions are no longer
hidden by an unqualified catch. All callers consume the stable snapshot, so no
filesystem access is deferred into their `foreach` loops. Regressions prove
that the returned collection does not change after a later directory creation
and that an I/O failure raised during enumeration cannot escape to the caller.

### P2-08: Cache configuration is not validated

> **Remediation status (2026-07-29): Implemented and regression-tested.**

Negative, zero, NaN, or extreme TTL/sweep/cap values can cause unexpected deletion, service termination, or a tight failure loop.

**Fix:** bind validated options at startup with minimum/maximum values and fail configuration validation before serving requests.

**Implemented fix:** `SnapshotCacheOptions` is bound from `Snapshots:Cache`
and validated through `IValidateOptions<T>` plus `ValidateOnStart()`. The bridge
refuses startup unless the cache root is an absolute valid path, TTL is finite
and between 1 minute and 365 days, sweep interval is finite and between 1 minute
and 24 hours, and the per-share cap is between 1 MiB and 100 TiB. Exact bounds
are accepted. The cleanup service receives one validated `IOptions` value and
no longer reparses mutable configuration during every sweep. Compose and
`.env.example` expose the same three policy settings with production defaults
and documented ranges. Regressions cover defaults, exact boundaries, relative
and empty roots, zero/negative values, `NaN`, positive infinity, underflow,
overflow, and DI-bound validation failure with configuration-key diagnostics.

### P2-09: RPC cancellation is not propagated

> **Remediation status (2026-07-29): Implemented and regression-tested at every
> bridge RPC boundary. Native live-timeout verification remains a release
> gate.**

Repository, ACL, version, and file-copy work generally ignores `ServerCallContext.CancellationToken`.

**Fix:** add cancellation-aware interfaces where missing and pass the request token through database calls, loops, and stream copies.

**Implemented fix:** every asynchronous bridge RPC now binds its repository,
authentication, configuration, ACL, version, share, or folder task to
`ServerCallContext.CancellationToken`. Authz rule/traversal loops, bulk user and
share projection loops, and snapshot label formatting check cancellation
between work units. Snapshot folder queries, materialization reservations,
leases, decompression, hashing, output writes, and cleanup already accept the
token directly from P1-09. Lifecycle receipt claim operations and pre-claim
user/share resolution are cancellation-aware. Once a durable lifecycle handler
has begun, it deliberately completes its idempotency receipt with
`CancellationToken.None`: abandoning a non-cooperative mutation and releasing
its receipt could permit a concurrent retry and duplicate side effects.

Legacy Core read interfaces that do not yet accept a token are wrapped with
`Task.WaitAsync(requestToken)`. This releases the RPC and its continuation
immediately; a database provider operation already dispatched through such a
legacy interface may finish in its scoped context. Those operations are bounded
single reads. Expensive version queries and all streaming/copy work use native
cancellation-aware APIs rather than only detaching the wait.

### P2-10: Bulk user export is unbounded and sequential

> **Remediation status (2026-07-29): Implemented, regression-tested, and
> verified in the pinned Samba 4.19.5 build-runtime image. Live multi-page
> reconciliation and concurrent-account-change behavior remain release
> verification.**

`ListUsers` loads all users and calls `GetNtHashAsync` one at a time. One corrupt hash can abort the whole RPC, and a large response can exceed gRPC message limits.

**Fix:** validate hashes individually, isolate corrupt rows, paginate/stream users, enforce exactly 16 bytes, and avoid an N+1 lookup/decryption pattern.

**Implemented fix:** `ListUsers` is now an explicit offset/page-size contract.
The bridge requires a non-zero page size and caps pages at 1,000 source rows.
Rejecting the protobuf default prevents an older, non-paginating native client
from treating the first page as a complete desired state during a mixed-version
rollout. The bridge also rejects offsets at or above 100,000 and fails with
`ResourceExhausted` rather than returning a partial desired state when another
page would cross that total limit. One rate-limit permit covers the logical
export's first page; bounded continuation pages require a short-lived
HMAC-authenticated token bound to the workload identity and exact next offset,
then retain request/completion audit events. An arbitrary non-zero offset
therefore cannot bypass the export rate limit.

`UserRepository` performs one `AsNoTracking` projection per page, ordered by
username and identity, selecting only active usernames and protected NT hashes.
`AuthenticationLookup` decrypts that bounded projection in memory, filters the
empty-password hash, enforces the common Samba username contract and exactly
16 decoded bytes, and isolates malformed ciphertext, invalid hex, invalid
names, and wrong-length values per row. Rejection logs contain counts only,
never hashes or usernames.

The native `kaimo_authsync` client requests 1,000-row pages, validates monotonic
continuation metadata, presence/absence of continuation tokens, and all
per-page/total/JSON limits, and buffers no more than 100,000 validated records
or 16 MiB of structured output. It emits the versioned JSON document only after
every page succeeds. A failed, malformed, expired, or over-limit continuation
therefore cannot become a partial `tdbsam` desired state. Offset pagination
intentionally provides bounded eventual convergence, not a database snapshot
across RPCs; account changes during one export are reconciled by the next
periodic run and remain part of the P2-13 revocation-SLA work.

### P2-11: Decrypted credentials are not minimized or cleared

> **Remediation status (2026-07-29): Implemented, regression-tested, and
> verified in the pinned Samba 4.19.5 build-runtime image. Live process-memory
> and tmpfs-mount verification remain release gates.**

NT hashes exist in managed byte arrays, protobuf copies, C++ strings, stdout capture, shell memory, and a temporary smbpasswd file.

**Fix:** minimize copies and lifetime, avoid shell text transport, zero mutable buffers where practical, and never persist hashes beyond the import transaction.

**Implemented fix:** the NT-hash protector now exposes a raw-byte decrypt path.
Encrypted hashes are decoded directly from a mutable 32-byte ASCII plaintext
buffer, and both that buffer and the decoded encryption blob are cleared in a
`finally` block. Legacy hex rows are decoded directly from their already
persisted string without making another plaintext string. Mutable key-derivation
and encryption plaintext buffers are likewise cleared.

The authentication lookup compares the raw 16-byte value against the
empty-password hash and clears every rejected buffer. `AuthGrpcService` copies
accepted bytes into protobuf and clears every lookup/batch-owned source array in
`finally`, including metadata failures and cancellation. Protobuf necessarily
owns its transport copy until serialization completes; managed runtimes cannot
guarantee clearing immutable strings or internal serializer buffers.

`kaimo_authsync` stores each retained hash in a move-only fixed 16-byte buffer.
Move operations clear the source, protobuf fields are released immediately
after copying, destruction wipes the retained bytes through a volatile write
loop, and hexadecimal JSON is streamed directly without constructing another
hash string.

`sync-users.sh` no longer captures the complete JSON document in a shell
variable. It writes exporter output to a random mode-0600 validation file,
checks the byte limit, validates the complete schema, and unlinks that JSON
before any Samba/POSIX mutation. The validated record file is unlinked as soon
as the private `smbpasswd` import is assembled; the import file is unlinked
immediately after `pdbedit` returns successfully, rather than at end of the
complete reconciliation. Error paths retain the existing `EXIT` cleanup.
Compose mounts `/run/kaimo-user-sync` as a root-owned 0700 `tmpfs` with
`noexec,nosuid,nodev`, so transient hash-bearing files never enter the
container's persistent writable layer.

Hashes remain necessarily present for bounded periods in gRPC/protobuf, kernel
pipe buffers, `jq`, `pdbedit`, and Samba's passdb import implementation. They
are never passed in process arguments or environment variables, never logged,
and are not retained in the durable managed-user state.

### P2-12: `authd` is not supervised independently

> **Remediation status (2026-07-29): Implemented, regression-tested, and
> verified in the pinned Samba 4.19.5 build-runtime and slim runtime images.
> Live crash/restart behavior remains a release gate.**

The entrypoint starts `kaimo_authd` in the background and then replaces itself with `smbd`. If `authd` exits, the container can remain nominally running while authorization fails closed indefinitely.

**Fix:** use a proper init/supervisor or merge process health into container health. Restart/fail the container when the sidecar is unavailable beyond a short threshold.

**Implemented fix:** `supervise-samba.sh` is now the container's final PID-1
process. It starts `kaimo_authd`, requires its Unix socket within a bounded
five-second readiness window, atomically publishes a root-owned mode-0600 PID
file, and only then starts `smbd`. It waits for both long-running processes. An
exit by either process terminates and reaps the peer, removes socket/PID
readiness state, and exits non-zero; even a clean child exit is abnormal for
the container unit. HUP, INT, QUIT, and TERM are forwarded to both children,
with a bounded five-second grace period before SIGKILL.

`authd-health.sh` independently requires the Unix socket, a non-symlink
root-owned mode-0600 supervisor PID file, a live PID, and an exact
`/proc/<pid>/exe` match to the installed `kaimo_authd` binary. The Compose
healthcheck runs this before synchronization and SMB login probes. The Samba
service uses `restart: unless-stopped`, so an unexpected `authd` or `smbd` exit
restarts the complete coupled unit instead of preserving SMB sessions against
a missing authorization/event/snapshot sidecar.

The supervisor refuses an unsafe pre-existing socket path and rejects invalid
readiness-attempt or shutdown-grace configuration. Startup readiness is bounded
to 1-600 100-ms attempts; shutdown grace is bounded to 1-30 seconds. Production
defaults are 50 attempts (five seconds) and five seconds respectively.

### P2-13: Disabled service/share behavior is polling-based

> **Remediation status (2026-07-29): Implemented, regression-tested, and
> verified in the pinned Samba 4.19.5 `build-runtime` image.
> Deployment-shaped timing and active-handle verification remain release
> gates.**

Configuration, share, and user sync run every 60 seconds. Existing open handles can continue even after desired state changes, and registry deletion alone does not revoke already-open file handles.

**Fix:** define a revocation SLA, add push/invalidation where needed, close affected shares/sessions immediately, and decide whether active file handles must be forcibly closed.

**Implemented fix:** user, share, and configuration reconciliation now use
independently validated intervals. Share and configuration state use a
one-to-five-second range and a two-second production default. User sync uses a
fixed 60-second interval because it is the separately
rate-limited, hash-bearing credential export. Under healthy operation, the
next reconciliation begins no later than the configured interval after a
committed change is visible to the bridge; convergence then requires the
bounded exporter and local mutation runtime. Samba refuses startup unless all
three desired-state documents have converged.

Disabled/deleted shares and path changes retain targeted `close-share`
behavior. Global service disable closes every registry share. A
disabled/deleted managed user now loses passdb credentials and Kaimo groups,
then triggers a global registry-share close because Samba 4.19 has no reliable
username-selective handle-revocation operation. This intentionally disconnects
unaffected clients as the cost of deterministically terminating the disabled
identity's sessions and open handles. The new managed-user boundary is
published only after that session revocation succeeds, preserving retry
ownership after a partial failure.

Any periodic user/share/config reconciliation failure makes the effective
local state uncertain and therefore closes every active registry share.
New tree connects and opens remain protected by the VFS bridge authorization
and fail closed during bridge loss. If the global close cannot be proven, the
periodic worker terminates PID 1; the P2-12 supervisor closes/reaps `authd` and
`smbd`, and Compose restarts the complete security unit.

This policy applies to service, share, and identity enablement state. General
ACL edits are evaluated on subsequent authorization operations, subject to the
separate bounded authd decision-cache TTL; they do not currently provide a
push signal capable of locating and closing only handles authorized by the old
ACL. Existing handles are therefore forcibly closed for explicit
service/share/user revocation, but not for an arbitrary ACL edit.

A user-revocation target below 60 seconds requires a separate hash-free
identity revision/invalidation feed. Reusing the NT-hash export at high
frequency would violate its two-per-60-second abuse limit and unnecessarily
increase plaintext credential processing.

### P2-14: Hard-coded development credentials are present in operational paths

The entrypoint and health/audit probes use the default `kaimotest`/`Passw0rd!` credentials unless overridden. Passwords are also passed in command arguments.

**Fix:** remove production defaults, require secrets, avoid password-in-argv where possible, and use a dedicated health mechanism that does not depend on a reusable account.

### P2-15: Samba ABI pinning lacks a CI enforcement gate

The module is built against Samba 4.19.5/ABI 49. The source version is pinned, but the repository does not prove that changes or alternate Dockerfiles retain the same version and ABI expectations.

**Fix:** add CI checks for the pinned tarball/version, build the module and Samba together, run `testparm`, load the module, and execute a minimal SMB operation matrix on every relevant change.
