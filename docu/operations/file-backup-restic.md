# File Backup and Restore (restic)

Kaimo backs up share and pool content, the database and the application data into encrypted,
deduplicated [restic](https://restic.net) repositories. This guide covers repositories, jobs, the
snapshot layout, restores, maintenance and the disaster-recovery runbook.

Related: [Database backup and restore](database-backup-and-restore.md) ·
[Storage and persistence](../architecture/storage-and-persistence.md) ·
[Background services](../architecture/background-services.md) ·
[Mail notifications](../subsystems/mail-notifications.md)

## Overview

| Concern | Where | Notes |
|---|---|---|
| restic binary | Web image, `/usr/local/bin/restic` | Pinned official release (`RESTIC_VERSION` in `src/Kaimo_File_Server.Web/Dockerfile`), SHA-256 verified; licenses in `/usr/share/doc/restic/` |
| Everything restic does | Web process | The credential vault that protects repository passwords lives there |
| UI | `/backup` | Tabs Overview, Jobs, Restore, Repositories (Global), Activity |
| Catalog and authorization | `BackupCatalogService` | Every action re-checks the actor's permissions server-side |
| Schedules, stale alerts | `BackupSchedulerService` | Polls every minute |
| Execution | `BackupRunner` | One serial lane for backups and maintenance, one lane for restores |
| Audit trail | table `backup_runs` | Every init, backup, forget, prune, check, copy, test restore and restore |
| Snapshot cache | table `backup_snapshots` | The UI and scope checks never contact the repository to list backup points |

restic is always called with secrets in the environment only (`RESTIC_PASSWORD`, backend
credentials), never on the command line. The child process does not inherit the Web environment
(which contains the database connection string). Every snapshot uses the fixed host name `kaimo`, so
container re-creation does not break parent-snapshot detection.

The database backup in Settings (`pg_dump` into `/data/kaimo-backups`) is a separate, unencrypted
feature and stays available.

## Permissions

| Bit | Scope | Allows |
|---|---|---|
| `ManageBackupRepositories` | Global only | Repositories, recovery kits, maintenance, system and pool restores |
| `ManageBackupJobs` | Global or department/share | Jobs for shares in scope; pools and database levels only at Global scope |
| `RestoreFromBackup` | Global or department/share | Browse, download, restore into a new folder or (as source) into another share |
| `RestoreBackupInPlace` | Global or department/share | Restore over the original location, into another share (as target), restore permissions |

The bits are part of the seeded **Administrator** role. The system role **BackupOperator** carries
the job and both restore bits and is meant to be assigned per department or share. The bits are not
part of `FullAdmin`/`SystemAdmin`.

Scoping rules:

- A scoped job admin sees and edits a job only if every source is a share in their scope and the job
  backs up files only.
- The homes share (`users`), pools and the content levels *Files + database* and *Full* are
  Global-only.
- A repository is usable for a job if it is active and released to the department of every source
  share or one of its parent departments (releases are inherited).
- Scoped admins never see repository settings or passwords.

## Repositories

A repository is created on the Repositories tab: backend, location, credentials, department release.

| Backend | Location | Credentials |
|---|---|---|
| Local | Path below one of `Backup:Restic:LocalRoots` (default `/data/kaimo-backups/restic`) | – |
| SFTP | Existing SFTP or rsync-over-SSH connection (pinned host key required) plus remote path | SSH key or password of the connection (password via a private `SSH_ASKPASS` helper in the restic temp directory, never as argument or environment value) |
| REST server | `https://host[:port]`, optional path; optional CA certificate | Optional user and password (`RESTIC_REST_USERNAME/PASSWORD`) |
| S3 | Endpoint, bucket, optional path, region, path-style flag; optional CA certificate | Access key and secret key |
| Backblaze B2 | Bucket, optional path (`b2:<bucket>:/<path>`) | Key ID and application key (`B2_ACCOUNT_ID/KEY`) |
| Azure Blob Storage | Storage account, container, optional path, optional endpoint suffix (national clouds) | Account key **or** SAS token |
| Google Cloud Storage | Bucket, optional path (`gs:<bucket>:/<path>`) | Service account key (JSON, type `service_account` or `authorized_user`); `project_id` is taken from it |
| OpenStack Swift | Container, optional path; optional CA certificate | `OS_*` / `ST_*` variables, openrc style (`export OS_X=…`, other lines ignored) |
| rclone | Path on the remote (incl. bucket) | One pasted rclone.conf remote section |

Secrets are stored vault-encrypted. The GCS key and the rclone section are written to owner-only
files in `Backup:Restic:TempDirectory` per restic process (`GOOGLE_APPLICATION_CREDENTIALS`,
`RCLONE_CONFIG`) and deleted afterwards. SFTP password connections also place their `SSH_ASKPASS`
helper there, which ssh executes: the directory must not be on a `noexec` mount, and the OpenSSH
client must be 8.4 or newer (`SSH_ASKPASS_REQUIRE=force`).

**rclone** reaches any storage rclone supports (OneDrive, Google Drive, Dropbox, WebDAV, …) with the
bundled, checksum-pinned rclone binary (`/usr/local/bin/rclone`). The pasted section is always
written as remote `[kaimo]` (URL `rclone:kaimo:<path>`), so its original name does not matter. To
keep the server's file system out of reach, remotes of type `local`, `alias`, `union`, `combine`,
`crypt`, `chunker`, `hasher`, `compress`, `cache` and `memory` are rejected, as are options that run
commands or read local files (`ssh`, `*_command`, `*_file`, `*_path`, `ca_cert`, `client_cert`,
`client_key`, `kerberos_ccache`); use the inline variants (`service_account_credentials`,
`key_pem`). When rclone refreshes an OAuth token during a run it rewrites its config file; Kaimo
validates the rewritten section like pasted input and stores it (vault-encrypted) before the temp
file is deleted, so remotes with rotating refresh tokens (e.g. OneDrive) keep working. Probing
edited credentials never writes anything back.

Mirrors between two repositories of the same backend type need identical credentials (restic's
backend variables are process-wide): same B2 key, same Azure account and key/SAS, same GCS key,
same Swift variables, same rclone section.

"Test" probes the location. **Initialize new** creates a repository with a generated 32-byte
password; **Connect existing** attaches a repository with its known password (disaster recovery).
The restic repository id is stored and re-checked whenever the location or credentials change.

### Recovery kit

A new repository stays in *Recovery kit pending* and accepts no backups until its recovery kit was
downloaded and confirmed by typing the kit code printed in it. The kit (`.txt`) contains the
repository URL, the password, non-secret parameters, the snapshot layout and the CLI and
disaster-recovery steps. It never contains backend credentials (access keys, REST password, SSH
key, service account key, Swift password, rclone section) — only the names of the variables to
set; keep the credentials separately. For rclone repositories, restoring with the CLI needs rclone
installed and the remote available as `[kaimo]` in the rclone.conf (or adjust the URL). Every kit download is logged. **Without the password nobody can restore the
repository.**

### Further settings

- **Append-only** (rest-server `--append-only`): retention and prune are skipped.
- **Minimum free space** (local repositories): a backup aborts with `insufficient_space` below it.
- **Maintenance schedule**, monthly data read, test-restore sample size and mirror, see
  [Maintenance](#maintenance-and-integrity).

## Jobs

A job writes its sources into one repository at fixed local times on selected weekdays. Missed slots
are caught up once after downtime (at most 7 days back). A job without times or weekdays runs on
demand only.

| Setting | Meaning |
|---|---|
| Shares | One backup point per share per run |
| Pools (Global) | One backup point per pool: the whole pool folder, including folders that are no share |
| Content | *Files*, *Files + database* or *Full* (see below; the database levels are Global-only) |
| Include recycle bin | Also back up `.RECYCLE_BIN` (per share; for the homes share per home folder) |
| Include file versions (pools only) | Also back up the pool's version store `.kaimo-versions`; only effective with a database level, because the blobs are useless without their `file_versions` rows |
| Retention | keep-last/daily/weekly/monthly/yearly per job; all zero keeps everything |
| Alert after days | A scheduled job without success for this long raises a *stale* notification |

### Content levels

| Level | Adds per run |
|---|---|
| Files | Nothing |
| Files + database | A fresh `pg_dump` taken **before** any file is read, stored in one *system* backup point |
| Full | Database dump plus the application data folder (`/data/kaimo-system`: Data Protection key ring and certificate, HTTPS certificate, SSH client keys) and an external `DataProtection:CertificatePath` file |

A database or Full job may have no file source at all. Dumping first guarantees that the files of a
run are never older than its database, so a restored database never references version blobs that
are missing from the same run.

The environment secrets (`JWT_SECRET`, `NT_HASH_ENCRYPTION_KEY`, the `.env` file) are not part of any
backup. Keep them separately.

## Snapshot layout

One restic snapshot per source per run; all snapshots of a run share the tag `run:<id>`. A failing
source fails only itself, except repository-level errors (unreachable, wrong password, lock,
insufficient space), which abort the run.

| Kind | Paths in the snapshot | Excluded | Tags |
|---|---|---|---|
| Share | `<share path>`, `/data/kaimo-backups/restic-meta/share-<id>/kaimo-acl-manifest.json` | `.kaimo-*` and (unless included) `.RECYCLE_BIN` at the share root; homes share additionally per home folder | `share:<share id>` |
| Pool | `<pool path>`, `/data/kaimo-backups/restic-meta/pool-<hash>/` with `<share id>-kaimo-acl-manifest.json` per share of the pool | `.kaimo-*` at the pool root (version store re-included when wanted) and in every folder of the pool; recycle bins as for shares | `pool:<folder name>` |
| System | `/data/kaimo-backups/restic-meta/system/` with `kaimo-database.dump` and `kaimo-backup-info.json`; for Full also `/data/kaimo-system` (+ external certificate) | `.kaimo-*` in the application data (restic cache and temp files, SMB snapshot cache) | `kind:system` |

Every snapshot also carries `kaimo`, `job:<job id>`, `run:<run id>` and `level:files|db|full`.
Snapshots without the `kaimo` tag (e.g. a manual CLI backup into the same repository) are listed as
*foreign*.

`kaimo-acl-manifest.json` holds the share definition and every owner and ACL entry with principal ids
and names. `kaimo-backup-info.json` lists the Kaimo version, job, content level, shares (id, name,
path), pools and the application data path. Staging files under `restic-meta` (owner-only) are
deleted after every run and on startup.

### Guards

- A missing source fails with `source_unavailable`.
- A share or pool that had content in earlier backup points but now holds no file outside `.kaimo-*`
  folders fails with `source_empty` — the typical sign of an unmounted pool (Kaimo recreates share,
  internal and home folders there, but never files), which would otherwise produce an "everything
  deleted" backup point that retention then uses to push out the good ones. A source that really
  only holds empty folders after having had files also fails until it holds a file again.
- A failing `pg_dump` fails the system source with `database_dump_failed` (details in the log
  excerpt); the file sources of the run are still backed up.
- Live data is read without file-system snapshots. Files changing during the read may produce
  restic warnings; the run then ends as *succeeded with warnings*.

## Restore

The Restore tab lists the shares the actor may restore. A backup point is browsed folder by folder
(read from the repository with a 90 s timeout per request).

| Mode | Result | Needs |
|---|---|---|
| Download | Single file raw, folder as ZIP, streamed from `restic dump` | `RestoreFromBackup` |
| New folder | `Restored yyyy-MM-dd HHmm` next to the original location | `RestoreFromBackup` |
| Another share | New folder in a chosen share and folder | `RestoreFromBackup` on the source, `RestoreBackupInPlace` on the target |
| In place | Missing entries are created; existing files are overwritten (a file version is kept first), skipped or restored beside them as `name (restored yyyy-MM-dd).ext`; identical files are left alone. **Nothing is ever deleted** | `RestoreBackupInPlace` |
| Restore permissions (option) | Owners and ACLs from the manifest; principals that no longer exist are skipped and listed in the activity log | `RestoreBackupInPlace` on the destination |

restic restores into `<destination share>/.kaimo-restore/<run id>/` (same file system, hidden from
users), then entries are moved into place under the share lock. Afterwards a `SubtreeChanged` entry
in the change log lets search indexing and sync clients pick up the result.

### System and pool backup points

Holders of Global `ManageBackupRepositories` additionally see the section **System & pools**. Such a
backup point is restored, after a confirmation, **as a whole** into a new owner-only folder
`/data/kaimo-backups/restic-restore/<yyyy-MM-dd_HHmmss>/` (`Backup:Restic:RestoreDirectory`); the
snapshot's absolute paths are recreated below it. Nothing live is changed. The folder is shown in the
activity log. While restic writes, the folder is named `<…>.partial` and renamed when complete; a
failed or cancelled restore removes it, and leftovers of a crashed process are removed on the next
start. Remove restored folders when
they are no longer needed — they may contain the database dump with every credential.

- **Database:** start the stack with
  `KAIMO_DB_RESTORE_FROM=/data/kaimo-backups/restic-restore/<folder>/data/kaimo-backups/restic-meta/system/kaimo-database.dump`
  (see [Database backup and restore](database-backup-and-restore.md#restoring-a-backup)). The Host
  container sees the same backup folder. The dump replaces the whole database.
- **Application data:** copy the needed files from `<folder>/data/kaimo-system/` while the stack is
  stopped.
- **Pool content:** copy the needed folders from `<folder>/<pool path>/` into the pool.

## Maintenance and integrity

Per repository on its maintenance schedule (default Sundays 03:00) or "Run maintenance now"; each step
is its own activity entry:

1. `unlock` (stale locks only, never `--remove-all`).
2. `forget` per job (retention; also right after every successful backup) and `prune` — skipped for
   append-only repositories.
3. `check`; the first check of each month also reads one twelfth of the pack data
   (`--read-data-subset <month>/12`), so the whole repository is read once a year.
4. Test restore: a random sample of files (≤ 256 MB each) of the newest backup point per share is
   restored and compared by SHA-256 with the live file, if size and modification time are unchanged.
   The database dump of the newest system backup point is read back (`restic dump`) and must be
   parseable by `pg_restore --list`; a damaged dump raises the test-restore alert.
5. Copy to the mirror repository (skipped if the check failed) and check of the mirror (3-2-1).

Retention groups snapshots by host and paths, so every share and pool of a job keeps its own history.
The system backup points of a job get a second pass grouped by host only: their paths change with the
content level or an external certificate, and an abandoned path group would otherwise be kept forever.
A share renamed or moved to another pool starts a new group; the old group is kept until removed
manually.

## Notifications

Category `filebackup` (rules are seeded disabled; enable them on the Notifications page):
`backup.restic.failed`, `.warnings`, `.stale`, `.check_failed`, `.test_restore_mismatch`,
`.repo_unreachable`, `.maintenance_failed`. Job events go to the job creator and holders of
`ManageBackupJobs`, repository events to holders of `ManageBackupRepositories`.

## Disaster recovery

Prerequisites: a job with content level **Full** and a recent successful run, the recovery kit of its
repository, the backend credentials of the repository and the environment secrets of the old server
(`.env` with `JWT_SECRET`, `NT_HASH_ENCRYPTION_KEY`, …).

1. **Prepare the new server** with the same `docker-compose.yml` and `.env` (same host paths for the
   storage pools). Do not start the stack yet.
2. **Install restic** (the version in the kit or newer) and export the repository:

   ```bash
   export RESTIC_REPOSITORY='<from the kit>'
   export RESTIC_PASSWORD='<from the kit>'
   export AWS_ACCESS_KEY_ID=… AWS_SECRET_ACCESS_KEY=…   # or REST / SSH credentials
   ```

3. **Restore the newest system backup point:**

   ```bash
   restic snapshots --tag kind:system --latest 1
   restic restore <snapshot id> --target /restore
   ```

4. **Put the system data in place** (host paths of the bind mounts):
   - `/restore/data/kaimo-system/*` → the application data folder (`.dp-keys`, `.dp-certificate`,
     `.certs`, `.external-storage`); owner `1654:1654`;
   - `/restore/data/kaimo-backups/restic-meta/system/kaimo-database.dump` → the backup folder
     (`LOCATION_BACKUP`).
5. **Restore the database:** set `KAIMO_DB_RESTORE_FROM=kaimo-database.dump` and start the stack. The
   Host restores the dump, migrates and seeds. Users, shares, permissions, versions rows, settings and
   the backup repositories with their passwords are back; the restored key ring decrypts them.
6. **Restore the files**, newest backup point per pool or share, into their original paths (shown in
   `kaimo-backup-info.json`):

   ```bash
   restic snapshots --tag pool:<pool name> --latest 1
   restic restore <snapshot id>:<pool path> --target <pool path>
   # or per share
   restic restore <snapshot id>:<share path> --target <share path>
   ```

   Then `chown -R 1654:1654` the restored folders and clear `KAIMO_DB_RESTORE_FROM`.
7. **Restart the stack.** Backups and restores from the web interface work again; check the first
   scheduled run in the activity log.

Without a Full job, restore the share backup points the same way and set permissions again from the
`kaimo-acl-manifest.json` of each share (or with "Restore permissions" after re-creating the shares).

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Backup:Restic:Binary` | `restic` | restic executable |
| `Backup:Restic:LocalRoots` | `/data/kaimo-backups/restic` | Folders local repositories may be created in (list or comma-separated) |
| `Backup:Restic:CacheDirectory` | `/data/kaimo-system/.kaimo-restic-cache` | restic cache (grows with repository size) |
| `Backup:Restic:TempDirectory` | `/data/kaimo-system/.kaimo-restic-tmp` | Owner-only SSH keys, SSH password helpers, CA files, GCS keys and rclone configs of running processes (must not be `noexec`) |
| `Backup:Restic:MetaDirectory` | `/data/kaimo-backups/restic-meta` | Staging of manifests, dump and run info (part of the snapshot paths; keep stable) |
| `Backup:Restic:RestoreDirectory` | `/data/kaimo-backups/restic-restore` | Target of system and pool restores |
| `Storage:RootPath` | `/data/storage` | Pools are its direct child folders |
| `Storage:ApplicationDataPath` | `/data/kaimo-system` | Application data included in Full backups |

## Known limits

- One serial lane for all backups and maintenance; restores use their own lane.
- Pool and system backup points are restored only as a whole into the server folder; selective
  restores of pool content go through the restic CLI or by copying from the restored folder.
- A run interrupted by a restart is marked *interrupted*.
- Large in-place restores with "overwrite" create one file version per overwritten file.
