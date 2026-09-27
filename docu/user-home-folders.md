# User home folders

Every user gets a private folder that only they can access. All homes live in one system
share named **`users`**; each home is the folder `users/<userId>`. The backend is identical on
every access path; "user" is only the label the web UI and app show.

| Access path | What a user sees | What an administrator sees |
|---|---|---|
| Web UI | Pinned entry **"user"** at the top of the share overview (`/files/user`). URL, breadcrumb and title never show `users/<id>`. | Their own "user" entry, plus **"users"** (`/files/users`): a metadata-only overview. |
| REST / app | First entry of `GET /api/v1/browse/shares`, named "user", with `rootPath = <userId>` | Same as a user |
| WebDAV | `users` in the root listing; inside it only their own `<userId>` | Only their own home |
| SMB | Share `users`, listed only for users with an enabled home; inside it only their own `<userId>` | Only their own home |

The name `users` is used instead of `homes` because `[homes]` is a reserved Samba section
with special semantics that would break the share-name–based authorization bridge.

## Setup

*Settings → Storage → User home folders*: choose a storage pool and click **Set up home
folders**. This creates `<pool>/users` and the share row (`share_definitions.IsUserHomes =
true`). An existing `<pool>/users` directory is adopted, so homes re-attach to their users
after a database reset.

The pool cannot be changed once set up. Moving homes to another pool is not supported yet.

Homes are provisioned automatically:

- for all users, right after setup;
- when a user is created in the web UI;
- on every application start (idempotent backfill);
- lazily when a user opens `/files/user` or calls the REST share list.

## Access model

All access is expressed as ordinary ACL entries. Every transport (web, REST, WebDAV, SMB)
therefore enforces it without special code.

| Path | Entry (per user with an enabled home) | Inheritance |
|---|---|---|
| share root `""` | Allow `ReadAll` | this folder only |
| `<userId>` | Allow `ReadAll | WriteAll` without `Delete` | this folder only |
| below `<userId>` | Allow `ReadAll | WriteAll` | sub-folders, sub-files, all descendants |

The root entry is required because SMB tree-connect checks `ListReadData` on the share root
and traversal checks `TraverseExecute` on every ancestor. Listings are ACL-filtered, so each
user only ever sees their own folder in the root.

Several permissions are deliberately left out:

- **`Delete` on the home folder itself.** A user cannot remove their home over WebDAV or SMB. Renaming it is already impossible because nobody has create rights in the root.
- **`AdminAll` (ChangePermissions / TakeOwnership).** A user cannot hand out access to their home.
- **Administrators.** They receive no entry at all.
- **Department defaults.** They never apply to this share. `AclService.ResolveDepartmentDefaultAsync` returns `None` for `IsUserHomes`, otherwise every department member would reach every home.

### Global switch

*Settings → Storage → Home folders enabled* turns home folders off for every user at once by
disabling the `users` share (`HomeDirectoryService.SetGloballyEnabledAsync`). Every access path
already refuses a disabled share: the web UI, REST, sync, WebDAV, and SMB, which drops it from
the registry. While the switch is off:

- the per-user toggle disappears from the user edit form and from `/files/users`;
- the overview shows every home as disabled.

Per-user settings and files are kept, so switching back on restores the previous state.

### Disabling a home

Disabling a home removes the user's three entries and sets `users.HomeDirectoryEnabled =
false`. The files stay on disk.

- The web UI and the app no longer show "user".
- SMB refuses the tree-connect to `users`.
- WebDAV answers `/dav/users` with 403.

Re-enabling restores the entries. A home can be toggled in two places:

- the user detail panel (Security tab);
- the `/files/users` overview.

### Deleting a home

In `/files/users`, an administrator with `ManageHomes` can delete a home after confirming it
(`HomeDirectoryService.DeleteHomeAsync`). The deletion removes everything tied to the home:

- the public links pointing into it (first, so they stop serving immediately);
- the folder on disk;
- its metadata and ACL rows;
- its file versions;
- the user's entry on the share root.

A `Deleted` change-log entry lets sync clients and the search index drop the content. The
recycle bin is not used.

If the user still exists and their home is enabled, a new, empty home is provisioned right away.
The folder of a deleted account (shown as "orphaned") disappears completely. Deletion works
while home folders are switched off globally.

### Administrators

The management permission **`ManageHomes`** (bit 29, global scope) grants the `/files/users`
overview and the enable/disable toggle. The system role *Administrator* holds it. It is
intentionally not part of the `FullAdmin` preset: `FullAdmin` doubles as the "is global admin"
check, and a new bit there would demote custom roles built from that preset.

The overview shows the following for each home:

- owner;
- size (walked on disk);
- status (active, disabled, or orphaned when the user was deleted);
- the home's public links.

It never lists folder content.

## SMB share visibility

Samba lists shares via `srvsvc` before any VFS hook runs, so the Kaimo ACL cannot hide a share
there. For `users` only, the bridge's `ListShares` marks the share as restricted and sends the
usernames of all enabled users with an enabled home. `sync-shares.sh` resolves them to SIDs
(`pdbedit`) and stores exactly those as the share security descriptor (`sharesec -S`, written
only when it changed). `access based share enum = yes` then hides the share from everyone else,
and smbd also refuses their tree-connect. All other shares keep Samba's default descriptor
(visibility by the hidden flag only, as before).

## Safeguards

- **Share management.** It hides the `users` share, and the names `users` and `user` are rejected for new or renamed shares.
- **ACL editor.** It refuses the `users` share, and the file browser hides ACL management inside homes.
- **Share lists in web and app.** The share overview, the REST share list and the search share picker skip the `users` share. Only SMB and WebDAV list it.
- **Web share route.** `/files/users/...` redirects to `/files/user`. The generic file browser refuses the share outright.
- **Sync API.** Delta, changes, wait and sync profiles refuse the share root `""` of `users`. Every user may list it, but its change log would expose deleted names from other homes. Clients sync `<userId>`.
- **Cloud sync.** It is not offered inside homes.
- **Recycle bin.** It is disabled for the share, because `.RECYCLE_BIN` would be shared by all users.
- **Public links.** Creating a link in a home requires the regular `ManageShareLinks` / `ManageUploadLinks` permission.

## Limits

- Privacy is enforced at application level. Anyone with host or database access, or who can
  reset a user's password, can still reach the data.
- No quotas, no pool move, no sharing of home sub-folders.
- Global-search results inside a home still show the `users` share.
- **SMB share list delay.** `users` appears in or disappears from the SMB share list only after the next share-sync cycle following a home being enabled or disabled. The Kaimo ACL takes effect immediately on every connect.

## Client app

The Flutter app (separate repository) should:

- read the new optional `rootPath` field of `ShareDto`;
- pin that entry at the top;
- prefix `rootPath` to every path it sends and hide it in the breadcrumb;
- use it as the root of a sync profile.
