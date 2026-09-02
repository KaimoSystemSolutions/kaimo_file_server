# Optional environment variables

The example stack runs without any of the settings listed here. To override a
value, add it to the `environment` section of the named service in
`docker-compose.yml` and recreate that service.

For example, to enable informational console logging for the web service only:

```yaml
services:
  web:
    environment:
      <<: *dotnet-environment
      KAIMO_LOG_SOURCE: web
      Jwt__Secret: ${JWT_SECRET:?Set JWT_SECRET in .env}
      KAIMO_LOG_LEVEL: Information
```

Values placed only in `.env` are not passed into a container unless
`docker-compose.yml` references them. The values below are defaults; uncomment
or add only settings that need to differ from them.

## Host, SMB bridge, and web

These variables can be added to `host`, `smb-bridge`, or `web`.

| Variable | Default | Purpose |
| --- | ---: | --- |
| `KAIMO_LOG_LEVEL` | Application setting, then `Warning` | Forces the console log level. Supported values: `Debug`, `Information`, `Warning`, `Error`. |
| `LogArchive__ChannelCapacity` | `16384` | Maximum number of queued archive-log entries. |
| `LogArchive__MaxFileBytes` | `10485760` | Maximum size of one archived log file. |
| `LogArchive__RetentionDays` | `14` | Number of days archived logs are retained. |
| `LogArchive__MaxBytesPerSource` | `262144000` | Maximum archived-log size per service. |
| `Seed__AdminPassword` | Randomly generated | Password used only when the first administrator is created in an empty database. Set the same value on all three services because any of them may initialize the database first. |
| `Elasticsearch__Url` | `http://elasticsearch:9200` | Address of the Elasticsearch service used for file search. Change it only when pointing at an Elasticsearch instance outside this Compose stack. |

## Web only

Add these variables to `web`.

| Variable | Default | Purpose |
| --- | ---: | --- |
| `Jwt__Issuer` | `KaimoFileServer` | JWT issuer and audience. Changing it invalidates existing tokens. |
| `Jwt__ExpirationHours` | `24` | Login-token lifetime in hours. |
| `GoogleOAuth__ClientId` | Image default | Overrides the Google OAuth client ID. |
| `GoogleOAuth__ClientSecret` | Image default | Overrides the Google OAuth client secret. |

## SMB bridge only

Add these variables to `smb-bridge`.

| Variable | Default | Purpose |
| --- | ---: | --- |
| `ControlPlane__HashExportRateLimit__PermitLimit` | `2` | Maximum initial credential-export requests per window and client. |
| `ControlPlane__HashExportRateLimit__WindowSeconds` | `60` | Credential-export rate-limit window. |
| `Snapshots__Cache__RootPath` | `/data/kaimo-system/.kaimo-snapshots` | Snapshot cache path. Must match `KAIMO_SNAPSHOT_CACHE_ROOT` in `samba`. |
| `Snapshots__Cache__TtlHours` | `24` | Snapshot-cache lifetime. |
| `Snapshots__Cache__MaxBytesPerShare` | `5368709120` | Maximum snapshot-cache size per share. |
| `Snapshots__Cache__SweepMinutes` | `30` | Snapshot-cache cleanup interval. |
| `Snapshots__Materialization__MaxFilesPerRequest` | `10000` | Maximum files materialized by one snapshot request. |
| `Snapshots__Materialization__MaxBytesPerRequest` | `1073741824` | Maximum bytes materialized by one snapshot request. |
| `Snapshots__Materialization__MaxConcurrentRequests` | `2` | Maximum concurrent snapshot materializations. |
| `Snapshots__Materialization__MaxDurationSeconds` | `25` | Maximum snapshot materialization duration. |
| `LifecycleEvents__ReceiptRetentionDays` | `30` | Retention time for completed Samba lifecycle-event receipts. |

## Samba only

Add these variables to `samba`.

| Variable | Default | Purpose |
| --- | ---: | --- |
| `KAIMO_LOG_LEVEL` | Application setting, then `Warning` | Forces the Samba console log level. |
| `KAIMO_LOG_ARCHIVE_MAX_FILE_BYTES` | `10485760` | Maximum size of one archived Samba log file. |
| `KAIMO_LOG_ARCHIVE_RETENTION_DAYS` | `14` | Number of days archived Samba logs are retained. |
| `KAIMO_LOG_ARCHIVE_MAX_SOURCE_BYTES` | `262144000` | Maximum archived-log size per Samba log source. |
| `KAIMO_AUTHZ_FAILOPEN` | `0` | Set to `1` to allow access when authorization fails. This reduces security. |
| `KAIMO_AUTHD_WORKERS` | `16` | Authorization sidecar worker count. |
| `KAIMO_AUTHD_QUEUE_CAPACITY` | `64` | Maximum queued sidecar connections. |
| `KAIMO_AUTHD_IO_TIMEOUT_MS` | `2000` | Sidecar I/O timeout in milliseconds. |
| `KAIMO_VFS_AUTH_TIMEOUT_MS` | `6000` | VFS authorization timeout in milliseconds (`10`–`60000`). |
| `KAIMO_VFS_SNAPSHOT_TIMEOUT_MS` | `32000` | VFS snapshot timeout in milliseconds (`10`–`60000`). |
| `KAIMO_VFS_EVENT_TIMEOUT_MS` | `250` | Lifecycle-event enqueue timeout in milliseconds (`10`–`60000`). |
| `KAIMO_AUTHD_CACHE_MAX_ENTRIES` | `10000` | Maximum authorization-cache entries. |
| `KAIMO_AUTHD_CACHE_MAX_BYTES` | `8388608` | Maximum authorization-cache memory. |
| `KAIMO_AUTHD_CACHE_TTL_MS` | `3000` | Authorization-cache lifetime in milliseconds (`100`–`10000`). |
| `KAIMO_EVENT_MAX_PENDING` | `100000` | Maximum pending lifecycle events. |
| `KAIMO_EVENT_MAX_DEAD` | `10000` | Maximum dead-letter lifecycle events. |
| `KAIMO_EVENT_MAX_ATTEMPTS` | `20` | Maximum event-delivery attempts. |
| `KAIMO_EVENT_RETRY_BASE_MS` | `1000` | Initial event retry delay. |
| `KAIMO_EVENT_RETRY_MAX_MS` | `60000` | Maximum event retry delay. |
| `KAIMO_USER_SYNC_INTERVAL_SECONDS` | `60` | Samba user synchronization interval. |
| `KAIMO_SHARE_SYNC_INTERVAL_SECONDS` | `2` | Samba share synchronization interval. |
| `KAIMO_CONFIG_SYNC_INTERVAL_SECONDS` | `2` | Samba configuration synchronization interval. |
| `KAIMO_SYNC_HEALTH_MAX_AGE_SECONDS` | `180` | Maximum age of the last successful synchronization before Samba becomes unhealthy. |
| `KAIMO_LIST_FILTER` | `1` | Set to `0` to disable ACL-based directory-list filtering. |
| `KAIMO_SNAPSHOT_OPENAT` | `1` | Set to `0` to disable snapshot `openat` handling. |
| `KAIMO_SNAPSHOT_CACHE_ROOT` | `/data/kaimo-system/.kaimo-snapshots` | Snapshot cache path. Must match the SMB bridge value. |
| `KAIMO_STORAGE_GID` | `1654` | POSIX group ID used for shared storage access. |
| `KAIMO_UNMANAGED_SAMBA_USERS` | Empty | Comma-separated Samba users excluded from managed-user synchronization. |

Path, socket, executable, and health-check implementation variables are not
listed because changing them requires matching image or volume-layout changes
and is not a normal deployment override.
