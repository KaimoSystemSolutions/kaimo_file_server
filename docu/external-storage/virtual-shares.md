# Virtual Shares (Cloud Access)

A virtual share exposes a folder behind a storage connection (OneDrive, Dropbox, Google Drive, SMB,
SFTP, WebDAV) as a share in the web file browser. Content stays at the provider and is accessed
lazily on demand; nothing is copied to local storage. Virtual shares are a separate backend from
local shares and are available in the Web host only.

Related: [Connections and credentials](connections-and-credentials.md) · [Providers](providers.md) ·
[Downloads and share links](../external-access/downloads-and-share-links.md)

## Model

| Entity | Table | Content |
|---|---|---|
| `CloudAccessShare` | `cloud_access_shares` | `ConnectionId`, owning `DepartmentId`, `Name`, `RemoteRootPath` and stable `RemoteRootItemId`, `IsEnabled` |
| `CloudAccessGrant` | `cloud_access_grants` | `ShareId`, `PrincipalId` (user or group), `Permission` (`Read` or `Write`) |

Defined in `src/Kaimo_File_Server.Core/Domain/CloudAccessShare.cs`.

- Share names are valid Samba share names and unique case-insensitively across local **and** virtual
  shares.
- Changing the remote root re-resolves the provider item and keeps the share ID and its grants.
- Deleting a virtual share never deletes remote data.
- A connection referenced by a virtual share cannot be deleted (restrictive foreign key).

## Differences from local shares

| Aspect | Local share | Virtual share |
|---|---|---|
| Protocols | Web, REST, WebDAV, SMB | Web file browser only |
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

Out of scope in this phase: the mount is invisible over REST, WebDAV and SMB, and every recursive
host-side operation (copy or ZIP of a parent folder, folder sizes, backup, indexing, sync delta,
share pool move) skips it, because the remote content is not part of the host share.
