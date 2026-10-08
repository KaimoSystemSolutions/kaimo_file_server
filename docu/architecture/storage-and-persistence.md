# Storage and Persistence

This document describes where Kaimo File Server keeps its state: the PostgreSQL database, the
storage pools on disk, version blobs, recycle bins, the snapshot cache and the change log, and how
these stores relate to each other.

Related: [System overview](system-overview.md) · [Domain model](domain-model.md) ·
[Background services](background-services.md) ·
[SMB lifecycle events and snapshots](../external-access/smb/lifecycle-events-and-snapshots.md)

## State at a glance

A single logical file is represented in up to five independently durable stores:

| Store | Location | Content | Authority |
|---|---|---|---|
| File system | `<pool>/<share>/…` | Live file content and the share's `.RECYCLE_BIN` | Authoritative for content |
| PostgreSQL | `kaimo_file_server` database | Metadata, ownership, ACLs, version rows, shares, configuration | Authoritative for permissions and history |
| Version blobs | `<pool>/.kaimo-versions/` | Compressed, content-addressed historical content | Referenced by `file_versions` rows |
| Elasticsearch | index `kaimo-files-v2` | Searchable file names and content (engine "Elasticsearch") | Derived, rebuildable |
| PostgreSQL | table `search_documents` | Searchable file names and content (engine "local indexing") | Derived, rebuildable |
| Snapshot cache | `/data/kaimo-system/.kaimo-snapshots/` | Materialized @GMT "Previous Versions" views for SMB | Derived, rebuildable |

No transaction spans these stores. `FileService` (`src/Kaimo_File_Server.Core/Services/File/FileService.cs`)
performs the file-system operation first and then applies database side effects (ownership, ACL path
updates, version rows, change-log entries) from central helper methods. Derived stores are fed
asynchronously from the change log.

## PostgreSQL

PostgreSQL 17 (Npgsql provider) is the only database. All .NET processes use one EF Core context,
`ApplicationDbContext` (`src/Kaimo_File_Server.Infrastructure/Persistence/ApplicationDbContext.cs`),
obtained through `IDbContextFactory<ApplicationDbContext>`. Migrations live in
`src/Kaimo_File_Server.Infrastructure/Migrations/` and are applied only by the Host.

Tables by area:

| Area | Tables |
|---|---|
| Identity and organization | `users`, `groups`, `user_groups`, `roles`, `departments`, `department_users`, `scoped_role_assignments` |
| Shares and files | `share_definitions`, `file_metadata`, `access_entries`, `file_versions` |
| Authentication and security monitoring | `revoked_web_tokens`, `login_attempts`, `client_activity`, `refresh_tokens` |
| Client sync API | `sync_devices`, `device_sync_profiles`, `client_request_receipts`, `file_change_log` |
| External storage | `provider_profiles`, `storage_connections`, `storage_authorization_transactions`, `storage_device_authorization_sessions`, `storage_connection_credential_leases`, `sync_definitions`, `sync_definition_runtimes`, `cloud_access_shares`, `cloud_access_grants` |
| Sharing | `share_links` |
| SMB integration | `samba_lifecycle_event_receipts` |
| Notifications | `notification_events`, `mail_rules`, `mail_templates`, `mail_deliveries` |
| Configuration | `config_settings` |

`config_settings` is a key/value store shared by all processes. It holds desired state (for example
`services.smb.enabled`, `services.webdav.enabled`), runtime settings and the search indexer cursor.

## Storage pools and shares

- Every directory directly under `Storage:RootPath` (default `/data/storage`) is a **pool**. Each pool
  is a separate bind mount (`/data/storage/pool01`, `/data/storage/pool02`, …).
- A **share** (`ShareDefinition`) is a directory inside a pool. `ShareDefinition.Path` is its absolute
  path; all share I/O resolves through it (`src/Kaimo_File_Server.Infrastructure/Services/FileServiceFactory.cs`).
- A pool can carry a display label, stored in `config_settings` under `storage.poolNames`
  (`src/Kaimo_File_Server.Core/Storage/StoragePoolNaming.cs`). The label is presentation only; I/O
  always uses the mount path.

### Path model

- Inside a share every path is **share-relative**, uses forward slashes and is normalized through
  `ShareRelativePath`. The share root is the empty string `""`.
- `FileSystemStorage.ToAbsolutePath` (`src/Kaimo_File_Server.Infrastructure/Storage/FileSystemStorage.cs`)
  maps a share-relative path to an absolute one through `ShareRelativePath.ToContainedAbsolutePath`,
  which rejects any path that escapes the share root, including sibling-prefix escapes
  (`/data/share` vs. `/data/share2`).
- Moves can land on a different path than requested (a timestamp suffix is appended on a name
  collision in the recycle bin). `IStorageEngine.MoveAsync` returns the actual destination, and all
  metadata and ACL updates use that returned path.

### Reserved namespaces

`ShareEntryPolicy` (`src/Kaimo_File_Server.Core/Helpers/ShareEntryPolicy.cs`) classifies the first path
segment of every share:

| Namespace | Purpose |
|---|---|
| `.RECYCLE_BIN` | Per-share recycle bin |
| `.kaimo-*` | Server-internal data (for example `.kaimo-close-captures` for SMB close captures). Hidden from every client protocol and rejected as a client write target |

## Recycle bin

When `ShareDefinition.IsRecycleEnabled` is set, deleting an item moves it to
`.RECYCLE_BIN/<original path>` instead of removing it. The move is a rename from the perspective of
ACLs, versions and the change log (`FileChangeType.Renamed`). Deleting inside `.RECYCLE_BIN` removes
the item permanently. SMB deletes follow the same rule inside the VFS module
(`src/samba-vfs/module/recycle_move.h`).

## File versions

Implemented by `FileVersionService` (`src/Kaimo_File_Server.Core/Services/File/FileVersionService.cs`):

- **Location.** `PoolVersionStorageLocator` places blobs in `<pool>/.kaimo-versions`, in the same pool
  as the share and outside every share folder. A share that is itself a pool root has no version
  storage.
- **Layout.** Blobs are content-addressed by the SHA-256 of the uncompressed content and stored
  gzip-compressed as `AB/CD/<sha256>.bin.gz`.
- **Deduplication.** If the newest version already has the same hash, no version is created; if a blob
  with that hash exists, it is reused.
- **Isolation.** Each `file_versions` row carries its `ShareId`, so histories of files with the same
  relative path in different shares never mix.
- **Retention.** Defaults are 64 versions and 90 days per file, applied after each new version.
- **Space guard.** A version is skipped (never the file operation itself) when writing the blob would
  leave less than `max(2 GiB, 5 %)` free in the pool.
- **Exclusions.** Files pulled in by an external-storage sync do not create versions.
- **Reconciliation.** `VersionStorageReconcilerService` (Host) moves blobs from the legacy location
  `<appdata>/.versions` into their pools and follows shares that moved to another pool.

## Snapshot cache

The SMB bridge materializes "Previous Versions" (@GMT tokens) into
`/data/kaimo-system/.kaimo-snapshots/<share>/`, outside every share. The cache is rebuildable, bounded
per share and evicted by TTL (`Snapshots__Cache__*` settings). Details:
[SMB lifecycle events and snapshots](../external-access/smb/lifecycle-events-and-snapshots.md).

## Change log

`file_change_log` (`FileChangeLogEntry`, `src/Kaimo_File_Server.Core/Domain/ClientSync/FileChangeLogEntry.cs`)
is an append-only feed with one row per mutation:

- `Seq` is a globally monotonic identity, used as an opaque cursor. Web, bridge and Host append
  concurrently; `FileChangeLogRepository.AppendAsync` takes a transaction-scoped advisory lock
  around the insert, so commit order equals `Seq` order. A reader that has passed `Seq` N can
  therefore never miss a lower `Seq` that commits later.
- When a COMMIT succeeds but its acknowledgement is lost, the retry strategy appends the entry a
  second time under a new `Seq`. This is accepted: create, modify and delete entries are path-based
  and re-apply harmlessly; only a duplicated rename that lands after a later opposite rename can
  leave a stale search hit until the next reindex.
- Each row records `ShareId`, `Path`, `OldPath` (renames), `ChangeType`
  (`Created`, `Modified`, `Deleted`, `Renamed`, `SubtreeChanged`), `IsDirectory` and, when cheaply
  available, size and modification time.
- All transports write it through the `IFileService` mutation paths (`ScopedFileChangeLog`), including
  SMB events relayed by the bridge.
- Writes are best-effort and outside the file operation's transaction. Consumers tolerate a lost row:
  clients reconcile with a full delta, the search index can be rebuilt.

Consumers:

| Consumer | Process | Use |
|---|---|---|
| `SearchIndexingService` | Web | Tails the log from a persisted per-engine cursor and writes the selected search index |
| `SyncApiController` | Web | `GET /api/v1/sync/{shareId}/changes?since=N` and the long-poll wait endpoint |
| `ClientSyncRetentionService` | Web | Prunes rows older than the retention window |

## Application data

`/data/kaimo-system` (`Storage:ApplicationDataPath`) contains process-wide secrets and caches:

| Path | Content |
|---|---|
| `.dp-keys/` | ASP.NET Data Protection key ring (application name `KaimoFiles`) |
| `.dp-certificate/` | Generated key-encryption certificate, unless `DataProtection:CertificatePath` points elsewhere |
| `.certs/` | Kestrel HTTPS certificate (Data Protection-encrypted), managed by `HttpsCertificateProvider` |
| `.external-storage/rsync-ssh/` | Client keys for rsync-over-SSH connections |
| `.kaimo-snapshots/` | Snapshot cache |

The database, the Data Protection key ring and its key-encryption certificate form one unit: secrets
stored in the database (connection grants, SMTP password, share-link tokens) can only be decrypted
with the matching key ring. See [Security model](security-model.md).

## Backups

Database backups are `pg_dump` custom-format archives in `/data/kaimo-backups`, created by the Host.
File content is not part of the database backup. Procedure and configuration:
[Database backup and restore](../operations/database-backup-and-restore.md).
