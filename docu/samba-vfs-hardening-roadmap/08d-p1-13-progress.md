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

### 2026-07-28 — P1-15: Private temporary NT-hash import

**Status:** Implemented, regression-tested, and verified in the pinned Samba
4.19.5 build and slim runtime image. Live startup/login verification remains
pending.

**Problem**

`sync-users.sh` placed reusable NT hashes in the predictable shared path
`/tmp/kaimo.smbpasswd`. It neither restricted the process umask nor verified
safe directory ownership, serialized concurrent imports, or deleted the file
after successful and failed imports.

**Solution implemented**

1. The sync now uses `/run/kaimo-user-sync` by default and creates it with mode
   0700. Before exporting a hash it rejects a symlink, a non-directory, a
   foreign owner, or any mode other than 0700.
2. `umask 077` applies before a per-run `mktemp` creates the smbpasswd import
   and diagnostic files. The reusable hashes therefore exist only in an
   unpredictable mode-0600 file inside the private directory.
3. Exit and signal traps delete the import and both diagnostic files after all
   success and failure paths.
4. A nonblocking `flock` in the private directory prevents startup and periodic
   synchronization runs from importing concurrently.
5. `KAIMO_SYNC_RUNTIME_DIR` permits an isolated private directory in tests. A
   separate path-prefix override lets the regression select stubbed Samba
   tools without changing the production default.
6. The shell regression is now a required `build-runtime` Docker layer rather
   than an unexecuted standalone test.
7. The slim runtime explicitly includes and verifies `flock`, `mktemp`, and
   `stat`, so the hardening does not depend on accidental base-image contents.

**Validation completed**

- The standalone regression passed in the Samba debug container:
  three distinct users imported, the import path was random and mode 0600,
  successful and failed imports left no temporary files, and a held lock
  rejected a second export.
- The Docker `build-runtime` target passed the same regression and completed
  against pinned Samba 4.19.5/ABI 49.
- The final slim deployable image built successfully and verified all required
  runtime tools.
- `git diff --check` passed.

**Validation still required**

- Start the deployable Samba container against the bridge, perform an initial
  and periodic user sync, and verify a real NTLMv2 login while confirming no
  per-run files remain in `/run/kaimo-user-sync`.

**Next planned finding:** P1-16 — reconcile `tdbsam` and managed POSIX users
when a Kaimo user is disabled, deleted, or absent from the bridge response.

### 2026-07-28 — P1-16: Desired-state Samba user reconciliation

**Status:** Implemented, regression-tested, and verified with real Samba
4.19.5 `pdbedit` and NTLM login rejection. Bridge-driven live disable/delete
and already-open-session behavior remain release validation.

**Problem**

`ListUsers` correctly omitted disabled, deleted, and passwordless users, but
`sync-users.sh` only imported the returned users. Their old `tdbsam` credentials
and broad storage/authd group membership therefore survived indefinitely.

**Solution implemented**

1. A private mode-0700 state directory and mode-0600 `managed-users` file define
   exactly which local accounts the Kaimo sync owns.
2. On migration, an absent state file bootstraps from current `tdbsam` entries
   while excluding the comma-separated reserved-user setting. Subsequent runs
   touch only the published managed set, so later unrelated accounts are not
   silently adopted.
3. After a successful import, every previously managed user absent from the
   bridge response is removed with `pdbedit -x -u` and removed from the storage
   and private authd-socket groups.
4. The POSIX account is retained, locked, and forced to `nologin`. Keeping its
   UID preserves existing file ownership, prevents UID reuse, and allows later
   reactivation without identity drift.
5. Reactivated users regain their passdb entry and both secondary groups while
   retaining the original UID.
6. Import, POSIX provisioning, group mutation, passdb removal, and state
   publication now fail the user sync. The managed boundary is atomically
   replaced only after reconciliation, so a partial failure remains retryable.
7. Existing authenticated SMB sessions are deliberately not killed here. The
   later revocation-SLA item must decide and test forced per-user session
   termination without conflating it with local credential convergence.

**Validation completed**

- The required container-build regression verifies stale-user removal, reserved
  account exclusion, locked UID retention, group removal, reactivation with the
  same UID, first-run passdb bootstrap, private state modes, failure retry
  state, cleanup, and concurrent-run rejection.
- The Docker `build-runtime` target completed against pinned Samba 4.19.5/ABI
  49 with the regression passing.
- A disposable pinned-Samba probe exercised the real `pdbedit -L`, import, and
  `pdbedit -x -u` paths and confirmed the retained POSIX identity and removed
  groups.
- A live `smbd`/`smbclient` probe proved the stale password authenticated before
  reconciliation and returned login failure afterward.

**Validation still required**

- Disable and delete a real bridge-backed Kaimo user, measure convergence at the
  periodic-sync boundary, and verify the managed state and container logs.
- Decide the user revocation SLA for already-open SMB sessions and add the
  corresponding forced-disconnect test under the later revocation finding.

**Next planned finding:** P1-17 — make every user/share/config synchronization
fail when a requested mutation was not applied, verify final state, and expose
the last successful convergence.
