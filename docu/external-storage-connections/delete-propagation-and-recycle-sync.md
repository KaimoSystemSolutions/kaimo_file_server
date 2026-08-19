# Delete propagation & recycle-aware two-way sync

## Problem

Two-way sync reconciles a local share folder against a remote endpoint by comparing
the *current* state of both sides. Historically, an item that existed on only one
side was **always copied to the other side**. Because the engine had no memory of
the previous run, it could not tell "newly created here" apart from "deleted there",
so **a file deleted on one endpoint was simply restored from the other** on the next
run. Deletions never converged.

## What changed

Two opt-in behaviors were added, both scoped to **two-way** syncs (push and pull are
unaffected — they have no such ambiguity):

1. **Delete propagation.** When enabled, a file or folder removed on one endpoint is
   removed on the other endpoint too, so both sides converge on the same state.
2. **Recycle-bin awareness.** When the local share has its recycle bin enabled
   (`ShareDefinition.IsRecycleEnabled`), a deletion the sync applies *locally* moves
   the item into the share's `.RECYCLE_BIN` instead of hard-deleting it, so it stays
   recoverable. This is automatic — there is no separate switch. Remote endpoints
   have no Kaimo recycle bin, so a remote deletion is a plain remote delete.

Delete propagation is configured per sync mapping via the **"Synchronize deletions"**
checkbox on the *Advanced* tab of both the External Storage admin and the legacy
Cloud Sync editor. It maps to `CloudSyncAdvancedSettings.SyncDeletions`.

## How deletion is detected

Telling a new item apart from a deleted one requires a memory of the last converged
state. After **every** successful two-way run — whether or not delete propagation is
enabled — the engine records a **manifest**, the set of share-relative paths that
existed after the run, into `SyncDefinitionRuntime.LastSyncManifest` (column
`last_sync_manifest` on `sync_definition_runtimes`, added by the `AddSyncManifest`
migration). Maintaining it unconditionally means that enabling the option on a
folder that has already been syncing takes effect on the **next** run, rather than
wasting one run to establish a baseline. The manifest is only *consulted* (to
delete) when the option is on.

On the next run, for an item present on only one side:

| In previous manifest? | Meaning | Action |
|-----------------------|---------|--------|
| Yes | It existed last time and is now gone on one side → **deleted** | Propagate the delete to the other side |
| No  | It was not here last time → **newly created** | Copy it across (unchanged behavior) |

The manifest and this comparison are only produced/consumed while
`SyncDeletions` is enabled; otherwise `SyncAsync` returns `null` and the runtime
column is left untouched.

### Safety baseline — no mass-delete without a baseline

A run with **no stored manifest** (a folder that has never completed a two-way sync)
treats every one-sided item as "not in manifest → newly created → copy", so it can
only ever copy — never mass-delete. A baseline is recorded on that run, and deletion
propagation is active from the next run onward. Because the manifest is maintained on
every two-way run regardless of the option, a folder that was already syncing before
you enable the option already has a baseline, so enabling it takes effect on the very
next run.

## Excluded namespaces

The reconciliation skips any child whose share-relative path is not a *regular* entry
per `ShareEntryPolicy` — the recycle bin (`.RECYCLE_BIN`) and internal `.kaimo-*`
namespaces. This keeps a locally recycled file from being re-uploaded to the remote,
which would otherwise defeat delete propagation.

## Provider support

Remote deletion uses `ICloudConnection.DeleteAsync(path, isDirectory, ct)`:

- **Protocol / mounted / host-mount providers** (SFTP, SMB, etc.) delegate to
  `IRemoteFileStore.DeleteAsync` via `RemoteFileStoreSyncAdapter`.
- **OneDrive** delegates to its Graph item delete (recursive for folders).
- **Google Drive** resolves the item id and issues `Files.Delete`.

A provider that cannot delete throws `NotSupportedException` (the interface default),
failing the run loudly rather than silently resurrecting the item.

## Touch points

- `CloudSyncAdvancedSettings.SyncDeletions` — the persisted flag (JSON column, no
  migration).
- `ICloudConnection.SyncAsync` / `SyncDirectory` — manifest-driven reconciliation and
  the new delete branches (`.../Services/DataServices/ICloudConnection.cs`).
- `CloudSyncTransferOptions` — carries `SyncDeletions` and `HonorRecycleBin`.
- `SyncManifest` — the manifest model (serialize/deserialize).
- `CloudSyncExecutionService` — loads the previous manifest, passes the recycle flag,
  and saves the new manifest after a successful run.
- `ISyncDefinitionRepository.GetManifestAsync` / `SaveManifestAsync` — persistence.
