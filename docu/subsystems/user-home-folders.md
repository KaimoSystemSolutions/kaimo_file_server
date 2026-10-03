# User Home Folders

Every user can have a private home folder. All homes live in one system share named **`users`**;
each home is the folder `users/<userId>`. Privacy is expressed entirely through ordinary ACL entries,
so every transport (web, REST, WebDAV, SMB) enforces it without special code paths.

Related: [Security model](../architecture/security-model.md) ·
[Samba VFS integration](../external-access/smb/samba-vfs-integration.md) · [REST API v1](../external-access/rest-api.md)

## Share

- The share is a regular `ShareDefinition` with `IsUserHomes = true`, created in a chosen pool as
  `<pool>/users`. An existing `<pool>/users` directory is adopted, so homes re-attach to their users
  after a database reset.
- The name `users` is used because `[homes]` is a reserved Samba section with special semantics.
- The names `users` and `user` are reserved and rejected for other shares.
- Provisioning is idempotent (`HomeDirectoryService`,
  `src/Kaimo_File_Server.Infrastructure/Services/HomeDirectoryService.cs`): for all users after setup,
  when a user is created, on every application start, and lazily when a user first accesses their
  home.

## Access model

Per user with an enabled home:

| Path | Entry | Inheritance |
|---|---|---|
| Share root `""` | Allow `ReadAll` | This folder only |
| `<userId>` | Allow `ReadAll | WriteAll` **without** `Delete` | This folder only |
| Below `<userId>` | Allow `ReadAll | WriteAll` | Sub-folders, sub-files, all descendants |

- The root entry is required because an SMB tree connect checks `ListReadData` on the share root and
  traversal checks `TraverseExecute` on every ancestor. Listings are ACL-filtered, so each user sees
  only their own folder.
- Users get no `Delete` on the home folder itself (it cannot be removed or renamed) and no `AdminAll`
  (they cannot grant access to others).
- Administrators receive no entry.
- Department defaults never apply: `AclService.ResolveDepartmentDefaultAsync` returns `None` for a
  share with `IsUserHomes`.
- Managing homes requires the global permission `ManageHomes`.

## Lifecycle

| Operation | Effect |
|---|---|
| Disable a home | Removes the user's three entries and sets `HomeDirectoryEnabled = false`; files stay on disk |
| Re-enable | Restores the entries |
| Global switch off | Disables the `users` share (`HomeDirectoryService.SetGloballyEnabledAsync`); every transport refuses a disabled share. Per-user state is kept |
| Delete a home (`DeleteHomeAsync`) | Removes public links into it first, then the folder (including its recycle bin), metadata and ACL rows, file versions and the root entry; appends a `Deleted` change-log entry. The recycle bin is not used. An existing, enabled user immediately receives a new empty home |

## Recycle bin

The recycle bin is always enabled for the `users` share. `HomeDirectoryService` creates the share with
it and `BackfillAsync` re-enables it on every start; share management never shows the share, so it
cannot be switched off.

Every home has its own bin at `users/<userId>/.RECYCLE_BIN`, so it needs no mount or special path on
any transport: it is an ordinary folder inside the home. Deleting `<userId>/docs/a.txt` moves it to
`<userId>/.RECYCLE_BIN/docs/a.txt`.

- `ShareDefinition.RecycleRootDepth` is `1` for this share (`0` everywhere else).
  `ShareEntryPolicy` applies its rules below that depth: `<userId>/.RECYCLE_BIN` is the recycle bin
  and `<userId>/.kaimo-*` is internal. The share-root rules still apply as well.
- `IFileServiceFactory.CreateForShare(ShareDefinition)` passes the depth to `FileService`.
  `AuthorizeDelete` returns it to the Samba VFS as `recycle_root_depth`.
- The bin lies inside the home, so it inherits the owner's home entries. The owner can browse it,
  restore items (move them out) and empty it. Other users and administrators have no access.
- Users cannot create or move items into the bin directly (reserved namespace). Deleting an entry
  inside it is permanent.
- On SMB and on macOS, the dot name makes the folder hidden by default. The web UI shows it with
  the recycle-bin icon and the "empty recycle bin" action.
- Recycled items count toward the size of the home.
- The sync API never syncs a recycle bin: `delta` skips it, and the change feed reports a recycle
  move as a delete of the old path and a restore as a subtree change at the new path.

## Exposure per transport

| Transport | Behavior |
|---|---|
| REST API | `GET /api/v1/browse/shares` lists the caller's home first with `rootPath = <userId>` |
| WebDAV | `users` appears in the root listing; inside, only the caller's own folder is visible |
| SMB | The bridge's `ListShares` marks `users` as `restricted` with the usernames of all enabled homes; `sync-shares.sh` resolves them to SIDs and writes the share security descriptor (`sharesec`), and `access based share enum` hides the share from everyone else |

## Safeguards

- The sync API refuses the share root `""` of `users` (delta, changes, wait, sync profiles), because
  its change log would expose names from other homes; clients sync `<userId>`.
- There is no share-root recycle bin, since it would be shared by all users; each home has its own
  (see [Recycle bin](#recycle-bin)).
- A `users` (or `user`) folder in a storage pool is never listed under unreferenced shares and can
  never be purged there, even if no share references it. Such a folder holds orphaned homes, which
  the home-folder setup re-adopts.
- ACL editing is refused for the share; server-side syncs are not offered inside homes.
- Public links into a home require the regular `ManageShareLinks` / `ManageUploadLinks` permissions.
