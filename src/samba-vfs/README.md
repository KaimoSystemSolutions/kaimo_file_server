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
docker run -d --name kaimo-samba-spike -p 1445:445 kaimo-samba-spike:phase0

# Self-test (creates share live, writes/reads, removes it again)
docker exec kaimo-samba-spike bash /usr/local/bin/selftest.sh
```

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
kaimo_bridge: CONNECT service=[hooktest] user=[kaimotest]   <- TREE_CONNECT
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
docker run -d --name kaimo-samba-vfs -p 1446:445 kaimo-samba-spike:vfs
# Force file op and verify hooks in log
docker exec kaimo-samba-vfs bash /usr/local/bin/selftest.sh
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
 Kaimo-DB ──► SmbBridge (.NET gRPC, :5080 mTLS) ──gRPC ListUsers──► kaimo_authsync (C++)
                 IAuthenticationLookup.GetNtHashAsync                 │  username + NT hash
                 (decrypted, filters disabled/empty)                  ▼
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
  smbclient -L localhost -U admin%admin1234 -m SMB3;      # CORRECT  -> Shares
  smbclient -L localhost -U admin%wrong     -m SMB3'      # WRONG    -> NT_STATUS_LOGON_FAILURE
```
> Prerequisite: demo users are seeded (`Seed:DemoData=true`, dev): `admin/admin1234`,
> `marco.hanisch/1234`, `anna.weber/1234`, `lisa.mueller/1234`.

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
  close_fn   (file written)    ──framed CLOSE──► NotifyClose ──► FileService.NotifyExternalCloseAsync
  unlinkat_fn(deleted)         ──framed DELETE─► NotifyDelete ─►   → Version (CreateVersionAsync)
  renameat_fn(renamed)  ──framed RENAME_AUTH─► AuthorizeRename
                        ──framed RENAME───────► NotifyRename ─►   → Ownership (EnsureOwnerAsync)
  mkdirat_fn (directory created) ──framed MKDIR──► NotifyMkdir ─►   → Search index (SearchServiceRouter)
                                      (fire-and-forget)             → ACL realignment (Rename)
```

- **.NET:** new `FileService.NotifyExternal{Close,Delete,Rename,Mkdir}Async` (in Core) use the
  **already wired** version/ownership/search services — identical results as web uploads.
  Facade: [`FileEventGrpcService`](../src/Kaimo_File_Server.SmbBridge/Services/FileEventGrpcService.cs).
- **Sidecar** uses a fixed worker pool (`KAIMO_AUTHD_WORKERS`, default 16)
  and a bounded accepted-client queue (`KAIMO_AUTHD_QUEUE_CAPACITY`, default
  64), so slow events do not create unbounded threads or descriptors. Excess
  clients receive `ERROR`; silent clients are closed after
  `KAIMO_AUTHD_IO_TIMEOUT_MS` (default 2000 ms). Events remain
  fire-and-forget.
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
  writes, and all partial reads. Authorization, snapshot, and best-effort event
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
- **Recycle bin** deliberately **not** implemented: the old SMB path (`MarkDeleteOnClose`) also doesn't recycle —
  recycle only exists in web `DeleteFileAsync`. So this is faithful parity.

**Verified:**

| Event | Result |
|---|---|
| marco writes file → closes | `file_versions` snapshot created (**versioning** ✅) |
| same file | `file_metadata.OwnerId = marco` (**ownership** ✅) |
| delete file | `NotifyDelete` fired → deindex path ✅ |
| rename file | `NotifyRename` fired → ACL realignment + index path ✅ |

**Known points:**

- **Search index:** Indexing runs via the same `SearchServiceRouter` as the host. The router
  gates Elasticsearch via a reachability ping; on this system ES indexing is currently
  **system-wide inactive** (the host also hasn't indexed anything new since July 9 — independent of
  this migration). The bridge therefore behaves **parity-faithfully**. Once ES indexing is active again,
  SMB writes will be indexed like web uploads.
- **Rapid successive writes** to the same file may collapse into one version: the version is
  **re-read** from the file on close (not from the open stream as in the old in-process path).
  Uncritical for normal save intervals.
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
                 IShareRepository.GetAllEnabledAsync                 │  name<TAB>path<TAB>hidden
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
  [`sync-shares.sh`](sync-shares.sh). The entrypoint syncs on start (with retries until bridge
  is reachable) and then every `KAIMO_SHARE_SYNC_INTERVAL_SECONDS` (default:
  2 seconds).
- **Reconciliation** in `sync-shares.sh` is idempotent: new shares → `net conf addshare`,
  changed (path/visibility) → `net conf setparm`, removed/disabled → `net conf delshare`.
  A path change or removal additionally runs `smbcontrol smbd close-share` so an
  existing client cannot remain attached to the old service path. `global` is
  never touched.

#### Disabled-share revocation semantics

- Once the disabled state is committed, every new bridge authorization,
  lifecycle-event, and snapshot request fails closed on its next database
  lookup; it does not wait for Samba registry reconciliation.
- An already-open Samba tree connection may continue using existing handles
  until the next successful share reconciliation. Under healthy operation,
  the revocation target is therefore `KAIMO_SHARE_SYNC_INTERVAL_SECONDS` plus
  the `ListShares` RPC/reconciliation runtime (2 seconds plus runtime by
  default).
- Reconciliation removes the registry share first and then calls
  `smbcontrol smbd close-share`, which forcibly disconnects every active tree
  connection for that share. Clients must reconnect after it is enabled again.
- If the bridge is unreachable, the synchronizer cannot safely infer which
  existing shares were disabled. Registry state and active sessions remain
  until a successful reconciliation; operators must alert on repeated
  share-sync failures.

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
  smbclient -L localhost -U admin%admin1234 -m SMB3'   # -> shares in enumeration
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
  net conf getparm global "server signing"'
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
- **ACL parity:** concrete files require `ListReadData`; folders go through
  `IFileService.GetFolderSnapshotAsync`, which checks the directory and batch-filters
  every historical child. Before returning a folder, the bridge removes stale files
  from that user's projection, including files revoked after earlier materialization.
- **Cache isolation:** `Snapshots:Cache:RootPath` and
  `KAIMO_SNAPSHOT_CACHE_ROOT` must match. Resolution fails closed if that root
  overlaps a share; `sync-shares.sh` also refuses to publish an overlapping share.
  The legacy top-level `.kaimo-snapshots` name remains denied in client-facing VFS
  path hooks while the cleanup service removes recognizable old cache trees.
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
- **Healthcheck:** reports `healthy` once `smbd` accepts connections.

Test after startup:
```bash
docker compose exec kaimo_samba bash /usr/local/bin/selftest.sh
docker compose logs kaimo_samba | grep "kaimo_bridge:"
```

> The first build compiles Samba from source (~7 min). After that, BuildKit layer cache takes effect.

## Important facts from Phase 0

- Samba runtime version: **4.19.5-Ubuntu** (`ubuntu:24.04` package).
- **`SMB_VFS_INTERFACE_VERSION = 49`** — the VFS module must be built against exactly this ABI.
- VFS module directory: `/usr/lib/x86_64-linux-gnu/samba/vfs/`.
- Module init symbol: `vfs_kaimo_bridge_init` → registered under the name `kaimo_bridge`.
- Build registration: `bld.SAMBA3_MODULE('vfs_kaimo_bridge', subsystem='vfs', …)` in
  `source3/modules/wscript_build`, plus entry in `default_shared_modules` in `source3/wscript`.

## Next steps (after Phase 0)

Phase 1 (Auth) — `.proto` + gRPC `GetNtHash`/`ResolveUser` in .NET, custom `pdb` module; then
access/I/O hooks (Phase 2). Details in [overall plan](../../docu/smb-samba-vfs-migration.md#6-implementation-in-phases).
