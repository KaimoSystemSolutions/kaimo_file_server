# Package 5: First-Class Sync Definitions

Package 5 introduces `SyncDefinition` as the durable sync configuration and
`SyncDefinitionRuntime` as its narrow mutable run state. A sync references a
provider-neutral `StorageConnection`; the provider grant is protected with the
context-bound credential vault and is no longer read from share JSON by the
execution engine.

## Additive rollout

The `FirstClassSyncDefinitions` migration creates two new tables and does not
rewrite or remove `ShareDefinition.CloudSettings`. The compatibility importer
runs before scheduled and manual executions. For each legacy share/path mapping
it atomically creates:

1. one `StorageConnection` in the local share's department;
2. one encrypted authorization-grant payload bound to that connection and provider;
3. one `SyncDefinition` containing paths, mode, schedule, filters, and bandwidth limits;
4. one `SyncDefinitionRuntime` initialized from the legacy last-success timestamp.

The unique `(LocalShareId, LocalPath)` index and source checksum make the import
restart-safe. Re-running it converges on the existing rows. A removed legacy
mapping disables its imported definition but retains the connection for audit
and recovery. Connection deletion is blocked while a sync definition references
it, both in the repository and by a restrictive database foreign key.

## Runtime cutover

The scheduler now enumerates enabled first-class definitions and resolves its
execution actor through `RunAsUserId`. Manual execution resolves the definition,
loads and decrypts its connection grant, and projects the data into the existing
provider adapter. Successful runs update only `SyncDefinitionRuntime`. Rotated
provider grants are protected and written to `StorageConnection` before the live
provider acknowledges them. Failed runs store only an allow-listed provider error
code or the generic `sync_failed` category; exception text is never copied into
the runtime row.

During this rollout, successful runs also update the legacy timestamp and any
rotated grant as a compatibility fallback. Legacy JSON is intentionally retained
until a later package has verified the cutover in production and moves the editor
to the unified external-storage administration area. Removing that fallback in
this package would make rollback unsafe.

No user-facing text is introduced by this package, so no English or German
resource entries are required. All code comments, schema documentation, and
operational notes remain in English.
