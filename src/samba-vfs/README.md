# samba-vfs — Phase-0-Spike (Proof-of-Concept)

Goal of this directory: **prove that the Samba+VFS approach is viable**, before investing further. See overall plan: [`../../docu/smb-samba-vfs-migration.md`](../../docu/smb-samba-vfs-migration.md).

Phase 0 clarifies the two biggest unknowns:

| # | Unknown | Status |
|---|---|---|
| A | Dynamic shares live via `net conf` (Registry backend), without smbd restart | ✅ **proven** |
| B | Custom VFS module against exact Samba ABI build **and** load from smbd | ✅ **proven** |

**Conclusion Phase 0: the approach is viable.** Both core unknowns are resolved. The path forward
(Phases 1–5) is described in the [overall plan](../../docu/smb-samba-vfs-migration.md).

---

## Step A — Live Registry Shares (Stock Samba)

**Result: works.** A share created via `net conf addshare` appears immediately in
`smbclient -L`, **without** smbd needing to restart; write/read via SMB3 lands directly on
storage. This will later replace the FileSystemWatcher/`SyncFromDb` mechanism from
`src/Kaimo_File_Server.Smb/SmbServer.cs`.

```bash
# Build and start image
docker build -t kaimo-samba-spike:phase0 .
docker run -d --name kaimo-samba-spike -p 1445:445 \
  -e KAIMO_SPIKE_USER=testuser \
  -e KAIMO_SPIKE_PASSWORD_FILE=/run/secrets/spike-password \
  --mount type=bind,src=/secure/path/spike-password,dst=/run/secrets/spike-password,readonly \
  --mount type=bind,src=/secure/path/smbclient-auth,dst=/run/secrets/smbclient-auth,readonly \
  kaimo-samba-spike:phase0

# Self-test (creates share live, writes/reads, removes it again)
docker exec \
  -e KAIMO_SELFTEST_AUTH_FILE=/run/secrets/smbclient-auth \
  kaimo-samba-spike bash /usr/local/bin/selftest.sh
```

The two mounted files must be owned by the container user and must not grant
group or other access. `spike-password` contains only the password on its first
line. `smbclient-auth` uses Samba's authentication-file format (`username = ...`
and `password = ...`). Provision both outside shell history, for example through
the deployment secret manager. The self-test deliberately has no default
identity or password.

Files: [`Dockerfile`](Dockerfile), [`conf/smb.conf`](conf/smb.conf),
[`entrypoint.sh`](entrypoint.sh), [`selftest.sh`](selftest.sh).

Core of the config (`smb.conf`):
```ini
registry shares = yes
include = registry           # smbd reads shares LIVE from registry.tdb
```

---

## Step B — Custom VFS Module

**Result: works.** `smbd` loads our module and the hooks fire on each Connect/Open —
on a dynamically created share via `net conf`:

```
kaimo_bridge: CONNECT service=[hooktest] user=[testuser]    <- TREE_CONNECT
kaimo_bridge: OPENAT  name=[x.txt]                          <- every file open
kaimo_bridge: DISCONNECT
```

These three seams are precisely the later gRPC call points to the .NET control plane
(`CanAccessShareAsync` on connect, ACL decision on open, close hooks for versioning/indexing).
The actual I/O remains native (`SMB_VFS_NEXT_*`) — the file lands directly on storage.

### Two key findings (confirm Risk 3)

1. **`samba-dev` is insufficient.** The distro dev package (4.19.5) does not provide the internal VFS headers
   **at all** (no `vfs.h` with `SMB_VFS_INTERFACE_VERSION`, no `smb_register_vfs`). An
   out-of-tree build against distro headers is impossible → build against the **source tree**.
2. **Module + smbd must come from the same build.** A module built against upstream source
   will fail when deployed to *distro* Samba with
   `libsmbd-base-samba4.so: cannot open shared object file` (Ubuntu renames the private Samba libs).
   → We **build and operate Samba ourselves** (`--prefix=/opt/samba`). This is also the
   later production path, because we must control the Samba version anyway (ABI 49).

### Build & test

```bash
# Default: slim production runtime. Samba source, compiler and waf build tree
# remain in cached intermediate stages and are not part of this image.
docker build -f Dockerfile.vfs -t kaimo-samba-spike:vfs .
docker run -d --name kaimo-samba-vfs -p 1446:445 \
  --mount type=bind,src=/secure/path/smbclient-auth,dst=/run/secrets/smbclient-auth,readonly \
  kaimo-samba-spike:vfs
# Force file op and verify hooks in log
docker exec \
  -e KAIMO_SELFTEST_AUTH_FILE=/run/secrets/smbclient-auth \
  kaimo-samba-vfs bash /usr/local/bin/selftest.sh
docker logs kaimo-samba-vfs 2>&1 | grep "kaimo_bridge:"

# Optional: unstripped native build environment for diagnostics/debugging.
docker build -f Dockerfile.vfs --target build-runtime -t kaimo-samba-build:vfs .
```

Files: [`Dockerfile.vfs`](Dockerfile.vfs), [`module/vfs_kaimo_bridge.c`](module/vfs_kaimo_bridge.c),
[`conf/smb.conf.vfs`](conf/smb.conf.vfs), [`entrypoint.vfs.sh`](entrypoint.vfs.sh).

> The default runtime strips installed ELF files and installs only automatically detected
> shared-library packages. Build `--target build-runtime` when native symbols, compiler,
> Samba sources or waf object files are required.

---

## Phase 1 — Auth (genuine NTLMv2 login against Kaimo user)

**Result: works.** A Kaimo user logs in with their actual password via NTLMv2;
a wrong password is rejected. The NT hashes come live from the Kaimo DB.

**Flow:**

```
 Kaimo-DB ──► SmbBridge (.NET gRPC, :5080 mTLS) ──paged ListUsers──► kaimo_authsync (C++)
                 bounded projected credential batches               │  username + 16-byte NT hash
                 (decrypts, filters, isolates corrupt rows)          ▼
                                                          sync-users.sh ──pdbedit──► Samba's tdbsam
                                                                                        │
                                             smbd verifies NTLMv2 locally against NT hash ─┘
```

- **.NET side:** new project `src/Kaimo_File_Server.SmbBridge` (ASP.NET gRPC). Thin facade over
  the existing `IAuthenticationLookup` — no auth logic duplicated. Needs the same
  `NtHash__EncryptionKey` as Host/Web (set in `docker-compose.override.yml`).
- **C++ side:** `module/authsync.cpp` (gRPC C++ client) + [`sync-users.sh`](sync-users.sh). The
  entrypoint syncs on start (with retries) and then every 60 s. **This also proves gRPC-in-C++ in the
  Samba container** — the last open toolchain risk from Phase 0.
- **P1-15/P1-16 credential convergence:** each run imports through random
  mode-0600 files below `/run/kaimo-user-sync`, then reconciles `tdbsam` to the
  active bridge response. Managed users that disappear lose their passdb entry
  and the `kaimo`/`kaimo-authd` secondary groups. Their POSIX account stays
  locked with `nologin` and keeps its UID so file ownership remains stable;
  reactivation restores Samba access with that UID. The private ownership set
  is stored in `/var/lib/kaimo-user-sync/managed-users`. On its first run the
  sync adopts all existing passdb users unless they are explicitly listed in
  the comma-separated `KAIMO_UNMANAGED_SAMBA_USERS` operator escape hatch.
  Production startup no longer creates or implicitly exempts a test account.
- **P1-17 verified convergence:** all user/share/config reconcilers fail on
  unapplied mutations and read the resulting Samba state back before reporting
  success. `/usr/local/bin/run-sync.sh` serializes each component and publishes
  private last-success/last-failure timestamps; `sync-health.sh` makes missing,
  failed, or stale convergence fail container health (180-second default,
  configurable with `KAIMO_SYNC_HEALTH_MAX_AGE_SECONDS`).
- **P2-17 lock ownership:** the runner retains its component lock through
  result publication but closes the lock descriptor in the reconcile command,
  preventing `wsdd` or another background descendant from retaining it.
  A concurrent cycle is skipped without disconnecting clients; it publishes no
  false success, so the normal freshness limit still detects a hung owner.
- **P1-18 structured records:** the three C++ exporters emit bounded version-1
  JSON documents. The shell reconcilers independently require exact schemas,
  types, unique safe names, exact NT hashes, valid protocol ranges, and share
  paths canonically contained below `KAIMO_STORAGE_ROOT`/`KAIMO_STORAGE`.
- **P2-10 bounded credential export:** `ListUsers` reads active credential
  sources in ordered database projections of at most 1,000 rows. Each page is
  independently bounded and each credential must have a valid Samba username
  plus an exact 16-byte decoded NT hash; corrupt rows are counted and skipped
  without exposing their username or hash in logs. Continuations require a
  short-lived, client/offset-bound authenticated token, so non-zero offsets
  cannot bypass the logical export's rate-limit permit. `kaimo_authsync`
  validates monotonic offsets, continuation-token shape, the
  100,000-source-row ceiling, and the existing 16-MiB JSON ceiling. It emits no
  desired-state JSON until every page has completed, so `sync-users.sh` never
  reconciles a partial export.
- **P2-11 minimized credential lifetime:** the bridge decrypts directly to raw
  bytes, clears rejected values and source arrays after protobuf copies, and
  avoids an additional managed plaintext hash string. `kaimo_authsync` retains
  hashes in move-only fixed buffers, clears protobuf fields after copying, and
  wipes retained bytes on move/destruction. The shell no longer captures the
  JSON export in a variable: random mode-0600 JSON, validated-record, and
  `smbpasswd` files are unlinked immediately after their respective validation,
  assembly, and import phases. Compose backs `/run/kaimo-user-sync` with a
  root-owned 0700 `tmpfs` (`noexec,nosuid,nodev`), and no credential is placed
  in an argument, environment variable, log, or durable managed-user state.
  Protobuf/serializer, pipe, `jq`, `pdbedit`, and Samba memory remain
  necessarily credential-bearing only for their bounded operation lifetime.
- **P2-12 fail-fast sidecar supervision:** the entrypoint hands PID 1 to
  `supervise-samba.sh`. It starts `kaimo_authd`, waits at most five seconds for
  the protected Unix socket, publishes root-owned mode-0600 PID files, and
  starts `smbd` only afterward. If either long-running process exits, the
  supervisor terminates and reaps the peer, removes readiness state, and exits
  non-zero. Container signals are forwarded to both processes with a bounded
  five-second shutdown grace. `authd-health.sh` validates the socket and authd
  identity; `smbd-health.sh` validates the smbd PID file, process identity, and
  `smbcontrol smbd ping`. Compose uses
  `restart: unless-stopped`, so a sidecar crash restarts the complete Samba
  security unit rather than leaving `smbd` alive in permanent fail-closed
  degradation.
- **P2-14 credential-free operations:** production startup does not create a
  reusable test account. Container health combines authd identity/readiness,
  smbd identity/control-plane responsiveness, and synchronization freshness
  without performing an authenticated SMB login. The former audit login probe
  was removed; audit operation compatibility belongs to the pinned Samba
  ABI/release test matrix. Manual protocol tests require an explicit,
  owner-only `KAIMO_SELFTEST_AUTH_FILE` and call `smbclient -A`, keeping the
  password out of process arguments and environment variables.
- **P2-01 exact connection context:** usernames and share names are stored as
  exact owned strings instead of fixed arrays. Account/share creation, VFS,
  `authd`, and every identity-bearing bridge RPC enforce the same 32-byte
  username and 64-byte share-name constraints before any lookup or mutation.
  Validation finishes before any Samba/POSIX mutation.
- **Proto contract:** [`protos/kaimo_smb_bridge.proto`](protos/kaimo_smb_bridge.proto) — defined once,
  generates C# (Bridge) and C++ stubs (authsync).
- **P0-07 control-plane security:** the bridge accepts only client certificates
  from its private CA. The Samba container uses one `kaimo-samba` workload
  identity because its helper processes share one container and credential
  mount. The bridge still has an explicit allow-list of required RPC methods;
  `GetNtHash` is intentionally not allowed. Bulk hash exports are rate-limited
  and audited without logging hashes or usernames. Compose places bridge/Samba
  on a dedicated internal network and mounts no control-plane credentials into
  Web or Adminer.

**Test (short form):**
```bash
docker compose up -d kaimo_smb_bridge kaimo_samba
docker compose exec kaimo_samba bash -lc '\
  export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH; \
  smbclient -L localhost -A /run/secrets/smbclient-auth -m SMB3'
```
> Prerequisite: provision a root-owned mode-0600 authentication file with a
> deliberately selected test identity and mount it at
> `/run/secrets/smbclient-auth`. Negative-password tests should use a separate
> short-lived authentication file; do not put passwords in the command line or
> environment.

**Open for Phase 2:** Authorization (share access, ACL on open) doesn't run via gRPC yet —
the VFS hooks (`connect`/`openat`) only log for now. Next they will call
`CanAccessShareAsync` / `FileService.OpenAsync` via the same bridge.

## Phase 2a — Connect Authorization (genuine Kaimo ACLs decide share access)

**Result: works.** On TREE_CONNECT, the genuine Kaimo ACL decides whether a user can
enter the share — same semantics as the earlier `KaimoSharePolicy.AuthorizeConnect`.

**Flow:**

```
 smbd VFS connect hook (kaimo_bridge.so, pure C)
   │  Unix socket: binary v1 frame (op=CONNECT, length-prefixed user/share)
   ▼
 kaimo_authd (sidecar, C++)  ──gRPC AuthorizeConnect──►  SmbBridge (.NET)
   │  framed ALLOW / DENY status                            CanAccessShareAsync(shareId, userId)
   ▼
 allow -> SMB_VFS_NEXT_CONNECT   |   deny -> errno=EACCES, TREE_CONNECT fails
```

- **Why a sidecar?** This keeps the smbd VFS module as pure C (just a socket roundtrip) — no
  gRPC/threads/fork in the smbd process. The gRPC complexity is encapsulated in `kaimo_authd`
  ([`module/authd.cpp`](module/authd.cpp)).
- **.NET:** [`AuthzGrpcService`](../src/Kaimo_File_Server.SmbBridge/Services/AuthzGrpcService.cs) resolves
  user/share names to GUIDs and calls `IAuthenticationLookup.CanAccessShareAsync`.
- **IPC$** is always allowed (share enumeration).

**Verified (dept-based allow/deny, matches Kaimo ACLs exactly):**

| User (Department) | Share | Decision |
|---|---|---|
| marco.hanisch (Frontend) | frontend-docs | **ALLOW** |
| marco.hanisch (Frontend) | marketing-files | **DENY** |
| lisa.mueller (Marketing) | marketing-files | **ALLOW** |
| lisa.mueller (Marketing) | frontend-docs | **DENY** |
| anna.weber (Backend) | backend-docs | **ALLOW** |
| admin (management role only) | every share | **DENY** |

> **Note about admin:** `admin` has the "Administrator" management role, but **no file ACLs** on
> the shares. `CanAccessShareAsync` therefore denies — **just like the old SMB stack** (faithful
> parity, not a bug). File access is granted via ACLs/departments, not management roles.

**Fail behavior:** If the sidecar/bridge is unreachable, the module **denies by default**
(fail-closed — a bridge outage must not silently grant access). Set `KAIMO_AUTHZ_FAILOPEN=1`
to allow on error instead (availability over security), which was the previous default.

**VFS-side deadlines:** Every local authorization roundtrip uses one absolute
monotonic budget across nonblocking `connect`, complete request transmission,
and complete response reception. The defaults are 6000 ms for authorization
(`KAIMO_VFS_AUTH_TIMEOUT_MS`) and 32000 ms for snapshot operations
(`KAIMO_VFS_SNAPSHOT_TIMEOUT_MS`). Best-effort lifecycle notifications do not
wait for a gRPC result and have an independent 250 ms local enqueue budget
(`KAIMO_VFS_EVENT_TIMEOUT_MS`). Accepted values are 10-60,000 ms; invalid
values fall back to the bounded defaults and are logged.

**Windows-compatible denial:** The pinned Samba build carries a narrow patch that
maps the VFS hook's `EACCES` to `NT_STATUS_ACCESS_DENIED`. Without it, Samba
hardcodes `NT_STATUS_UNSUCCESSFUL`, which Windows renders as "A device attached
to the system is not functioning" and may retry as a transient error. The
focused `test-vfs-connect-status.py` regression test verifies the denial status
and a sub-1.5-second response.

Windows can offer its normal credential dialog for `ACCESS_DENIED` when no
existing SMB session fixes the identity. Windows still permits only one username
per server name at a time; switching users while another share on the same host
is connected requires disconnecting that session or using a separate DNS alias.

**Implemented in Phase 2b:** File/path, delete, and rename authorization plus the
directory listing filter. Native runtime verification of the latest hardening revisions
remains pending.

## Phase 2b — File/path ACL + listing filter (Phase 2 complete)

**Result:** the managed access-mask mapping is implemented and covered by automated
tests; native Samba runtime verification of the P0-03 revision remains pending.
File opens and directory listing use genuine Kaimo ACLs without reducing SMB access
to read/write booleans.

- **`create_file` hook** ([vfs_kaimo_bridge.c](module/vfs_kaimo_bridge.c)) → gRPC `AuthorizeOpen`.
  The correct seam (not `openat`): full path + raw SMB desired-access mask. The bridge
  expands Samba 4.19.5 generic rights, checks data/list, write, append, traverse,
  attributes, EA, security-descriptor, owner, delete, and delete-child permissions,
  then returns the exact specific mask that the VFS passes to Samba. `MAXIMUM_ALLOWED`
  is attenuated to Kaimo-granted rights instead of being recalculated from the broad
  POSIX service identity. Missing file creation requires parent `CreateWriteData`;
  missing directory creation requires parent `CreateAppendData`.
- **`readdir` hook** → hides entries without read permission (`AuthorizeOpen` read-only per entry).
  Listing requests are marked separately so Samba's implicit FSP `ReadAttributes`
  behavior is not applied to visibility checks. The **sidecar caches** decisions and
  granted masks (TTL 3 s) so large listings don't flood the bridge.
  Disabled with `KAIMO_LIST_FILTER=0`.
- **`renameat` hook** → gRPC `AuthorizeRename` before mutation. The bridge requires
  source `Delete` (or source-parent `DeleteSubItems`), file/directory-appropriate create
  permission on the destination parent, and deletion permission for an existing replacement.
  The VFS validates source/destination inode and type both before and after the RPC to
  reject stale or exchanged directory entries.

**Verified:**

| Case | Result |
|---|---|
| lisa (Marketing = read-only) writes to marketing-files | **DENY** (`create_file` → `CreateWriteData`) |
| marco (Development = write) writes to frontend-docs | **ALLOW**, file on disk |
| marco reads file with explicit deny ACL | **DENY** (`ACCESS_DENIED`) |
| marco lists directory with deny file | File **invisible** (listing filter), but physically on disk |

> **Important — storage mount:** The bridge needs the same `/data/storage` mount (existence/type
> check as `OpenAsync`). It is set in [`../docker-compose.yml`](../docker-compose.yml). Without
> it, the bridge treats existing files as "not found" and allows too much.

**Still open for later phases:** Snapshots/@GMT (Phase 5) and recycle
bin/versioning/search index on close hooks (Phase 3).

## Phase 3 — Close hooks (versioning, ownership, search index)

**Result: works.** Samba performs the file I/O natively and then reports the event; the
bridge handles the same cross-cutting effects as earlier `FileSession.DisposeAsync`.

**Flow:**

```
 smbd VFS hook (pure C)            Sidecar (kaimo_authd)        SmbBridge (.NET)
  close_fn (descriptor capture)──framed CLOSE──► NotifyClose ──► FileService.NotifyExternalCloseAsync
  unlinkat_fn(deleted)         ──framed DELETE─► NotifyDelete ─►   → Version (CreateVersionAsync)
  renameat_fn(renamed)  ──framed RENAME_AUTH─► AuthorizeRename
                        ──framed RENAME───────► NotifyRename ─►   → Ownership (EnsureOwnerAsync)
  mkdirat_fn (directory created) ──framed MKDIR──► NotifyMkdir ─►   → Search index (SearchServiceRouter)
                                      (durable retry + ack)         → ACL realignment (Rename)
```

- **.NET:** new `FileService.NotifyExternal{Close,Delete,Rename,Mkdir}Async` (in Core) use the
  **already wired** version/ownership/search services — identical results as web uploads.
  Facade: [`FileEventGrpcService`](../src/Kaimo_File_Server.SmbBridge/Services/FileEventGrpcService.cs).
- **Sidecar** uses a fixed worker pool (`KAIMO_AUTHD_WORKERS`, default 16)
  and a bounded accepted-client queue (`KAIMO_AUTHD_QUEUE_CAPACITY`, default
  64), so slow events do not create unbounded threads or descriptors. Excess
  clients receive `ERROR`; silent clients are closed after
  `KAIMO_AUTHD_IO_TIMEOUT_MS` (default 2000 ms).
- **Durable event delivery (P1-11):** `authd` assigns a stable UUID, writes the
  complete framed event to the owner-only `KAIMO_EVENT_SPOOL_PATH` using
  `fsync` + atomic `rename`, and only then acknowledges the VFS. A separate
  dispatcher requires both successful gRPC status and `NotifyReply.ok`, retries
  with bounded exponential backoff, and moves exhausted events to the bounded
  `dead/` directory. The spool is a dedicated Compose volume, so it survives
  container recreation. The bridge leases each ID in
  `samba_lifecycle_event_receipts`; completed duplicates are acknowledged
  without re-running effects, crashed leases expire, and completed receipts are
  retained for `LifecycleEvents:ReceiptRetentionDays` (default 30).
- **Exact close content (P1-12):** before the native descriptor is released,
  the VFS publishes a read-only `.kaimo-close-captures/<id>.cap` from that
  descriptor (`FICLONE` where supported, stable checked copy otherwise).
  Versioning and indexing open this capture, never the live pathname. `authd`
  keeps it across delivery retries/dead-lettering and removes it only after the
  bridge acknowledges the stable event ID. Share sync provisions the reserved
  root as `root:<storage-gid>` mode `2770`; all client-visible `.kaimo-*` paths
  are denied by the VFS.
- **Local peer security:** `/var/run/kaimo` is `root:kaimo-authd` mode `0750`
  and `authz.sock` is mode `0660`. Synchronized Samba users are members of the
  dedicated group, while `authd` authenticates every connection with
  `SO_PEERCRED`. Non-root callers may only name the passwd user matching their
  kernel UID. Root-real-ID Samba workers are accepted as session-identity
  carriers only when they match the configured trusted `smbd` executable
  identity; unauthorized peers fail closed even if `KAIMO_AUTHZ_FAILOPEN=1`.
  The group and executable can be set with `KAIMO_AUTHD_GROUP` and
  `KAIMO_AUTHD_PEER_EXECUTABLE`.
- **Local wire protocol:** [`local_protocol.h`](module/local_protocol.h)
  defines a 12-byte `KAIM` envelope with protocol version, enum operation,
  request/response kind, structured status, and a big-endian payload length.
  Requests are capped at 8 KiB and responses at 64 KiB before payload reads or
  allocations. Every variable field is a length-prefixed, NUL-free UTF-8
  string; exact schema consumption rejects missing or trailing fields.
  Both C and C++ endpoints use complete read/write loops, so Unix stream
  fragmentation and partial I/O cannot change message boundaries.
- **VFS client deadlines:** the module opens its local client socket as
  nonblocking and uses a single monotonic deadline for connect, all partial
  writes, and all partial reads. Authorization, snapshot, and durable event
  traffic have separate budgets, so a stalled sidecar cannot indefinitely pin
  an `smbd` worker and event enqueue cannot consume an authorization-sized
  timeout.
- **Authorization cache:** open decisions use a mutex-protected LRU capped by
  both entry count (`KAIMO_AUTHD_CACHE_MAX_ENTRIES`, default 10,000) and an
  accounted memory budget (`KAIMO_AUTHD_CACHE_MAX_BYTES`, default 8 MiB).
  Expired entries are removed on lookup and by an opportunistic sweep on the
  first cache operation after each one-second interval; oversize keys are not
  cached, and hit/miss/occupancy/eviction/skip counters are sampled into the
  sidecar log.
  `KAIMO_AUTHD_CACHE_TTL_MS` defaults to 3000 ms and is the documented
  maximum ACL-revocation delay for a cached decision (accepted range
  100–10,000 ms).
- **Recycle bin:** SMB and Web use the same per-share `IsRecycleEnabled`
  setting and `.RECYCLE_BIN` namespace. `AuthorizeDelete` returns the delete
  disposition to the VFS. An enabled SMB delete is atomically renamed to
  `.RECYCLE_BIN/<original-path>` with collision suffixes; the exact destination
  is delivered through the durable rename-event path so ACL, version,
  ownership, and search metadata follow the file. Deleting an entry already
  below `.RECYCLE_BIN`, or deleting while the setting is disabled, performs a
  permanent native `unlinkat`/`rmdir`.

**Verified:**

| Event | Result |
|---|---|
| marco writes file → closes | `file_versions` snapshot created (**versioning** ✅) |
| same file | `file_metadata.OwnerId = marco` (**ownership** ✅) |
| delete file | `NotifyDelete` fired → deindex path ✅ |
| rename file/directory | `NotifyRename` fired with source type → ACL realignment + matching file/directory index lifecycle ✅ |

**Known points:**

- **Search index:** Indexing runs via the same `SearchServiceRouter` as the host. The router
  gates Elasticsearch via a reachability ping; on this system ES indexing is currently
  **system-wide inactive** (the host also hasn't indexed anything new since July 9 — independent of
  this migration). The bridge therefore behaves **parity-faithfully**. Once ES indexing is active again,
  SMB writes will be indexed like web uploads.
- **Concurrent writers:** a close capture is taken from the closing descriptor.
  The copy fallback rejects a file whose inode/size/timestamps change during
  capture; a later writer's subsequent close produces its own event.
- **`mkdirat` hook** doesn't fire reliably in Samba's SMB2 directory creation path (directories
  apparently aren't always created via `mkdirat_fn`) — minor edge gap, file ops are complete.

## Phase 4 — Dynamic shares (Registry provisioning from Kaimo DB)

**Result: works.** Shares *enabled* in the Kaimo DB appear automatically in
Samba — without smbd restart — and create/rename/delete/disable via the web UI takes effect live. This
replaces the FileSystemWatcher/`SyncFromDb()` mechanism from
`src/Kaimo_File_Server.Smb/SmbServer.cs`.

**Flow (mirror of NT hash sync from Phase 1):**

```
 Kaimo-DB ──► SmbBridge (.NET gRPC, :5080) ──gRPC ListShares──► kaimo_sharesync (C++)
                 IShareRepository.GetAllEnabledAsync                 │  versioned JSON envelope
                 (enabled shares only)                               ▼
                                                          sync-shares.sh ──net conf──► registry.tdb
                                                            (add/setparm/delshare)        │
                                                    smbd reads shares LIVE from registry ──┘
```

- **.NET:** [`ShareGrpcService`](../src/Kaimo_File_Server.SmbBridge/Services/ShareGrpcService.cs) —
  thin facade over `IShareRepository`; no share logic duplicated (repository already filters disabled shares).
  Every Samba-facing authorization, lifecycle-event, and snapshot RPC also
  resolves its share through the central `ResolveEnabledShareAsync` gate.
  Registered in [`Program.cs`](../src/Kaimo_File_Server.SmbBridge/Program.cs).
- **C++:** [`module/sharesync.cpp`](module/sharesync.cpp) (gRPC client) +
  [`sync-shares.sh`](sync-shares.sh). The entrypoint requires user, share, and
  configuration convergence before starting Samba. It then reconciles at the
  independently configured `KAIMO_*_SYNC_INTERVAL_SECONDS` values. Share and
  configuration intervals must be 1-5 seconds and default to 2 seconds. User
  sync is fixed at 60 seconds because it is the
  rate-limited, hash-bearing credential export.
- **Reconciliation** in `sync-shares.sh` is idempotent: new shares → `net conf addshare`,
  changed (path/visibility) → `net conf setparm`, removed/disabled → `net conf delshare`.
  A path change or removal additionally runs `smbcontrol smbd close-share` so an
  existing client cannot remain attached to the old service path. `global` is
  never touched.

#### User, share, and service revocation semantics

- Once the disabled state is committed, every new bridge authorization,
  lifecycle-event, and snapshot request fails closed on its next database
  lookup; it does not wait for Samba registry reconciliation.
- Under healthy operation, the next relevant reconciliation begins within its
  configured interval after the commit becomes visible. The default target is
  2 seconds plus runtime for share/service state and 60 seconds plus runtime
  for user state.
- Reconciliation removes the registry share first and then calls
  `smbcontrol smbd close-share`, which forcibly disconnects every active tree
  connection for that share. Clients must reconnect after it is enabled again.
- Global service disable closes every registry share. Disabled/deleted users
  lose passdb credentials and Kaimo groups before every registry share is
  closed. Samba 4.19 has no reliable username-selective close primitive, so
  this deliberately disconnects unaffected clients to guarantee that the
  revoked identity retains no session or open handle.
- A failed periodic export or mutation makes local state uncertain and closes
  every registry share. New operations remain protected by fail-closed VFS
  authorization. If the global close cannot be proven, PID 1 is terminated so
  the P2-12 supervisor and Compose restart the complete Samba security unit.
- Arbitrary ACL edits are not a desired-state polling event. Subsequent
  authorization operations observe them within the separate authd cache TTL,
  but handles already opened under the former ACL are not selectively closed.
- A shorter user-revocation target requires a separate hash-free identity
  revision/invalidation feed. Raising the frequency of the NT-hash export would
  defeat its two-per-60-second rate limit and unnecessarily extend credential
  exposure.

### Visibility (ABE) — Decision: hidden flag only

`IsShareHidden` → `browseable = no` (the share disappears from listing but remains directly
accessible via `\\host\share`). The **hard** share access is decided unchanged by the Phase 2a
`connect` hook based on genuine Kaimo ACLs. Full per-user ABE (`valid users` per share)
was deliberately **not** implemented — it would overlap with the `connect` hook and duplicate
ACL logic. Details/rationale: [Risk 2 in overall plan](../../docu/smb-samba-vfs-migration.md#risk-2--dynamic-share-visibility-per-user--trickiest-point).

**Test (after web UI/DB contains shares):**
```bash
docker compose up -d kaimo_smb_bridge kaimo_samba
docker compose exec kaimo_samba bash -lc '\
  export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH; \
  /usr/local/bin/sync-shares.sh;               # Mirror registry from DB
  net conf listshares;                         # -> the enabled Kaimo shares
  smbclient -L localhost -A /run/secrets/smbclient-auth -m SMB3' # -> shares
```

### Protocol settings from Kaimo DB (`ISmbConfigStore`)

**Result: works.** The SMB protocol/security options maintained via web UI
(dialect range, signing, encryption) land in Samba's global config — the same thing
`SmbServer.LoadProtocolSettings()` used to feed the old .NET SMB server on (re)start.

**Flow** (similar to share sync):

```
 Kaimo-DB ──► SmbBridge (:5080) ──gRPC GetProtocolSettings──► kaimo_configsync (C++)
                 ISmbConfigStore.GetProtocolSettingsAsync         │  min⇥max⇥signing⇥encrypt
                 (fresh, no cache)                                ▼
                                              sync-config.sh ──net conf setparm global──► registry.tdb
                                                (only on change: smbcontrol smbd reload-config)
```

- **Mapping** (in the bridge, so the shell remains Samba-agnostic): dialect enum → `server min/max
  protocol` (`SMB2_02`…`SMB3_11`); `RequireSigning` → `server signing = mandatory|auto`;
  `RequireEncryption` → `smb encrypt = required|default`.
- **Canonical registry verification:** Samba 4.19.5 accepts `smb encrypt` for
  `net conf setparm` but exposes the persisted global key as `server smb
  encrypt`. The reconciler therefore uses the accepted input name for mutation
  and the canonical key for both its idempotency comparison and mandatory
  read-after-write verification.
- **Precedence:** In [`conf/smb.conf.vfs`](conf/smb.conf.vfs), `include = registry` is **at the end** of
  the `[global]` section so DB values set via `net conf` override inline fallback defaults.
  smbd reads them on start or after `smbcontrol smbd reload-config` (new connections only; existing ones stay).
  The config sync triggers the reload **only on actual change**.
- **Deliberately not synced:** WS-Discovery and audit log have no global
  smb.conf parameters in Samba (separate mechanisms: `wsdd` and the `full_audit` VFS; the Kaimo VFS module
  logs connect/open/close itself anyway).

**Test:**
```bash
docker compose exec kaimo_samba bash -lc '\
  export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH; \
  /usr/local/bin/sync-config.sh; \
  net conf getparm global "server min protocol"; \
  net conf getparm global "server signing"; \
  net conf getparm global "server smb encrypt"'
```

### Known issue — SMB write permissions (storage ownership)

**Symptom:** Read via SMB works, **write fails** with `ACCESS_DENIED` (web UI writes normally).
Root cause: Samba does file I/O **as the authenticated Unix user** (each Kaimo user gets a POSIX account
with their own UID via `sync-users.sh`). But share directories are created by the
**Host/Web container** — it runs as `$APP_UID` (**1654**, the standard `app` user of .NET images)
and creates them with mode `0755`. So only UID 1654 can write; SMB users (other UIDs) can only read.
The Kaimo ACL even says ALLOW — only the kernel throws `EACCES`.

> **Dead ends (don't use):** Samba's `force user` **and** "all users share same UID"
> do solve write permissions, **but destroy Samba's per-user identity** (SID is algorithmically
> derived from UID, plus `getpwuid` lookups) → all users collapse to one principal →
> **auth/connect breaks**. Both were tried and abandoned.

**Fix (implemented):** Keep per-user identity (distinct UIDs), solve write permission via
**shared group + group-writable storage**. Specifically:

- [`entrypoint.vfs.sh`](entrypoint.vfs.sh) creates the `kaimo` group with storage GID on start
  (`KAIMO_STORAGE_GID`, default **1654**), sets share directories to `2775` (setgid + g+w) and
  applies the group/permissions recursively (internal dot-dirs `.dp-keys`/`.certs` are exempted).
- [`sync-users.sh`](sync-users.sh) adds **each** synced Kaimo user to the group via `usermod -aG` —
  as a **secondary** group, primary UID/group (and thus SID/identity) stays.
- [`sync-shares.sh`](sync-shares.sh) sets newly provisioned shares directly to group + `2775`.
- [`conf/smb.conf.vfs`](conf/smb.conf.vfs) enforces group-writable new objects:
  `create mask = 0664` / `force create mode = 0060` / `directory mask = 2775` / `force directory mode = 0070`.

`force user` / shared UIDs remain taboo (see dead ends above). Verified: SMB write as
`marco.hanisch` on a `0755` share (`crazyFrog`) → before `ACCESS_DENIED`, after file with mode
`0664`, group `kaimo`.

**Remaining gap:** POSIX ACLs are **not** available on this setup's storage filesystem (`setfacl` fails),
so the default ACL path doesn't work. For files **created later by web** (UID 1654),
group write permission still depends on the web/host container's umask — with `022` they become
`0644` (group read-only), so SMB can read them but not overwrite. Fix: **umask `002`** in web/host
container. Files already on disk were touched once at rollout to `g+rwX` and are uncritical.

## Phase 5 — Snapshots (@GMT) & Cutover

**Result: implemented and validated against the pinned Samba 4.19.5 build.** Two parts:

### @GMT "Previous Versions" over SMB

Because Kaimo stores versions as gzip-compressed, content-addressed blobs (not
filesystem snapshot dirs), native `vfs_shadow_copy2` cannot serve them. The custom
module does it via the bridge instead:

```
 Windows "Previous Versions" tab
   │  FSCTL_SRV_ENUMERATE_SNAPSHOTS          open "@GMT-…\file" (smb_fname->twrp set)
   ▼                                          ▼
 kaimo_bridge.so get_shadow_copy_data      kaimo_bridge.so create_file/stat (twrp)
   │  SNAPENUM                                │  SNAPRESOLVE
   ▼                                          ▼
 kaimo_authd ──gRPC EnumerateSnapshots──►   kaimo_authd ──gRPC ResolveVersion──►
                SmbBridge (.NET)                            SmbBridge (.NET)
                GetSnapshotTimestamps/GetVersions           GetVersionAt + ReadVersion
                                                            → ACL-filter + materialize into
                                                              /data/kaimo-system/.kaimo-snapshots/<share-id>/@GMT-…/<user-id>/
   labels (@GMT tokens)                        base_name rewritten to that copy → native read
```

- **Enumeration** (`get_shadow_copy_data_fn`) returns the `@GMT-` labels for the
  file. **Resolution**: a timewarp open/stat (`smb_fname->twrp`) is turned into an
  `@GMT-` token, the bridge materializes that one version **decompressed** into the
  global internal cache (`/data/kaimo-system/.kaimo-snapshots/<share-id>/@GMT-…/<user-id>/<relpath>`)
  outside every Samba connectpath. The bridge returns only a cache-root-relative
  path; the VFS validates all components and joins its independently configured
  absolute root before redirecting the open.
- **Enumeration bound:** the local protocol accepts at most 2,048 labels in the
  exact 24-byte `@GMT-yyyy.MM.dd-HH.mm.ss` form. The resulting maximum payload is
  57,348 bytes, below the 65,536-byte response-frame limit; compilation fails if
  those constants ever become inconsistent. The bridge formats, deduplicates,
  and orders labels newest-first, returning the newest 2,048 and warning when
  older entries are omitted. `kaimo_authd` independently rejects oversized or
  malformed gRPC results before serialization; the VFS verifies the count,
  every length-prefixed token, exact record count, and end of payload before
  allocating Samba label storage. Invalid responses yield no snapshots, never
  a partially parsed list.
- **ACL parity:** concrete files require `ListReadData`; folders go through
  `IFileService.GetFolderSnapshotAsync`, which checks the directory and batch-filters
  every historical child. Before returning a folder, the bridge removes stale files
  from that user's projection, including files revoked after earlier materialization.
- **Cache isolation:** `Snapshots:Cache:RootPath` and
  `KAIMO_SNAPSHOT_CACHE_ROOT` must match. Resolution fails closed if that root
  overlaps a share; `sync-shares.sh` also refuses to publish an overlapping share.
  The legacy top-level `.kaimo-snapshots` name remains denied in client-facing VFS
  path hooks while the cleanup service removes recognizable old cache trees.
- **Validated cache policy:** the bridge validates cache settings before it
  starts serving. TTL must be 1 minute–365 days, the cleanup sweep interval
  1 minute–24 hours, and the per-share cap 1 MiB–100 TiB. Configure them with
  `KAIMO_SNAPSHOT_CACHE_TTL_HOURS`,
  `KAIMO_SNAPSHOT_CACHE_SWEEP_MINUTES`, and
  `KAIMO_SNAPSHOT_CACHE_MAX_BYTES_PER_SHARE`; zero, negative, non-finite, and
  out-of-range values fail startup.
- **Bounded folder materialization:** before any projection mutation, the bridge
  reserves one process-wide concurrency slot and validates the ACL-filtered
  snapshot against configurable file and byte limits. A strict request timer
  covers metadata lookup, ACL filtering, cache validation, decompression, hashing,
  and publication. Cancellation removes newly created projection files and every
  same-directory temporary file. Compose exposes
  `KAIMO_SNAPSHOT_MAX_FILES`, `KAIMO_SNAPSHOT_MAX_REQUEST_BYTES`,
  `KAIMO_SNAPSHOT_MAX_CONCURRENT_REQUESTS`, and
  `KAIMO_SNAPSHOT_MAX_DURATION_SECONDS`; defaults are 10,000 files, 1 GiB,
  two concurrent requests, and 25 seconds.
- **.NET:** [`SnapshotGrpcService`](../src/Kaimo_File_Server.SmbBridge/Services/SnapshotGrpcService.cs)
  — thin facade over the already-complete `IFileVersionService`.
- **C/C++:** `get_shadow_copy_data_fn` + `stat`/`lstat` + `create_file` twrp branch
  in [`vfs_kaimo_bridge.c`](module/vfs_kaimo_bridge.c); `SNAPENUM`/`SNAPRESOLVE`
  handlers in [`authd.cpp`](module/authd.cpp).
- **Strict read-only behavior:** timewarp opens reject write/create/truncate/
  append/delete-on-close and metadata mutation intent, attenuate granted access
  to read/execute rights, and redirect through `SMB_VFS_NEXT_OPENAT`. Delete,
  rename, and mkdir against a timewarp path fail with read-only-filesystem
  semantics. Disabling the redirect fails closed rather than exposing live data.

> **Validation status:** the .NET side is build- and unit-tested. The real C VFS
> module compiles against Samba 4.19.5/ABI 49. A live SMB3 regression reads
> historical content, observes the downstream `full_audit` hook, and proves
> overwrite, delete, rename, and mkdir return `NT_STATUS_MEDIA_WRITE_PROTECTED`
> without changing live or cached bytes. The Windows Explorer "Previous
> Versions" dialog and representative folder browsing remain manual production
> compatibility checks.

### Cutover

- **Old library removed:** the whole `Kaimo_File_Server.Smb` project (in-process
  SMB server, `KaimoFileStore`, `KaimoSharePolicy`, `SmbSync`, …) and its two unit
  test files are deleted; references dropped from the solution, `Host`, and tests.
- **On/off toggle:** the "Datendienste" flag (`services.smb.enabled`) is now
  enforced by the bridge's `AuthorizeConnect` — when disabled it **denies every
  TREE_CONNECT**, so no share is enterable (smbd keeps listening). The host runs a
  tiny [`SambaSmbControlService`](../src/Kaimo_File_Server.Host/SambaSmbControlService.cs)
  so the reconciler still reflects Running/Stopped status in the UI without an
  in-process server. The state is also surfaced to the container via
  `GetProtocolSettings.enabled` → `sync-config.sh` (log only).
- **Port 445** belongs to `kaimo_samba` in [`../docker-compose.yml`](../docker-compose.yml);
  the host no longer serves SMB.

## Deployment via docker compose

The spike is integrated as service `kaimo_samba` in the central [`../docker-compose.yml`](../docker-compose.yml)
— `docker compose up` brings it up, **without** disturbing the existing .NET SMB implementation:

```bash
cd ..                       # into the docker-compose.yml directory
docker compose up -d kaimo_samba          # Samba only
# or everything together:
docker compose up -d
```

- **PKI:** local certificates default to `./secrets/smb-control-plane` and are
  generated automatically by the one-shot `kaimo_smb_pki_init` service and are
  git-ignored. Production should set `KAIMO_SMB_CONTROL_PKI` to an externally
  managed directory with `bridge/{ca.crt,server.crt,server.key}` and
  `samba/{ca.crt,samba.crt,samba.key}` and rotate the private CA/leaf identities
  operationally.
- **Port:** Samba owns host/container port **445** after the Phase-5 cutover.
- **Storage:** same bind mount `./tests/data/storage:/data/storage` as host/web → Samba does file I/O directly.
- **Healthcheck:** reports `healthy` only when the protected authd and smbd PID
  files identify the expected live processes, authd's private socket is ready,
  `smbcontrol smbd ping` succeeds, and synchronization is current. It does not
  create or use a reusable SMB account.

Manual protocol test after startup (requires a separately provisioned,
root-owned mode-0600 authentication file inside the container):
```bash
docker compose exec \
  -e KAIMO_SELFTEST_AUTH_FILE=/run/secrets/smbclient-auth \
  kaimo_samba bash /usr/local/bin/selftest.sh
docker compose logs kaimo_samba | grep "kaimo_bridge:"
```

> The first build compiles Samba from source (~7 min). After that, BuildKit layer cache takes effect.

## Important facts from Phase 0

- Samba runtime version: self-built upstream **4.19.5** on Ubuntu 24.04.
- **`SMB_VFS_INTERFACE_VERSION = 49`** — the VFS module must be built against exactly this ABI.
- VFS module directory: `/usr/lib/x86_64-linux-gnu/samba/vfs/`.
- Module init symbol: `vfs_kaimo_bridge_init` → registered under the name `kaimo_bridge`.
- Build registration: `bld.SAMBA3_MODULE('vfs_kaimo_bridge', subsystem='vfs', …)` in
  `source3/modules/wscript_build`, plus entry in `default_shared_modules` in `source3/wscript`.

### Pinned build contract and Samba upgrades

[`samba-build.env`](samba-build.env) is the single operational contract for the
upstream Samba version, archive SHA-256, and `SMB_VFS_INTERFACE_VERSION`.
`Dockerfile.vfs` and the source-cache `Dockerfile.src` both consume it and
verify the archive before extraction. Do not replace the version only in a
Dockerfile or bypass the digest check.

Every `src/samba-vfs/**` change triggers the `Samba VFS Compatibility` CI
workflow. Its `build-runtime` target:

1. builds Samba and `kaimo_bridge` from the same verified source tree;
2. asserts the installed `smbd` version and the source-header VFS ABI;
3. verifies the real module marker and runs `testparm`;
4. starts the real module stack with `full_audit`; and
5. executes authenticated connect, mkdir, create/write, read, rename, unlink,
   rmdir, list, close, and disconnect operations while requiring audit records
   for every configured operation name.

A Samba upgrade is complete only when all of the following are reviewed in one
change:

- update the version, independently verified upstream archive digest, and
  expected VFS interface in `samba-build.env`;
- rebase and review `patches/0001-map-vfs-connect-errno.patch`;
- compile the complete source tree and real module without relying on an old
  Docker cache;
- review every `vfs_fn_pointers` callback signature and access-mask constant
  used by `vfs_kaimo_bridge.c`;
- verify `full_audit` operation names against the new Samba implementation;
- pass the compatibility workflow and the broader deployable-stack release
  matrix; and
- record the version/ABI change, test evidence, rollback image, and operational
  rollout plan in the hardening journal.

The CI matrix is deliberately a minimum compatibility gate. Windows Previous
Versions UX, client interoperability, revocation timing, crash recovery, and
the complete mutation/metadata matrix remain separate release gates.

## Next steps (after Phase 0)

Phase 1 (Auth) — `.proto` + gRPC `GetNtHash`/`ResolveUser` in .NET, custom `pdb` module; then
access/I/O hooks (Phase 2). Details in [overall plan](../../docu/smb-samba-vfs-migration.md#6-implementation-in-phases).
