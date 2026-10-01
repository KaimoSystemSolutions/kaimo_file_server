# Virtual Shares (Cloud Access)

A virtual share exposes a folder behind a storage connection (OneDrive, Dropbox, Google Drive, SMB,
SFTP, WebDAV) as a share in the web file browser. Content stays at the provider and is accessed
lazily on demand; nothing is copied to local storage. Virtual shares are a separate backend from
local shares and are available in the Web host only.

Related: [Connections and credentials](connections-and-credentials.md) · [Providers](providers.md) ·
[Downloads and share links](../interfaces/downloads-and-share-links.md)

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
