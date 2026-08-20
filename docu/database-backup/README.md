# Database backup & restore

Kaimo File Server stores everything except file contents in a PostgreSQL 17
database. This document describes the built-in backup strategy and how to
restore a backup.

## Overview

| Concern | Where it runs | Notes |
| --- | --- | --- |
| Schema migrations + seeding | **Host** process only | Web and SmbBridge wait until the Host is ready. |
| Scheduled backups | **Host** (`DatabaseBackupSchedulerService`) | One backup per day inside a configurable time window. |
| Pre-migration backup | **Host**, before applying migrations | Safety net so a failed migration can be rolled back. |
| Manual backup + download | **Web** UI (Settings → *Backup*) | Gated by the `ManageBackups` permission. |
| Startup restore | **Host**, before migrations | One-shot, triggered by `KAIMO_DB_RESTORE_FROM`. |

Backups are created with `pg_dump` in the compressed **custom format** (`-Fc`),
which is restorable with `pg_restore`. They are written to the backup folder
that is mounted into the Host and Web containers.

## Configuration

### Backup folder (docker-compose / `.env`)

The backup folder is a host bind mount, configured like the other data paths:

```env
# .env
LOCATION_BACKUP=./tests/data/backups
```

It is mounted to `/data/kaimo-backups` inside the Host and Web containers
(`Backup__RootPath`). Automatic and manual backups both land here.

### Schedule & retention (Settings UI)

Open **Settings → Backup** (requires the `ManageBackups` permission). You can:

- **Enable/disable** the daily scheduled backup.
- Set the **time window** (start/end, local server time). The backup runs once
  per day inside the window. If the server was off during the window, the backup
  runs on the next start that still falls inside it (catch-up).
- Set **retention**: how many scheduled backups to keep and after how many days
  to delete them. Pre-migration backups are kept for 90 days and are never
  pruned by count; manual backups are never auto-deleted.

### Permission / role

- Permission bit: `ManagementPermission.ManageBackups`. It is part of
  `SystemAdmin` and `FullAdmin`.
- A dedicated system role **`BackupManager`** is seeded with only this
  permission, so backup management can be delegated without full system access.

## Creating backups

- **Automatic:** happens on schedule and before every migration; no action
  needed.
- **Manual:** Settings → Backup → *Create backup now*. The file appears in the
  list and can be downloaded.

Backup file names encode the timestamp and the trigger, for example:

```
kaimo_20260820-030000_scheduled.dump
kaimo_20260820-101500_manual.dump
kaimo_20260819-221000_premigration.dump
```

## Restoring a backup

Restore is a **startup** operation performed by the Host, kept deliberately
simple and safe:

1. Make sure the backup file is in the backup folder (it already is if it was
   created by this system, i.e. under `LOCATION_BACKUP` on the host →
   `/data/kaimo-backups` in the container).
2. Set the restore path in `.env` to the file **as seen inside the container**:

   ```env
   KAIMO_DB_RESTORE_FROM=/data/kaimo-backups/kaimo_20260819-221000_premigration.dump
   ```

3. Restart the stack (at least the Host). On startup the Host will:
   - restore the dump with `pg_restore --clean --if-exists` (this **overwrites**
     the current database),
   - apply any newer migrations on top (so an older dump is brought up to the
     current schema),
   - write a marker file `…​.dump.done` next to the backup.

4. Because of the `.done` marker, the **same file is not restored again** on the
   next start, even if `KAIMO_DB_RESTORE_FROM` is still set. To restore the same
   file again, delete its `.done` marker. For normal operation, clear
   `KAIMO_DB_RESTORE_FROM` again.

### Manual restore (fallback)

You can also restore manually with the PostgreSQL client, e.g. from the db
container or any host with `psql`/`pg_restore`:

```bash
pg_restore --clean --if-exists --no-owner --no-privileges \
  -h <db-host> -U <user> -d <database> \
  /path/to/kaimo_20260819-221000_premigration.dump
```

## How readiness gating works

The Host owns the schema. Web and SmbBridge never migrate — on startup they call
`WaitForDatabaseReadyAsync`, which polls the database until there are no pending
migrations, and only then continue. This in-app poll is the single,
orchestration-independent readiness guarantee: it holds regardless of Docker
Compose ordering (and regardless of whether the Host container is started before
or after them).

In `docker-compose.yml`, Web and SmbBridge additionally declare
`depends_on: { host: { condition: service_started } }` purely as a
container-ordering hint. It is intentionally **not** `service_healthy`: gating on
a Host healthcheck breaks Visual Studio's "fast mode" container debugging, where
VS replaces the Host entrypoint with a wait-stub so the app only runs once the
debugger attaches — the Host would then never report healthy and the whole stack
would stall. The in-app wait avoids that entirely.
