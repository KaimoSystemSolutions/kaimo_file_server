# Implementation Progress – P1-13 onward

[← Implementation progress index](08-implementation-progress.md)

### 2026-07-28 — P1-13: Retry-idempotent rename version transitions

**Status:** Implemented; focused and complete managed regression suites pass.
Live bridge-crash/retry verification remains pending.

**Problem**

The durable P1-11 receipt suppresses an event only after every rename side
effect succeeds. If the bridge stopped after version history had moved but
before a later effect completed, retrying the same event found no versions at
the old path and treated every version at the destination as displaced. That
could delete history created after the original rename.

**Solution implemented**

1. The stable Samba event ID is threaded from `NotifyRename` through
   `IFileService` and `IFileVersionService` into the version repository.
2. Samba rename processing validates that the ID belongs to a matching
   `rename` receipt.
3. Destination displacement, source-history movement, and a
   `RenameVersionsCompletedAtUtc` checkpoint are committed in one serializable
   database transaction.
4. A retry that sees the checkpoint returns an empty displaced set without
   querying or mutating source or destination history. This preserves versions
   written to the destination between the original attempt and its retry.
5. Regular Web/in-process renames do not carry a Samba event ID and retain
   their existing semantics.
6. Migration `20260728135149_SambaRenameVersionCheckpoint` adds the nullable
   receipt checkpoint.

**Validation completed**

- A database regression covers a replace-style rename whose source has no
  history: the first attempt correctly removes displaced destination history,
  a later destination version is then created, and retrying the same event
  preserves that version.
- A gRPC regression proves that the parsed stable event ID reaches the external
  rename hook.
- Focused lifecycle, event, FileService, and versioning tests pass.
- Complete managed suite: 557 passed, 0 failed, 0 skipped.

**Validation still required**

- Stop the bridge after the version transaction commits but before the overall
  event receipt completes, then verify after restart that the spool retry
  completes the remaining effects without changing destination history.
- Exercise two bridge replicas near lease expiry to validate serializable
  conflict/retry behavior against PostgreSQL under live load.

**Next planned finding:** P1-14 — determine the renamed object's type before the
native rename and report directory lifecycle events correctly.

### 2026-07-28 — P1-14: Correct directory rename lifecycle type

**Status:** Implemented, regression-tested, and built against pinned Samba
4.19.5/ABI 49. Live SMB/search verification remains pending.

**Problem**

Directory rename events must select the directory search lifecycle callback.
The audit still described the VFS rename event as hardcoded to file even though
P0-04 had already begun deriving the type from the source stat result.

**Solution implemented**

1. Retained the fail-closed `FSTATAT` of the source before authorization and
   native mutation.
2. Centralized the source-mode to lifecycle-directory flag mapping in
   `rename_event.h`; neither a missing nor a displaced destination influences
   the event type.
3. Kept the derived flag in the durable local rename payload, which `authd`
   forwards unchanged to `NotifyRename`.
4. Added native coverage for directory, regular-file, and symlink modes.
5. Added managed coverage proving that both object types cross the gRPC boundary
   and that a directory rename invokes only the directory search callback.

**Validation still required**

- Rename a populated directory over SMB and verify descendants in the live
  search index move to the new prefix without a full re-index.

**Validation completed**

- The Docker `build-runtime` target compiled and linked `kaimo_bridge.so`
  against pinned Samba 4.19.5.
- The native directory/regular-file/symlink lifecycle type regression passed.
- Focused lifecycle tests passed: 47 passed, 0 failed, 0 skipped.
- Complete managed suite passed: 559 passed, 0 failed, 0 skipped.

**Next planned finding:** P1-15 — remove the predictable temporary file used for
NT-hash import.
