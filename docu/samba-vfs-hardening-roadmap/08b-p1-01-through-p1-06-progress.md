# Implementation Progress – P1-01 through P1-06

[← Progress overview](08-implementation-progress.md) · [Main table of contents](README.md)

### 2026-07-23 — P1-01: Bounded authd workers and client deadlines

**Status:** Implemented; pinned native build and isolated saturation runtime test verified.

**Solution implemented**

1. Replaced detached per-connection threads with a fixed pool
   (`KAIMO_AUTHD_WORKERS`, default 16, accepted range 1–256).
2. Added a bounded FIFO for accepted descriptors
   (`KAIMO_AUTHD_QUEUE_CAPACITY`, default 64, range 1–4096). When full, the
   accept loop writes the protocol's fail-closed `ERROR` response and closes
   the descriptor.
3. Applied `SO_RCVTIMEO` and `SO_SNDTIMEO` to every accepted socket
   (`KAIMO_AUTHD_IO_TIMEOUT_MS`, default 2000 ms, range 100–60000), so a client
   that never sends a request cannot hold a worker indefinitely.
4. Accepted descriptors use `SOCK_CLOEXEC`; deadline-setup failures close the
   connection. Invalid resource-limit configuration fails sidecar startup.
5. Added logarithmically sampled overload and receive-timeout counters to avoid
   turning an attack into unbounded log volume.
6. Added a container runtime regression test that saturates the Unix socket
   with silent clients and measures `/proc` thread/descriptor counts.
7. Updated the image build marker to `2026-07-23b bounded authd workers`.

**Validation completed**

- Pinned Samba 4.19.5 image builds successfully with the new sidecar.
- Capacity test with 2 workers, queue capacity 3, and 40 silent clients:
  thread count remained constant at 23 (including gRPC runtime threads), server
  descriptors peaked at 13, 35 overload clients received `ERROR`, and
  descriptors returned to the baseline of 8 after receive deadlines.
- `KAIMO_AUTHD_WORKERS=0` fails startup with exit code 1.

**Validation still required / deliberately separate**

- Run mixed live SMB authorization, event, and snapshot load to tune the
  production worker/queue values and observe latency under saturation.
- P1-03 was completed afterward with length-prefixed framing and full
  partial-I/O handling.
- P1-05 was completed afterward with VFS-side nonblocking I/O and strict
  end-to-end deadlines; P1-01 remains the complementary server-side bound.

**Next planned finding:** P1-02 — bound and expire the authorization cache globally.

### 2026-07-23 — P1-02: Bounded authorization decision cache

**Status:** Implemented; deterministic native unit tests and pinned image build verified.

**Solution implemented**

1. Replaced the global `unordered_map` with a mutex-protected LRU abstraction
   that updates recency on hits and updates.
2. Added a hard entry cap (`KAIMO_AUTHD_CACHE_MAX_ENTRIES`, default 10,000)
   and a conservative accounted memory cap
   (`KAIMO_AUTHD_CACHE_MAX_BYTES`, default 8 MiB). The budget includes two
   owned key strings plus fixed node/allocation allowance; bucket reservation
   is also limited by the byte budget.
3. Entries that would individually exceed the byte budget are never cached.
   New entries evict least-recently-used decisions until both caps are
   satisfied.
4. Expired entries are removed exactly on lookup and by an opportunistic full
   sweep on the first operation after each interval of at most one second, so
   expired unique paths cannot accumulate beyond the hard caps.
5. Made the TTL configurable (`KAIMO_AUTHD_CACHE_TTL_MS`, default 3000,
   accepted range 100–10,000 ms). This TTL is the documented maximum
   ACL-revocation delay for an already cached open decision.
6. Added monotonic hit, miss, eviction, expiration, and oversize-skip
   statistics. Cache occupancy/bytes/hit ratio and pressure counters are
   logarithmically sampled into logs.
7. Added deterministic native tests for LRU order, entry eviction, byte-budget
   eviction, oversize skipping, and expiry.
8. Updated the image build marker to `2026-07-23c bounded authz cache`.

**Validation completed**

- Native decision-cache test binary passes during the Samba image build.
- The pinned Samba 4.19.5 image, authd, gRPC clients, and VFS module compile.
- The P1-01 saturation regression still passes against the cache-enabled
  image: constant thread count, bounded descriptors, 35/40 predictable
  overload rejections, and descriptor recovery after receive deadlines.
- `KAIMO_AUTHD_CACHE_TTL_MS=10001` fails startup with exit code 1 instead of
  silently exceeding the documented revocation bound.

**Validation still required / deliberately separate**

- Tune entry/byte limits using production directory-listing cardinality and
  observe hit/eviction rates under live SMB load.
- A future ACL-change notification may invalidate matching entries
  immediately; until then, the configured TTL is the explicit revocation
  bound.
- P1-03 and P1-05 were completed afterward; framing correctness and
  VFS-side end-to-end deadlines are both covered.

**Next planned finding:** P1-03 — replace the local stream protocol with bounded, complete framing.

### 2026-07-23 — P1-03: Versioned, bounded local stream framing

**Status:** Implemented; deterministic protocol tests, pinned native build,
fragmentation runtime tests, and the existing saturation regression verified.

**Solution implemented**

1. Added one C/C++-compatible local protocol definition with a fixed 12-byte
   `KAIM` header: protocol version, enum operation, request/response kind,
   structured status, and unsigned big-endian payload length.
2. Replaced all tab/newline request and response messages for connect, open,
   delete authorization, rename authorization, lifecycle events, and snapshot
   enumeration/resolution with operation-specific binary schemas.
3. Encoded every variable field as a 32-bit length plus UTF-8 bytes. Parsers
   reject overlong fields, embedded NULs, invalid UTF-8, invalid booleans,
   missing fields, and trailing fields. Tabs and newlines are now ordinary
   field content instead of protocol delimiters.
4. Kept the request payload boundary at 8 KiB and added an explicit 64 KiB
   response boundary. `authd` validates headers and payload size before its
   bounded request allocation; the VFS validates response size against both
   the protocol maximum and caller capacity before reading payload bytes.
5. Added shared retrying `read_exact` and `write_all` loops that handle
   `EINTR`, short reads/writes, EOF in the middle of a frame, and
   `MSG_NOSIGNAL`.
6. Required every response to match the request operation. Queue saturation
   now returns a valid framed `OVERLOADED` response with operation `NONE`;
   malformed protocol responses remain distinguishable from infrastructure
   failures and always fail closed.
7. Converted OPEN granted masks to a binary `uint32`, snapshot sizes to
   `uint64`, and snapshot lists to a bounded count plus length-prefixed tokens.
   The VFS verifies the declared snapshot count against all received records
   before publishing labels.
8. Added deterministic serializer/parser, boundary, full-write, and
   byte-fragmentation tests plus an `authd` runtime regression for fragmented,
   truncated, oversized, and wrong-version frames.
9. Updated the module build marker to
   `2026-07-23d framed local protocol`.

**Validation completed**

- The shared protocol unit binary passes during the native image build,
  including one-byte fragmentation, 64 KiB full-write/read, delimiter
  preservation, embedded-NUL rejection, invalid-UTF-8 rejection, and request
  size enforcement.
- The pinned Samba 4.19.5 image builds successfully. `kaimo_authd`, all C++
  gRPC clients, and `vfs_kaimo_bridge.so` compile and link.
- The container runtime protocol test passes for one-byte request
  fragmentation, truncated payloads, an 8,193-byte request declaration, and an
  unsupported protocol version.
- The P1-01 saturation test still passes with the framed overload response:
  with 2 workers, queue capacity 3, and 40 silent clients, thread count stayed
  at 23, server descriptors peaked at 13 and returned to 8, and 35 clients
  received `OVERLOADED`.
- Full managed solution suite: 528 passed, 0 failed, 0 skipped.
- `git diff --check` reports no whitespace errors.

**Validation still required / deliberately separate**

- Live `smbclient` connect/list through the real VFS module was completed as
  part of P1-04. Open/delete/rename/event and snapshot behavior still needs the
  broader live SMB/Windows verification tracked separately.
- P1-04 was completed immediately afterward with private socket permissions,
  kernel peer credentials, and peer/session identity binding.
- P1-05 was completed afterward with nonblocking VFS-side connect and one
  strict deadline across complete framed send/receive operations.

**Next planned finding:** P1-04 — restrict the Unix socket and authenticate the
local peer/session identity. Completed immediately afterward.

### 2026-07-23 — P1-04: Private socket and authenticated local peers

**Status:** Implemented; pinned native build, negative peer-security tests, a
real `smbd` → VFS → `authd` connect/list test, and the managed regression suite
verified.

**Solution implemented**

1. Replaced the world-writable socket with `/var/run/kaimo` owned by
   `root:kaimo-authd` at mode `0750` and `authz.sock` at mode `0660`.
   Startup canonicalizes the parent (including `/var/run` → `/run`), validates
   its owner, group, and permissions, and only removes a stale path when it is
   an expected-owner Unix socket.
2. Added the dedicated `kaimo-authd` service group to container startup and to
   synchronized Samba users. The test account follows the same membership
   model.
3. Captured `pid`, `uid`, and `gid` with `SO_PEERCRED` before a connection can
   enter the bounded worker queue.
4. Bound every non-root request username to the exact UID returned by
   `getpwnam_r`. A group member can therefore submit its own identity but
   cannot claim another Samba/Kaimo user.
5. Treated the root-real-ID Samba worker as a trusted session carrier only
   after validating the configured peer executable as a root-owned,
   non-group/other-writable regular file. Dumpable root peers are matched by
   executable device/inode. Samba intentionally makes authenticated workers
   non-dumpable, so an `EACCES`/`EPERM` fallback additionally requires kernel
   UID 0 and Samba's exact `/proc/<pid>/stat` process-name forms (`smbd`,
   `smbd: …`, or `smbd[…]`) without granting the container `SYS_PTRACE`.
6. Added structured `UNAUTHORIZED_PEER` protocol responses. The VFS maps this
   status to a hard deny independently of `KAIMO_AUTHZ_FAILOPEN`; event-only
   requests from unauthorized peers are discarded.
7. Added startup validation for the trusted peer executable and configuration
   knobs `KAIMO_AUTHD_GROUP` and `KAIMO_AUTHD_PEER_EXECUTABLE`.
8. Fixed the image build to copy the unambiguous Waf runtime artifact
   `bin/modules/vfs/kaimo_bridge.so`. The previous `find | head` could select
   the old `.inst.so` stub even though the real module compiled. The build now
   also requires the real module's embedded build marker.
9. Updated the module marker to
   `2026-07-23e authenticated local peer`.

**Validation completed**

- The pinned Samba 4.19.5 image builds successfully; native protocol/cache
  tests pass and the installed module contains the real P1-04 build marker.
- The peer-security runtime regression verifies directory `0750`, socket
  `0660`, an accepted matching non-root UID, rejection of a mismatched claimed
  user, denial without socket-group access, and rejection of an untrusted root
  executable.
- An isolated live Samba regression authenticates a real SMB user, loads the
  real `kaimo_bridge.so`, passes its `CONNECT` frame through `authd`, and
  completes `smbclient ls`. The downstream bridge is intentionally absent and
  fail-open is enabled, proving that the local authenticated-peer path itself
  succeeded.
- The framed-protocol regression passes unchanged.
- The saturation regression passes with 2 workers, queue capacity 3, 40 silent
  clients, a stable 23 threads, descriptors returning from 13 to 8, and 35
  predictable overload rejections.
- User synchronization tests verify all synchronized users are added to both
  storage and `kaimo-authd` groups.
- `docker compose config --quiet` succeeds.
- Full managed solution suite: 528 passed, 0 failed, 0 skipped.

**Validation still required / deliberately separate**

- Exercise the full open/delete/rename/event/snapshot matrix from Windows and
  representative production clients. P1-04's actual connect/list path is
  covered, but it does not replace that broader compatibility run.
- P1-05 was completed immediately afterward with nonblocking VFS-side connect
  and strict end-to-end deadlines.

**Next planned finding:** P1-05 — bound all VFS-side local socket operations by
strict end-to-end deadlines. Completed immediately afterward.

### 2026-07-23 — P1-05: Strict VFS-side end-to-end deadlines

**Status:** Implemented; deterministic native deadline tests, pinned Samba
4.19.5 build, and a real stalled-sidecar SMB runtime regression verified.

**Solution implemented**

1. Added absolute `CLOCK_MONOTONIC` deadlines to the shared local-protocol
   helpers. Deadline-aware complete read/write loops use `poll` plus
   `MSG_DONTWAIT`; partial progress never resets the budget.
2. Opened every VFS-side Unix client socket with `SOCK_NONBLOCK |
   SOCK_CLOEXEC`. An in-progress connect waits only for the remaining budget
   and verifies completion through `SO_ERROR`.
3. Applied the same deadline instance to connect, the complete request frame,
   the complete response header, and the complete response payload.
4. Added independent configurable budgets:
   `KAIMO_VFS_AUTH_TIMEOUT_MS` (6000 ms),
   `KAIMO_VFS_SNAPSHOT_TIMEOUT_MS` (32000 ms), and
   `KAIMO_VFS_EVENT_TIMEOUT_MS` (250 ms). Values outside 10-60,000 ms fall
   back to logged bounded defaults.
5. Kept lifecycle notification delivery separate from authorization:
   notifications wait only for their short local enqueue budget and never for
   the downstream gRPC result. Durable spooling/acknowledgement remains P1-11.
6. Rejects overlong Unix-socket paths instead of silently truncating the
   configured endpoint.
7. Updated the module marker to
   `2026-07-23f bounded VFS local I/O`.

**Validation completed**

- The shared native protocol test now proves a silent/slow receiver cannot
  extend the receive deadline by making partial progress and proves a blocked
  sender exits within the same absolute budget.
- The pinned Samba 4.19.5 image builds successfully; native protocol/cache
  tests pass and the real VFS module compiles and links.
- A live regression loads the real module in `smbd`, connects it to a fake
  sidecar that accepts but never responds, and sets a 300 ms authorization
  budget. The SMB request failed closed with `NT_STATUS_ACCESS_DENIED` in
  0.364 seconds rather than pinning the worker.
- The normal immediate-denial regression still returns
  `NT_STATUS_ACCESS_DENIED` in 0.050 seconds, and the authenticated
  `smbd` -> VFS -> `authd` peer-identity regression passes with the new module.
- `docker compose config --quiet` passes. The managed test project passes:
  527 passed, 0 failed, 0 skipped.

**Validation still required / deliberately separate**

- Run sustained mixed authorization/snapshot/event load to tune production
  budgets and alerting.
- P1-11 remains open for durable event delivery; P1-05 only guarantees that a
  local enqueue attempt cannot block an `smbd` worker indefinitely.

**Next planned finding:** P1-06 — enforce strictly read-only snapshot opens
without bypassing the remaining VFS stack.

### 2026-07-23 — P1-06: Read-only, VFS-stack-safe timewarp access

**Status:** Implemented; pinned Samba 4.19.5 compilation and live SMB3
read/write/mutation regression verified.

**Solution implemented**

1. Rejects explicit write, append, EA/attribute write, delete, DACL/owner
   change, generic-write/all, create/overwrite, delete-on-close, allocation,
   EA, and security-descriptor intent before opening a timewarp object.
2. Expands generic read/execute requests, supports `MAXIMUM_ALLOWED`, and
   intersects the bridge grant with both the client's request and the fixed
   read-only snapshot mask.
3. Rejects mutating POSIX flags and passes a defensively sanitized `O_RDONLY`
   `vfs_open_how` to the next layer.
4. Replaced raw `openat(AT_FDCWD, ...)` with `SMB_VFS_NEXT_OPENAT` and a copied,
   validated Samba filename, following the pinned `vfs_shadow_copy2` pattern.
5. Returns `EROFS` for timewarp delete, rename, mkdir, and disabled snapshot
   redirects, preventing fallback to live share content.
6. Updated the module marker to
   `2026-07-23g read-only stacked snapshots`.

**Validation completed**

- The real module compiles and links against Samba 4.19.5/ABI 49.
- A live SMB3 regression reads historical bytes while the live file contains
  different data.
- The same regression proves overwrite, delete, rename, and mkdir attempts
  receive `NT_STATUS_MEDIA_WRITE_PROTECTED`; both live and cached data remain
  unchanged even though the cache file is POSIX-writable by the SMB user.
- `full_audit`, placed after `kaimo_bridge`, records the successful historical
  open, proving the redirect reaches the remaining VFS stack.
- The existing stalled-sidecar SMB regression still fails closed within its
  configured 300 ms budget, and `docker compose config --quiet` passes.

**Validation still required / deliberately separate**

- Exercise the Windows Explorer "Previous Versions" dialog and folder browsing
  against representative production clients.
- P1-07 was completed immediately afterward with atomic, content-verified
  materialization.

**Next planned finding:** P1-07 — make snapshot materialization atomic and
content-verified. Completed immediately afterward.
