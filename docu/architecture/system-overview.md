# System Overview

This document describes how Kaimo File Server is assembled at runtime: the containers, the networks
between them, the mounted volumes, the startup order, and how the .NET and native code is layered.
Code paths are relative to the repository root (`Kaimo_File_Server/`).

Related: [Storage and persistence](storage-and-persistence.md) ·
[Security model](security-model.md) · [Background services](background-services.md) ·
[Samba VFS integration](../external-access/smb/samba-vfs-integration.md)

## Runtime topology

The system runs as a Docker Compose stack (`docker-compose.yml`). Development overrides live in
`docker-compose.dev.yml`, which must be passed explicitly with `-f`.

```mermaid
flowchart LR
    subgraph clients[Clients]
        smbc[SMB clients]
        http[Browsers / REST / WebDAV clients]
    end

    subgraph default[network: default]
        samba["kaimo_samba<br/>smbd + VFS module + kaimo_authd"]
        web["kaimo_file_server_web<br/>ASP.NET: Blazor, REST, WebDAV"]
        host["kaimo_file_server<br/>Host worker (DB owner)"]
        es[(Elasticsearch)]
    end

    bridge["kaimo_smb_bridge<br/>gRPC control plane"]
    db[(PostgreSQL<br/>kaimo_file_server)]
    pools[(Storage pools<br/>/data/storage/*)]

    smbc -- "SMB :445" --> samba
    http -- "HTTP :8081 / HTTPS :8443" --> web
    samba -- "gRPC + mTLS :5080<br/>(kaimo_smb_control)" --> bridge
    bridge -- "kaimo_bridge_database" --> db
    web -- "kaimo_database" --> db
    host -- "kaimo_database" --> db
    web --> es
    host --> es
    samba --- pools
    web --- pools
    host --- pools
    bridge --- pools
```

| Service (container) | Built from | Role | Published ports | Networks |
|---|---|---|---|---|
| `kaimo_smb_pki_init` | `src/samba-vfs/Dockerfile.pki` | One-shot generation of the control-plane mTLS PKI into `data/smb-control-plane` | – | default |
| `kaimo_file_server.host` (`kaimo_file_server`) | `src/Kaimo_File_Server.Host/Dockerfile` | Database owner: restore, backup, migrations, seeding; data-service reconciliation; version-storage reconciliation | – | default, `kaimo_database` |
| `kaimo_smb_bridge` | `src/Kaimo_File_Server.SmbBridge/Dockerfile` | gRPC control plane for Samba (authentication, authorization, events, shares, configuration, snapshots) | – (5080 internal) | `kaimo_bridge_database`, `kaimo_smb_control` |
| `kaimo_samba` | `src/samba-vfs/Dockerfile.vfs` | Native SMB data path: Samba built from source, the `kaimo_bridge` VFS module and the `kaimo_authd` sidecar | `445` | default, `kaimo_smb_control` |
| `kaimo_file_server.web` (`kaimo_file_server_web`) | `src/Kaimo_File_Server.Web/Dockerfile` | Web UI, REST API, WebDAV, downloads and share links, search indexing, mail dispatch, external-storage sync | `8081→8080`, `8443→8443` | default, `kaimo_database` |
| `elasticsearch` (`kaimo_elasticsearch`) | `elasticsearch:9.4.2` | Full-text index, single node | `127.0.0.1:9200` | default |
| `kaimo_file_server_db` | `postgres:17-alpine` | The single PostgreSQL database `kaimo_file_server` | – | `kaimo_database`, `kaimo_bridge_database` |
| `adminer` | `adminer` | Database administration UI | `127.0.0.1:8080` | default, `kaimo_database` |

`docker-compose.dev.yml` adds a `mailpit` SMTP catcher and development-only secrets.

### Networks

All non-default networks are `internal: true` (no route to the outside):

- **`kaimo_database`** connects Host and Web to PostgreSQL.
- **`kaimo_bridge_database`** connects only the bridge to PostgreSQL. The bridge is not on the
  default network and has no outbound connectivity.
- **`kaimo_smb_control`** connects Samba to the bridge. It carries the gRPC control plane only;
  Samba has no database access.

### Volumes

Every .NET service and Samba mount the same host directories (YAML anchors at the top of
`docker-compose.yml`):

| Mount | Content |
|---|---|
| `/data/storage/pool01`, `/data/storage/pool02` | Storage pools (share data, recycle bins, version blobs) |
| `/data/kaimo-system` | Application data: Data Protection key ring, HTTPS certificate, SSH material, snapshot cache |
| `/data/kaimo-logs` | Structured log archive, one source folder per process |
| `/data/kaimo-backups` | Database backups (Host and Web only) |
| `/var/lib/kaimo/event-spool` | Durable SMB lifecycle event spool (Samba only) |
| `/run/secrets/kaimo-control-plane` | mTLS material: `data/smb-control-plane/bridge` for the bridge, `.../samba` for Samba |

Samba additionally uses a `tmpfs` at `/run/kaimo-user-sync` for transient credential import files.

### Startup order

```mermaid
sequenceDiagram
    participant DB as PostgreSQL
    participant PKI as pki_init
    participant H as Host
    participant B as SmbBridge
    participant W as Web
    participant S as Samba
    DB->>DB: healthy (pg_isready)
    PKI->>PKI: generate CA + certificates (once)
    H->>H: writable-directory preflight
    H->>DB: optional restore, pre-migration backup, migrate, seed, ready marker
    H->>H: ensure Elasticsearch index mapping
    B->>DB: WaitForDatabaseReadyAsync (poll until migrated and ready marker set)
    W->>DB: WaitForDatabaseReadyAsync
    S->>B: authd + sync jobs connect over mTLS
```

Compose uses `service_started` (not `service_healthy`) for the Host dependency. The real ordering
guarantee is in-process: `WaitForDatabaseReadyAsync` in
`src/Kaimo_File_Server.Infrastructure/ServiceCollectionExtensions.cs` blocks Web and bridge until the
Host has applied all migrations **and** finished seeding. The Host signals the latter through the
`system.schema.ready` key in `config_settings`: as soon as it holds the migration lock (again right
after a startup restore, whose data carries the backup's marker, and again after migrating) it clears
the key, and as the final step under the lock it writes its last migration id. The migration lock is a
session-level advisory lock; before migrating and before writing the marker the Host verifies that its
connection was not silently re-established (which would have dropped the lock) and otherwise restarts
the startup sequence under a fresh lock. A Web or bridge process that starts while the Host seeds therefore waits even when the
release changed seeding without adding a migration. Only a process that checks before the Host has
connected at all still passes on the previous run's marker. A Web or bridge image that finds applied migrations it does not know is older than
the schema; it stops at once with an explicit error instead of writing to a newer schema. Only the
Host ever migrates.

Host, bridge, Web, Samba, PostgreSQL and Elasticsearch run with `restart: unless-stopped`, so the
stack comes back after a reboot or Docker restart, and a process that exits during startup (e.g. the
10-minute readiness timeout) comes back on its own. The flip side: a failure that waiting cannot fix
shows up as a restart loop, not as a stopped container. If the Host fails after migrating (e.g. a seeder
error or an invalid NT-hash key), the ready marker stays cleared, so a Web or bridge process that
restarts in the meantime waits 10 minutes, exits and starts over until the Host is fixed. An image older
than the schema exits at once on every start. Check the container logs when a service keeps restarting.

Host and Web run with `umask 0002` so files they create stay group-writable for the shared storage
group used by Samba.

## Code layering

```mermaid
flowchart TD
    Core["Kaimo_File_Server.Core<br/>domain, abstractions, services"]
    Infra["Kaimo_File_Server.Infrastructure<br/>EF Core, repositories, storage, providers"]
    Search["Kaimo_File_Server.Search<br/>Elasticsearch / filename search"]
    Host["Kaimo_File_Server.Host<br/>worker"]
    Web["Kaimo_File_Server.Web<br/>ASP.NET"]
    Bridge["Kaimo_File_Server.SmbBridge<br/>ASP.NET gRPC"]
    Vfs["samba-vfs (C/C++)<br/>VFS module, authd, sync tools"]
    Infra --> Core
    Search --> Core
    Host --> Infra & Search
    Web --> Infra & Search
    Bridge --> Infra & Search
    Vfs -. "gRPC (protos/kaimo_smb_bridge.proto)" .-> Bridge
```

| Project | Type | Responsibility |
|---|---|---|
| `src/Kaimo_File_Server.Core` | Class library | Domain entities (`Domain/`), repository interfaces (`Repositories/`), security (`Security/AclService.cs`, `ManagementPermission.cs`), the protocol-independent file service (`Services/File/FileService.cs`, `FileVersionService.cs`), external-storage contracts (`Services/ExternalStorage/`) |
| `src/Kaimo_File_Server.Infrastructure` | Class library | `ApplicationDbContext` and migrations (`Persistence/`, `Migrations/`), repositories, file-system storage (`Storage/FileSystemStorage.cs`), backups, cloud and protocol providers (`Clouds/`, `ExternalStorage/`), notifications outbox, log archive, shared hosted services. Composition entry points: `AddInfrastructure`, `AddCoreServices`, `AddExternalStorageProviders`, `MigrateSeedAndBackupAsync`, `WaitForDatabaseReadyAsync` in `ServiceCollectionExtensions.cs` |
| `src/Kaimo_File_Server.Search` | Library (Worker SDK) | `AddElasticSearch`, `SearchServiceRouter` (Elasticsearch, local PostgreSQL index or filename fallback), `SearchAclFilter` |
| `src/Kaimo_File_Server.Host` | Worker | Database owner and reconcilers. No network listener (`Program.cs`) |
| `src/Kaimo_File_Server.Web` | ASP.NET | Kestrel on 8080/8443, Blazor Server, MVC controllers (`Controllers/Api`, `Controllers/WebDav`), OpenAPI at `/openapi/v1.json` |
| `src/Kaimo_File_Server.SmbBridge` | ASP.NET gRPC | gRPC services in `Services/*GrpcService.cs`, mTLS and per-identity authorization in `Security/` |
| `src/samba-vfs` | C / C++ / shell | `module/vfs_kaimo_bridge.c` (loaded into smbd), `module/authd.cpp` (sidecar), `module/{authsync,sharesync,configsync}.cpp` (pull clients), `sync-*.sh`, `supervise-samba.sh`, Samba patches |

All protocol front ends (Web UI, REST, WebDAV, SMB via the bridge, public share links) converge on
the same Core services. ACL checks, recycle bin, versioning, ownership and change logging therefore
behave identically regardless of the protocol that triggered an operation.

## Cross-process coordination

The processes share no memory and do not call each other, with one exception: Samba calls the bridge
over gRPC. All other coordination goes through PostgreSQL:

- **Desired state and settings** live in the `config_settings` table (data-service enable flags,
  log level, SMB and search settings, storage pool labels). Each process reads them through
  `IConfigRepository` with short caches.
- **Feeds** between processes are append-only tables: `file_change_log` (consumed by the search
  indexer and the client sync API) and `notification_events` (consumed by the mail dispatcher).
- **Single-owner workers**: schema migrations and backups run only in the Host; Elasticsearch writes
  and mail dispatch run only in Web. See [Background services](background-services.md).
- **Liveness**: the Host refreshes `runtime.host.heartbeat` every 30 s on a loop of its own, so a
  slow reconcile pass (e.g. a long Samba start) does not make it look dead. Shutdown does not wait
  for a pending heartbeat write, so an unreachable database cannot delay the graceful Samba stop. The Web settings page
  shows "Host not reachable" once it is older than 90 s (or missing), instead of the last SMB status
  the Host wrote.

### Channel robustness

- **Connection**: every process uses Npgsql with `EnableRetryOnFailure` (3 retries, up to 5 s), kernel
  TCP keepalives (`Tcp Keepalive`, first probe after 30 s idle, then every 10 s; left untouched when the
  connection string sets any `Tcp Keepalive*` key), which also detect a dead peer while a command waits
  for its result, and `Application Name=kaimo-<source>` for `pg_stat_activity`. Code that opens an
  explicit transaction must run it through `DbContextFactoryExtensions.ExecuteResilientAsync`
  (`Infrastructure/Persistence/`), which replays the whole unit on a fresh context; its cancellation
  token also cancels the backoff between attempts. Once the retries are exhausted EF throws
  `RetryLimitExceededException` (a plain `Exception`); the startup waits treat it as transient.
- **Logging**: every retry is logged as a warning with its cause (EF's `ExecutionStrategyRetrying`,
  raised from Information because the default level is Warning), every replayed unit additionally as
  "Replaying database unit of work <method> (attempt n)", and every replay that finds its effect already
  committed names what it found. Category: `Kaimo_File_Server.Infrastructure.Database`.
- **Replay idempotency**: a replay can follow a COMMIT that succeeded but whose acknowledgement was
  lost. This applies to whole units run through `ExecuteResilientAsync` and to single auto-committed
  statements, which the strategy retries as well. Writes whose duplicate would matter therefore check
  whether their effect is already committed: cloud-sync and credential lease acquisition accept their
  own lease id, cloud-sync lease renewal accepts the value it just wrote, token rotation accepts its own
  replacement, a notification event insert looks for the identical row, notification and Samba event
  claims recognize their own lease timestamp, mail delivery claims their own start time, Samba event
  completion accepts an already completed receipt, notification completion updates the `Processing`
  event first (row lock) and stores deliveries only if it matched (it never moves a finished event back
  to `Pending`), and a rename without event receipt replays exactly the rows its first attempt read
  (moves and deletes by row id), which is correct whether that attempt committed or rolled back and
  always reports the displaced versions for blob removal.
  Other plain inserts and the share-link access/upload counters are at-least-once on a lost
  acknowledgement; the warning log shows when that happened. The change-log append is the deliberate
  exception (see [Storage and persistence](storage-and-persistence.md#change-log)).
- **Leases** (cloud-sync operation lease in `config_settings`, credential lease, Samba event
  receipts) are renewed by a heartbeat that survives transient database errors. Renew and release
  are compare-and-swap updates on the value read before, so a former owner can never overwrite or
  delete a lease another process has taken over after expiry, even when the update first waits for a
  row lock. An expired Samba lease is never revived. When a holder finds its lease lost, it logs a
  warning and stops renewing. A cloud sync additionally stops its transfer
  (`ICloudSyncOperationLease.LeaseLost`) and ends as `Interrupted` (not failed, no notification). The
  cloud-sync lease also signals `LeaseLost` a few minutes before its absolute 6-hour lifetime, while it
  is still valid, so a long sync stops before anything could take it over and resumes on its next
  run. A notification completion carries the claim's lease value and is discarded once the event was
  re-claimed. Samba lifecycle handlers and share moves are not cooperative and keep running, so there
  the overlap is only reported. Releasing is best effort; an unreleased lease expires.
- **Lease acquisition** treats a serialization failure or duplicate key as contention: it retries
  twice after 20 ms and then reports the share busy, instead of going through the execution
  strategy's backoff of seconds. Connection failures still go to the strategy.
- **Config writes** are upserts: a concurrent insert of the same key turns into an update.

## Logging

Every .NET process registers `AddDynamicLogLevel` (log level read live from the database) and
`AddLogArchive("<source>")`, which writes structured Information+ entries to
`/data/kaimo-logs/<source>/` with size-based rotation and retention
(`src/Kaimo_File_Server.Infrastructure/Logging/`). Samba forwards its logs into the same archive via
`src/samba-vfs/kaimo-samba-log-forwarder.py`.
