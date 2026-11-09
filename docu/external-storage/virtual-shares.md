# Virtual Shares (Cloud Access)

A virtual share exposes a folder behind a storage connection (OneDrive, Dropbox, Google Drive, SMB,
SFTP, WebDAV) as a share in the web file browser, the REST API and WebDAV
([rules](#virtual-shares-and-mounted-folders-over-rest-and-webdav)). Content stays at the provider
and is accessed lazily on demand; nothing is copied to local storage. Virtual shares are a separate
backend from local shares and are served by the Web host only.

Related: [Connections and credentials](connections-and-credentials.md) · [Providers](providers.md) ·
[Downloads and share links](../external-access/downloads-and-share-links.md)

## Model

| Entity | Table | Content |
|---|---|---|
| `CloudAccessShare` | `cloud_access_shares` | `ConnectionId`, owning `DepartmentId`, `Name`, `RemoteRootPath` and stable `RemoteRootItemId`, `IsEnabled` |
| `CloudAccessGrant` | `cloud_access_grants` | `ShareId`, `PrincipalId` (user or group), `Permission` (`Read` or `Write`) |

Defined in `src/Kaimo_File_Server.Core/Domain/CloudAccessShare.cs`.

- Share names are valid Samba share names. Creating or renaming a virtual share checks them
  case-insensitively against local and virtual shares. Creating a local share does not check virtual
  shares; on WebDAV, which addresses shares by name, the local share then wins and the virtual share
  is not listed. REST addresses shares by ID and is not affected.
- Changing the remote root re-resolves the provider item and keeps the share ID and its grants.
- Deleting a virtual share never deletes remote data.
- A connection referenced by a virtual share cannot be deleted (restrictive foreign key).

## Differences from local shares

| Aspect | Local share | Virtual share |
|---|---|---|
| Protocols | Web, REST, WebDAV, SMB | Web, REST, WebDAV |
| Access control | Per-item ACLs (`AccessEntry`) | Whole-share grants, default deny |
| Versioning, recycle bin, search index, change log, snapshots | Yes | No |
| Effective access | Kaimo ACLs | Kaimo grant **and** the provider account's own permissions |

## Authorization

`CloudAccessAuthorizationService` (`src/Kaimo_File_Server.Web/Services/CloudAccessAuthorizationService.cs`)
returns the actor's highest access level on a share: holders of `ManageCloudAccess` for the share's
department implicitly have `Write`; everyone else gets the highest level among their direct and
group grants, or no access. The check runs on **every** list, read, write and download operation,
not only when listing. A provider whose connection lacks write capability is clamped to read at the
capability layer.

## Data path

- **Listing** goes through `IRemoteFileStore` (or the cloud connection) one directory level at a
  time; remote paths are normalized and constrained below the configured root.
- **Downloads** use single-use, five-minute tickets on `GET /api/cloud-access/download`
  (`CloudAccessDownloadTicketStore`); tickets carry no provider credentials.
- **Uploads and mutations** stream to the provider; provider-side rename/move is used where
  available.

### Metadata cache

`CloudAccessDirectoryCache` (`src/Kaimo_File_Server.Web/Services/CloudAccessDirectoryCache.cs`) is a
process-local cache of directory listings only — never file content, tokens or authorization
decisions:

- TTL from the Cloud Access settings (default 20 s, range 1–300 s).
- Key: virtual share ID plus normalized directory path; concurrent misses for the same key are
  coalesced; returned objects are cloned.
- Any mutation invalidates all entries of the affected share in constant time (generation counter).

### Cross-share transfers

`CrossShareTransferService` (`src/Kaimo_File_Server.Web/Services/CrossShareTransferService.cs`) copies
items between local and virtual shares, and `CloudToLocalTransferService` copies remote content into
local shares:

- Content is streamed through a bounded pipe and never fully buffered in memory.
- The destination write goes through the destination backend's normal authorization (local writes
  through `IFileService` with ACL checks, versioning and change log).
- **Cut** is accepted only for a local source: the source is deleted only after every destination
  write has completed successfully; on failure the source is retained.

## Provisioning as a folder in a local share

A virtual share is provisioned either standalone (`CloudAccessProvisioning.VirtualShare`, the
default: listed in the share overview, browsed under `/files/access/{id}`) or as a folder inside a
local share (`CloudAccessProvisioning.Folder`). The mode and the mount point (`MountShareId`,
`MountPath`, at most one per virtual share) live on `cloud_access_shares`; nothing is created on
disk. Admins set them on the **Provisioning** tab of the virtual share
(`CloudAccessMountTab` → `CloudAccessMountService`), which requires `ManageCloudAccess` for the
virtual share and `EditShareSettings` on the host share.

| Rule | Implementation |
|---|---|
| Mount only on a free name, never at the share root, below another mount, in the home-folder share or on a reserved name (`.kaimo-*`, `.RECYCLE_BIN`) | `CloudAccessMountService.MountAsync`: validation, insert, disk re-check, rollback on a concurrent create |
| Name reserved against every user write afterwards (create, upload, rename/move target, unzip/archive target) on Web, REST, WebDAV and SMB | `ICloudAccessMountGuard` (`CloudAccessMountGuard`) checked in `FileService` (throws `MountPathReservedException`, a 403 like an ACL deny) and in the SMB bridge (`AuthzGrpcService` create and rename-destination checks) |
| Rename/move of a parent folder moves the mount point | `FileService` rename side effects → `CloudAccessMountGuard.RenamePathAsync` (also for SMB lifecycle events) |
| Deleting a parent (recycle bin, permanent delete, cross-share cut) detaches the mount | Mount columns cleared, mode stays `Folder` (orphaned, reachable nowhere), warning log `CloudAccessMountDetached` (2401); a restore does not bring the mount back |
| Host share deleted | Foreign key `ON DELETE SET NULL` orphans the mount |
| Effective rights inside the folder | Intersection: `ListReadData` of the host-share ACL at the mount path **and** the virtual-share grant; `Write` additionally needs host `CreateWriteData` and `Delete`. Applies to cloud-access managers too (`CloudAccessAuthorizationService.ClampToHostAsync`) |
| Users without access | The folder is not shown at all; its name is still reserved |
| Concurrent editors | `UpsertShareAsync` never writes the mount columns; only `SetMountAsync` and the guard do |

In the web file browser the mounted folder keeps the host route
(`/files/{hostShare}/{mountPath}/{sub}`): `CloudAccessMountResolver` (per circuit) resolves the
visible mounts of a share once on entry and after every local listing, `LocalFileBrowserPage`
renders the remote browser below a mount with the host breadcrumbs (`FileBrowser.ParentCrumbs`),
and `/files/access/{id}` redirects there for users who can see the mount. In the host listing the
mount is a synthetic, open-only folder entry (`IFileBrowserViewModel.IsMountEntry`). Global search
keeps its rules: the host share is searched through the index (which never contains remote content),
and inside the mounted folder the name walk of virtual shares is used.

### Virtual shares and mounted folders over REST and WebDAV

REST and WebDAV run in the Web host, next to the credential vault. They show a mounted folder as a
normal folder of the host share, and a standalone virtual share next to the local shares
(`GET api/v1/browse/shares` with `isVirtual`, `/dav/<name>`). `MountAwareFileServiceRouter` (scoped to
one request) resolves the visible mounts of the host share once and hands the endpoint either the
normal `IFileService` or a `RemoteMountFileService`, an adapter over the provider's `IRemoteFileStore`.
Endpoints, `WebDavCopy` and the error mapping stay the same. Paths stay host-share-relative
(`Team/Cloud/a.txt`): the adapter strips the mount path on the way in and puts it back on the way out.
A standalone virtual share is the same adapter with an empty mount path; for the endpoints the router
presents it as a `ShareDefinition` that exists only for the request and is never stored. Listings go
through the `CloudAccessDirectoryCache` shared with the web browser, and every change invalidates the
share.

Existing grants apply on every transport; there is no per-share switch for REST or WebDAV. The per-user
service switches (app, WebDAV) still apply. The table below is written for mounts; a standalone virtual
share follows the same rules, with these differences:

| Topic | Standalone virtual share |
|---|---|
| Visibility | Listed if the user holds a grant or manages the share and its connection is `Ready`. Without access, or for an ID or name of a mounted share, the share answers as unknown (`404`). A mounted or orphaned share is reachable only through its host, so the host ACL cannot be bypassed |
| Rights | The grant alone (or management of the share), intersected with the provider capabilities |
| Share root | Cannot be deleted or renamed (`403` on WebDAV, `400 invalid_path` on REST, which needs a path) |
| Move | Into the share: copy, then delete the source. Within the share: rename. Out of it, also into another virtual share: refused, as for a mount |
| Connection not `Ready` | Not listed; a direct request answers `503` |
| Sync | Not supported. All sync endpoints answer `404` for its ID; the app does not offer it |
| Search | With its `shareId`: name walk at the provider. Without `shareId` it is not searched, because the index never contains remote content |

| Topic | Rule |
|---|---|
| Visibility | A mount the user may not open never matches, so the path behaves as if nothing were mounted: `404` when reading, `403` when writing (name reservation). The parent listing has no entry for it, so its existence does not leak |
| Rights | `Permission` of the mount (host ACL and grant combined) intersected with the session's provider capabilities; a missing right is a `403` |
| Mount point | Synthetic folder (epoch timestamps); cannot be written, deleted or renamed (`403`) |
| Delete | Always **permanent** and recursive, on WebDAV without any confirmation. The host share's recycle bin does not apply |
| Copy | Both ways across the mount boundary, through `WebDavCopy` |
| Move | Local → mount: copy, then delete the source. Mount → anywhere else: REST `403 move_not_supported`, WebDAV `403`, checked before anything changes, because the source could not be restored |
| Overwrite | The old target stays until the new content has replaced it. WebDAV `Overwrite: T` into a mount therefore merges folders and answers `409` on a file/folder clash, instead of deleting the target first |
| Uploads | A body still arriving from a client is spooled to a temp file first, so an aborted upload leaves the remote file untouched |
| Range requests | Only when the provider's stream is seekable (SFTP, SMB, cloud temp file). A provider stream that cannot seek (WebDAV) answers `200` with the whole file. SMB and cloud providers fetch the whole file for every ranged request; WebDAV `HEAD` answers from the metadata and fetches nothing |
| Preconditions | A request that changes something reloads the listings it consults, so `If-Match`, `If-None-Match` and existence checks see changes made at the provider. Reads stay on the cache |
| Timestamps | Best effort: a provider that cannot set them keeps its own time, without an error. An overwrite follows the upload contract: Modified only moves forward, Created only back. A copy leaves an unknown source time (epoch) to the target |
| Versions, snapshots | Empty lists; `version-content` answers `404` |
| Sync | Not supported. `delta` and `changes` walk the local tree and never see a mount; a profile rooted in a mount is refused with `409 sync_root_in_mount` |
| Search | With `shareId` and a `path` in a mount: name walk at the provider (same limits as in the web browser, empty snippets). Everywhere else the index, which never contains remote content |
| Connection or provider down | REST `503 remote_unavailable` with `Retry-After`, WebDAV `503`. A missing path is told apart from an outage through the parent listing, because providers report both differently |

Each request opens its own provider session on first use and closes it at the end of the request.

Out of scope: virtual shares and mounts are invisible over SMB, and every recursive host-side operation (copy or ZIP
of a parent folder, folder sizes, backup, indexing, sync delta, share pool move) skips it, because
the remote content is not part of the host share. A restic restore into the host share never
writes to a mount path, at any depth of the restored tree: such entries are reported as skipped.
