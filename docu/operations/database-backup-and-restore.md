# Database Backup and Restore

Kaimo File Server stores everything except file contents in one PostgreSQL 17 database. This guide
describes the built-in backups, how to restore one, and what else must be backed up alongside.

Related: [Storage and persistence](../architecture/storage-and-persistence.md) ·
[Background services](../architecture/background-services.md)

## Overview

| Concern | Runs in | Notes |
|---|---|---|
| Schema migrations and seeding | Host only | Web and SmbBridge wait until the Host is ready |
| Scheduled backups | Host (`DatabaseBackupSchedulerService`) | One backup per day inside a configurable time window |
| Pre-migration backup | Host, before applying migrations | Safety net for a failed migration |
| Manual backup and download | Web (`DatabaseBackupService`, `GET /api/database-backups/download`) | Requires `ManageBackups` |
| Startup restore | Host, before migrations | One-shot, triggered by `KAIMO_DB_RESTORE_FROM` |

Backups are created with `pg_dump` in the compressed custom format (`-Fc`) and restored with
`pg_restore` (`src/Kaimo_File_Server.Infrastructure/Backup/`).

## Configuration

The backup folder is a host bind mount, mounted at `/data/kaimo-backups` in the Host and Web
containers (`Backup__RootPath`):

```env
# .env
LOCATION_BACKUP=./data/backups
```

Schedule and retention are stored in the database and edited by administrators with
`ManageBackups`:

- enable or disable the daily scheduled backup;
- time window (local server time). If the server was off during the window, the backup runs on the
  next start that still falls inside it;
- retention for scheduled backups (count and age). Pre-migration backups are kept for 90 days and are
  not pruned by count; manual backups are never deleted automatically.

The permission `ManageBackups` is part of the `SystemAdmin` and `FullAdmin` presets; the seeded system
role **BackupManager** carries only this permission.

File names encode timestamp and trigger:

```
kaimo_20260820-030000_scheduled.dump
kaimo_20260820-101500_manual.dump
kaimo_20260819-221000_premigration.dump
```

## What else to back up

The database backup does not contain:

- **file content** in the storage pools (`/data/storage/*`, including `.kaimo-versions` and
  `.RECYCLE_BIN`);
- the **Data Protection key ring and key-encryption certificate** in `/data/kaimo-system`
  (`.dp-keys/`, `.dp-certificate/`). Connection credentials, the SMTP password, share-link display
  tokens and the HTTPS certificate in the database or app-data folder can only be decrypted with this
  key ring;
- the **secrets** passed as environment variables (`JWT_SECRET`, `NT_HASH_ENCRYPTION_KEY`). Without the
  original `NT_HASH_ENCRYPTION_KEY`, stored NT hashes cannot be decrypted and SMB logins fail until
  users set their passwords again.

Back up the database, `/data/kaimo-system` and the secrets from the same point in time and restore
them together.

## Restoring a backup

Restore is a startup operation performed by the Host:

1. Place the dump in the backup folder (`LOCATION_BACKUP` on the host, `/data/kaimo-backups` in the
   container).
2. Set the path **as seen inside the container**:

   ```env
   KAIMO_DB_RESTORE_FROM=/data/kaimo-backups/kaimo_20260819-221000_premigration.dump
   ```

3. Restart the stack (at least the Host). On startup the Host:
   - restores the dump with `pg_restore --clean --if-exists` (this **overwrites** the current
     database);
   - applies newer migrations on top, bringing an older dump to the current schema;
   - writes a marker file `<dump>.done` next to the backup.
4. Because of the marker, the same file is not restored again on the next start. Delete the marker to
   restore the same file again, and clear `KAIMO_DB_RESTORE_FROM` for normal operation.

### Manual restore

```bash
pg_restore --clean --if-exists --no-owner --no-privileges -h <db-host> -U <user> -d <database> /path/to/kaimo_20260819-221000_premigration.dump
```

## Readiness gating

Only the Host migrates. Web and SmbBridge call `WaitForDatabaseReadyAsync`, which polls until no
migrations are pending, and continue only then. This in-process wait is the readiness guarantee
regardless of container start order. `docker-compose.yml` declares the Host dependency with
`condition: service_started` as an ordering hint only; a `service_healthy` gate would stall
debugger-attached development runs in which the Host entrypoint is replaced.
