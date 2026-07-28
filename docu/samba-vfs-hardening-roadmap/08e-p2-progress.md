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
