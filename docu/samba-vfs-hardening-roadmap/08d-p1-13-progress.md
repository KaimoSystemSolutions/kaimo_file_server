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
