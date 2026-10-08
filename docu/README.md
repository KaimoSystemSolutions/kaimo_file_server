# Kaimo File Server — Technical Documentation

This folder documents how Kaimo File Server works at the technical level: the processes and
containers, how they communicate, where state lives, how access is enforced, and which external
interfaces exist. It describes the implemented system only.

## The stack in one paragraph

Kaimo File Server is a Docker Compose stack around one PostgreSQL database. A .NET **Host** worker
owns the schema, backups and reconcilers. The .NET **Web** host serves the Blazor UI, the REST API,
WebDAV, downloads and share links, and runs the single-owner workers for search indexing, mail
dispatch and external-storage syncs. **SMB** is served by a self-built Samba whose custom VFS module
asks a .NET **SmbBridge** over mutually authenticated gRPC for every authorization decision and
reports every change. All transports converge on the same Core `IFileService`, so ACLs, recycle bin,
versioning and the change log behave identically everywhere. Elasticsearch is a derived index fed
asynchronously from the change log.

## Reading order

### Architecture

| Document | Content |
|---|---|
| [System overview](architecture/system-overview.md) | Containers, networks, volumes, startup order, code layering |
| [Storage and persistence](architecture/storage-and-persistence.md) | PostgreSQL, storage pools, versions, recycle bin, snapshot cache, change log |
| [Domain model](architecture/domain-model.md) | Entities and relationships |
| [Security model](architecture/security-model.md) | Authentication, ACL evaluation, management permissions, secrets, transport security |
| [Background services](architecture/background-services.md) | Hosted services per process and coordination patterns |

### External access

Every way a client reaches the server from outside. All of them converge on the same ACL-checked
`IFileService`.

| Document | Content |
|---|---|
| [REST API v1](external-access/rest-api.md) | `/api/v1` authentication and devices, browsing, safe mutations, error format |
| [Client Sync API](external-access/sync-api.md) | `/api/v1/sync` device sync connections, delta, change feed, long-poll |
| [Search API](external-access/search-api.md) | `POST /api/v1/search` request, response, limits |
| [WebDAV server](external-access/webdav.md) | `/dav` architecture, authentication, methods, locking |
| [Downloads and share links](external-access/downloads-and-share-links.md) | Ticket-based downloads, public download and upload links |
| [SMB: Samba VFS integration](external-access/smb/samba-vfs-integration.md) | Samba build, VFS hooks, `kaimo_authd` sidecar, local protocol, POSIX identity model |
| [SMB: Control plane (gRPC)](external-access/smb/control-plane-grpc.md) | mTLS PKI, RPC contract and allow-list, user/share/config provisioning |
| [SMB: Lifecycle events and snapshots](external-access/smb/lifecycle-events-and-snapshots.md) | Durable event spool, idempotency, close capture, @GMT Previous Versions |

### External storage

| Document | Content |
|---|---|
| [Connections and credentials](external-storage/connections-and-credentials.md) | Connection model, provider contracts, credential vault, authorization runtime |
| [Providers](external-storage/providers.md) | OneDrive, Dropbox, Google Drive, SMB, SFTP, rsync over SSH, WebDAV |
| [Sync engine](external-storage/sync-engine.md) | Sync definitions, scheduling, execution, delete propagation, legacy import |
| [Virtual shares](external-storage/virtual-shares.md) | Cloud Access virtual shares, grants, metadata cache, cross-share transfers |

### Subsystems

| Document | Content |
|---|---|
| [Search and indexing](subsystems/search-indexing.md) | Elasticsearch or local PostgreSQL index, change-log indexer, filename fallback, ACL filter |
| [Mail notifications](subsystems/mail-notifications.md) | Outbox, dispatcher, rules, templates |
| [User home folders](subsystems/user-home-folders.md) | The `users` system share and its ACL layout |

### Testing

| Document | Content |
|---|---|
| [Database tests](testing/database-tests.md) | Sqlite default suite, opt-in PostgreSQL tests (`KAIMO_TEST_PG`) for retries, isolation and locks |
| [Samba VFS test suite](testing/samba-vfs-test-suite.md) | Test layers, harness and fakes, failure detection, coverage gates, CI, known findings and gaps |
| [Samba VFS test catalog](testing/samba-vfs-test-catalog.md) | What is caught, scenario by scenario, with the test that proves it |

### Operations

| Document | Content |
|---|---|
| [Database backup and restore](operations/database-backup-and-restore.md) | Scheduled and manual backups, restore, what else to back up |
| [WebDAV client setup](operations/webdav-client-setup.md) | Connecting Windows, macOS, Linux and other clients |
| [Microsoft OneDrive setup](operations/microsoft-onedrive-setup.md) | Entra application, overrides, runtime requirements |
| [Dropbox setup](operations/dropbox-setup.md) | Dropbox app registration and app key |

## Related documentation outside this folder

- `src/samba-vfs/README.md` — build, test and run notes for the Samba container.
- `src/Kaimo_File_Server.Web/DESIGN_SPEC.md` — binding design specification for the web UI.
- `example/` — example deployment configuration.
