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
| Delete a home (`DeleteHomeAsync`) | Removes public links into it first, then the folder, metadata and ACL rows, file versions and the root entry; appends a `Deleted` change-log entry. The recycle bin is not used. An existing, enabled user immediately receives a new empty home |

## Exposure per transport

| Transport | Behavior |
|---|---|
| REST API | `GET /api/v1/browse/shares` lists the caller's home first with `rootPath = <userId>` |
| WebDAV | `users` appears in the root listing; inside, only the caller's own folder is visible |
| SMB | The bridge's `ListShares` marks `users` as `restricted` with the usernames of all enabled homes; `sync-shares.sh` resolves them to SIDs and writes the share security descriptor (`sharesec`), and `access based share enum` hides the share from everyone else |

## Safeguards

- The sync API refuses the share root `""` of `users` (delta, changes, wait, sync profiles), because
  its change log would expose names from other homes; clients sync `<userId>`.
- The recycle bin is disabled for the share, since `.RECYCLE_BIN` would be shared by all users.
- ACL editing is refused for the share; server-side syncs are not offered inside homes.
- Public links into a home require the regular `ManageShareLinks` / `ManageUploadLinks` permissions.
