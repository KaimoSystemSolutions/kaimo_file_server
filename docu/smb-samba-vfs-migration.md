# SMB Migration: Samba + Custom VFS Module (gRPC Bridge to .NET)

> **Status:** 🟢 Phase 0–5 **completed** — Auth, ACLs (Connect/Open/Listing), Close-Hooks
> (Versioning/Ownership/Index), **dynamic shares** (Registry provisioning from DB),
> **protocol settings** (Dialect-Range/Signing/Encryption from `ISmbConfigStore`) and
> **@GMT snapshots** ("Previous Versions") run via gRPC; data path remains native. The old
> `Kaimo_File_Server.Smb` library is **removed**; Samba owns port 445 (**cutover done**).
> Remaining items are tracked in the "Open SMB problems" list at the end of this document.
> **Author:** Design document, created 2026-07-17; Phase 5 completed 2026-07-18
> **Concerns:** Replacement of SMB protocol layer in Kaimo File Server
> **Related:** `src/Kaimo_File_Server.Smb/` (to be replaced),
> `src/Kaimo_File_Server.SmbBridge/` (new gRPC control plane), `docker-compose.yml`,
> [`../samba-vfs/README.md`](../samba-vfs/README.md) (Phase 0/1 results)

---

## Contents

1. [Context & Motivation](#1-context--motivation)
2. [Target Architecture](#2-target-architecture)
3. [Responsibility Division: Samba Native vs. gRPC → .NET](#3-responsibility-division-samba-native-vs-grpc--net)
4. [What is removed / remains / is new](#4-what-is-removed--remains--is-new)
5. [Risks & Open Decisions](#5-risks--open-decisions)
6. [Implementation in Phases](#6-implementation-in-phases)
7. [Verification](#7-verification)
8. [Effort & Recommendation](#8-effort--recommendation)

---

## 1. Context & Motivation

The current SMB access runs through the **self-written NuGet library `SMB-Server` 2.1.1**
(project `Kaimo_File_Server.Smb`, namespaces `Smb.*`). Because it is a custom, incomplete
SMB 2/3 implementation, it is in practice **unreliable**: interoperability issues with
Windows/macOS, protocol edge cases, signing/encryption, durable handles, oplocks/leases.

**Goal:** replace the protocol layer with **real Samba (`smbd`)** — the de-facto reference
implementation that every SMB client knows — and connect Kaimo business logic (ACLs, versioning,
search, ownership, recycle) via a **custom Samba VFS module in C** that communicates via **gRPC** with
the existing .NET part. Samba runs as a **separate Docker container**, deployed via
`docker compose`.

### Decided Trade-offs

| Question | Decision | Consequence |
|---|---|---|
| **Data path** | Samba reads/writes **directly** to mounted storage | gRPC only for control plane → low latency, native throughput performance |
| **Auth** | Continue using existing, encrypted **NT hashes from the DB** | Compatible (`MD4(UTF16LE(pw))` = Samba's NT hash), custom `pdb` module needed |
| **Rollout** | **Complete replacement** of `SMB-Server` library and `Kaimo_File_Server.Smb` layer | No parallel operation; clean cut |

---

## 2. Target Architecture

```
   SMB Clients (Windows / macOS / Linux)
              │  SMB 2/3, Port 445
              ▼
 ┌─────────────────────────────────────────┐        ┌──────────────────────────────┐
 │  Container: kaimo_samba  (NEW)           │        │  Container: kaimo_file_server │
 │  ─ smbd (real Samba)                     │        │  (Host, .NET)                 │
 │  ─ config backend = registry             │  gRPC  │  ─ NEW: SmbBridge gRPC Server │
 │  ─ VFS Module  kaimo_bridge.so  (C/C++) ──┼───────►│    (Control Plane)            │
 │  ─ pdb Module  kaimo_pdb  (NT Hash)    ──┼───────►│  ─ IFileService / IAclService │
 │  ─ ShareControl Daemon  ◄────────────────┼────────┤    IAuthenticationLookup      │
 │                                          │        │    IFileVersionService, Search│
 │  mount: /data/storage  (direct I/O)      │        │    (all from Core, remains)   │
 └───────────────┬──────────────────────────┘        └──────────────┬───────────────┘
                 │ reads/writes files directly                       │
                 ▼                                                   ▼
        ┌──────────────── shared volume:  /data/storage ────────────────┐
        └───────────────────────────────────────────────────────────────┘
                         (Postgres + Elasticsearch as before)
```

Both containers mount the same storage. Samba performs raw file I/O itself; the VFS module calls
.NET only at "cross-cutting" points. .NET can read the same files (for versioning/index)
because it has the same volume mounted — just like host and web do today.

---

## 3. Responsibility Division: Samba Native vs. gRPC → .NET

| Task | Who Handles It | Note |
|---|---|---|
| SMB protocol, signing, encryption, durable handles, oplocks/leases | **Samba native** | exactly why we're switching |
| Raw Read/Write/Seek/Flush | **Samba native, directly to disk** | no gRPC per byte |
| NTLMv2 handshake | **Samba native** | hash comparison local |
| NT hash retrieval | **gRPC → .NET** | custom `pdb` module (Risk 1) |
| TREE_CONNECT authorization (share access) | **VFS `connect` hook → gRPC** | `CanAccessShareAsync` |
| ACL decision on Open/Create/Mkdir/Delete | **VFS hook → gRPC** | today in `FileService.OpenAsync` |
| Versioning/Snapshot on Close | **VFS `close` hook → gRPC** | `IFileVersionService` |
| Search index + Ownership on Close | **VFS `close` hook → gRPC** | Elasticsearch, Ownership stamp |
| Delete → Recycle Bin | **VFS `unlink` hook → gRPC** *or* native `vfs_recycle` | Trade-off (Risk 4) |
| "Previous Versions" (@GMT) | **VFS Snapshot Hooks → gRPC** | `GetSnapshotTimestampsAsync` / `OpenSnapshotAsync` |
| Directory listing including ACL filtering | **VFS `readdir` hook** (performance sensitive) | today `FileService.ListAsync` filters per ACL |
| Manage share list dynamically | **ShareControl → `net conf` (registry)** | replaces current FileSystemWatcher/`SyncFromDb` |
| Share visibility per user (ABE) | **open** — see Risk 2 | today `KaimoSharePolicy.IsVisible` |

---

## 4. What is Removed / Remains / Is New

### Removed (replaced)
- NuGet `SMB-Server` 2.1.1 and the entire `Kaimo_File_Server.Smb` project
  (`SmbServer.cs`, `KaimoIdentityBackend.cs`, `KaimoSharePolicy.cs`, `KaimoFileStore.cs`,
  `KaimoFileHandle/Info`, `KaimoShare`, `KaimoUserRegistry`, `SmbSync`, `SmbManagedDataService`).
- The FileSystemWatcher-based share reconciliation mechanism in `SmbServer.cs`.

### Remains practically unchanged (the big advantage)
- `Kaimo_File_Server.Core` — Domain, `IFileService`/`FileService`, `IAclService`, versioning,
  ownership, search. Placed behind a gRPC facade instead of `KaimoFileStore`.
- `Kaimo_File_Server.Infrastructure` — `FileSystemStorage`, Repositories, `FileServiceFactory`,
  `IAuthenticationLookup`, config store, migrations (including `AesGcmNtHashProtector`).
- `Kaimo_File_Server.Web` — Admin UI (can remain unchanged if ShareControl polls the DB).
- Postgres, Elasticsearch, the `/data/storage` volume model.

### New
1. **.NET: gRPC Control Plane Server** (new project `Kaimo_File_Server.SmbBridge` or in Host).
   Thin facade on existing core services. Reuse:
   `IFileServiceFactory.CreateForShare(shareId, path)`, `IFileService.OpenAsync/ListAsync/…`,
   `IAuthenticationLookup.GetNtHashAsync/ResolveUserContextAsync`, `IAclService`, `IFileVersionService`.
2. **C Project `samba-vfs/`**: VFS module `kaimo_bridge` (C + C++ translation unit for gRPC client with
   `extern "C"` shim), custom `pdb` module, ShareControl daemon, `smb.conf` template with
   `config backend = registry`, entrypoint.
3. **Shared `.proto` files** — defined once, generated for .NET (`Grpc.Tools`) and C/C++
   (`protoc` + `grpc_cpp_plugin`).
4. **`samba-vfs/Dockerfile`** + new compose service `kaimo_samba` (port 445, mount `/data/storage`);
   port 445 moves from host to Samba container.

---

## 5. Risks & Open Decisions

### Risk 1 — Auth: NT Hash Reuse Requires Custom Samba `pdb` Backend
Samba validates NTLMv2 locally but needs the NT hash from its `passdb`. Kaimo's NT hash is
`MD4(UTF16LE(pw))` — **exactly Samba's NT hash**, so it's compatible. Two approaches:
- *(recommended)* **custom `pdb` module** (`pdb_methods`, especially `getsampwnam`), fetches the hash live via
  gRPC from .NET → single source of truth. Cost: C against Samba internals + SID/flag mapping.
- *(Fallback)* Periodically **synchronize** NT hashes into Samba's `tdbsam`. Simpler but
  requires sync job and duplicate data storage.

> **Implemented in Phase 1 (fallback approach):** The .NET bridge (`Kaimo_File_Server.SmbBridge`) provides
> NT hashes via gRPC with `ListUsers`/`GetNtHash`; the C++ client `kaimo_authsync` in the Samba container
> imports them via `pdbedit` into `tdbsam`. Real NTLMv2 login (`admin/admin1234`,
> `marco.hanisch/1234`) works, wrong password is rejected. The custom `pdb` module (on-demand,
> without bulk sync) remains a later production option. **As a side benefit:** gRPC-in-C++ in
> Samba container is now proven — the last open toolchain risk from Phase 0.

### Risk 2 — Dynamic Share *Visibility* Per User ⚠️ (Trickiest Point)
- *Make shares exist dynamically* → **solved** via `config backend = registry` +
  `registry shares = yes`: `smbd` reads shares live from `registry.tdb`, **without reload/restart**. The
  ShareControl daemon manages them via `net conf addshare/setparm/delshare`. Replaces `SyncFromDb()`.
- *Per-User ABE Visibility* (today `KaimoSharePolicy.IsVisible` → `CanListShareAsync`) →
  **the real bottleneck.** Share enumeration runs via `srvsvc`/IPC$ *before* a share VFS is active;
  no clean VFS hook. Options:
  - **(a)** native `access based share enum = yes` + sync share ACL from Kaimo to registry.
  - **(b)** coarse `IsShareHidden` semantics only (`browseable = no`), give up fine-grained listing.
  - **(c)** compromise: (a) for visibility + `connect` hook for hard authorization.
  - → **Decision (Phase 4): (b).** Only the hidden flag controls visibility
    (`IsShareHidden` → `browseable = no`); hard access is already decided by Phase 2a's
    `connect` hook based on real Kaimo ACLs. This eliminates per-user `valid users`
    synchronization, which would overlap with the `connect` hook as access authority and duplicate
    ACL logic. Full per-user ABE (option c) remains a later option if hiding non-accessible shares
    in enumeration becomes a requirement.

### Risk 3 — VFS ABI Coupling + gRPC-in-C Toolchain
VFS modules must be compiled against the **exact Samba version** (`SMB_VFS_INTERFACE_VERSION`);
internal headers (`vfs.h` etc.) are **not** in `samba-dev`, only in the source tree. →
Build module in the same image against the same Samba source; Samba upgrades may require rebuilds
(maintenance cost). gRPC has only a low-level API in pure C → practical is **gRPC-C++** in a
C++ translation unit with `extern "C"` shim. Since the control plane is **low-frequency**
(open/close/connect, not per byte), transport is not critical — alternative: Protobuf over Unix socket (nanopb).

> **Confirmed in Phase 0:** `samba-dev` does not provide VFS headers (out-of-tree build impossible),
> and an upstream-built module does **not** load into distro Samba
> (`libsmbd-base-samba4.so: cannot open shared object`). Consequence: **build and operate Samba ourselves**
> (`--prefix=/opt/samba`) — module, `smbd` and private libs from one build. The Kaimo
> Samba image thus pins the Samba version anyway, which mitigates ABI coupling.

### Risk 4 — Feature Parity at Three Locations
- **Snapshots/@GMT:** Snapshot VFS module (`FSCTL_SRV_ENUMERATE_SNAPSHOTS` → gRPC) *or* arrange Kaimo's
  version layout so that native `vfs_shadow_copy2` understands it.
- **Recycle:** check whether native `vfs_recycle` is sufficient (less code) or whether Kaimo's
  `.RECYCLE_BIN`/`IsRecycleEnabled` behavior requires `unlink` hook → gRPC.
- **Directory Listing ACL Filter:** today `FileService.ListAsync` hides entries without permission. A
  `readdir` hook with gRPC per entry would be expensive → batch filter/caching. Performance sensitive, measure early.

---

## 6. Implementation in Phases

| Phase | Content | Goal |
|---|---|---|
| **0 — Spike/PoC** ✅ | `samba-vfs/` container: Live Registry shares + `kaimo_bridge` module intercepting Connect/Open/Disconnect | Toolchain + ABI binding + Live shares **proven** — see [`../samba-vfs/README.md`](../samba-vfs/README.md) |
| **1 — Auth** ✅ | `.proto` + .NET gRPC bridge (`GetNtHash`/`ListUsers` via `IAuthenticationLookup`); C++ client `kaimo_authsync` syncs NT hashes to Samba's `tdbsam` | real NTLMv2 login against Kaimo user **works** — see [`../samba-vfs/README.md`](../samba-vfs/README.md) |
| **2a — Connect Auth** ✅ | VFS `connect` hook → bounded-worker Sidecar `kaimo_authd` (Unix socket) → gRPC `AuthorizeConnect` → `CanAccessShareAsync`; open-decision LRU is bounded by entries/bytes and TTL | Share access per real Kaimo ACLs — **works** (dept-based allow/deny verified); P1-01/P1-02 resource bounds are native-tested |
| **2b — Path ACL** ⚠️ | VFS `create_file` → `AuthorizeOpen` with complete access masks; `unlinkat` → `AuthorizeDelete`; `renameat` → `AuthorizeRename` for source/destination/replacement; `readdir` uses listing-only checks | Complete open/delete/rename policy is implemented and managed-tested; native Samba runtime verification of P0-03/P0-04 remains pending |
| **3 — Close Hooks** ✅ | `close`/`unlinkat`/`renameat`/`mkdirat` → Sidecar → gRPC `EventService` → `FileService.NotifyExternal*` (versioning, ownership, search index, ACL realign) | Parity to `FileSession.DisposeAsync` — **works** (versioning/ownership verified; see [`../samba-vfs/README.md`](../samba-vfs/README.md)) |
| **4 — Dyn. Shares & Visibility** ✅ | ShareControl sync (`ListShares` → `kaimo_sharesync` → `sync-shares.sh` → `net conf`) ✅; ABE = **hidden flag only** (`browseable`) ✅; Protocol settings from `ISmbConfigStore` (`GetProtocolSettings` → `kaimo_configsync` → `sync-config.sh` → `net conf setparm global`) ✅ | dynamic shares + protocol config live — **works** |
| **5 — Snapshots & Cutover** ⚠️ | @GMT snapshots via `get_shadow_copy_data` + timewarp resolve; folder projections use `IFileService` per-file ACL filtering; materialized content lives in an isolated global cache partitioned by share/user; the gRPC control plane uses an isolated network, mTLS client identities, RPC allow-lists, and audited/rate-limited hash export; `enable/disable SMB` is enforced by the bridge; the old SMB library is removed | P0-05/P0-06/P0-07 fixed; P0-07 focused mTLS runtime verified; broad live SMB regression and P1 snapshot hardening remain |

---

## 7. Verification (End-to-End)

- `docker compose up`, then from **Windows Explorer + macOS Finder + `smbclient`** against
  `\\host\<share>`: Login with Kaimo user (NTLMv2), create/read/write/rename/delete folders/files.
- **ACL:** User without permission gets `AccessDenied` on open/connect (gRPC decision takes effect).
- **Versioning:** save file multiple times → "Previous versions" (@GMT) visible; ES index
  updated; ownership stamped.
- **Dynamics:** via web UI create/rename/delete share → appears/disappears live without
  restart; hidden/ABE shares correctly (in)visible.
- **Robustness:** `smbtorture`/`smbclient` interop, signing/encryption enforced — the core reason.
- **Performance:** measure latency with many small files / large directory listings (Risk 4).

---

## 8. Effort & Recommendation

Substantial undertaking, **several weeks**. Risk concentrated on: (1) custom `pdb` backend,
(2) Per-user ABE visibility, (3) VFS ABI/gRPC-in-C toolchain, (4) snapshot/@GMT & listing filter.
The rest is well-scoped integration work because Kaimo's core logic in `Core`/
`Infrastructure` is preserved and only "rewired."

> **Recommendation: Phase 0 first** — it clarifies the two biggest unknowns (build toolchain +
> live registry shares) with minimal effort before larger investment.

Phase 0 progress is tracked in [`samba-vfs/README.md`](../samba-vfs/README.md).

---

## 9. Open SMB problems (post-Phase-5 backlog)

The migration is functionally complete. The list below was **cross-checked against
the actual code on 2026-07-19** and re-grouped by real priority (P0 = blocks
production, P1 = correctness/feature gap, P2 = operational hardening, P3 =
cosmetic/deferred by design). Each item keeps its original A/B/C number for
traceability and carries a **Status**. Items marked *fixed 2026-07-19* were
addressed in this pass. The `kaimo_samba` image was rebuilt and driven live during
this session, so the snapshot path (#1/#2), the audit toggle (#7) and the authz
change (#14) are **verified against a running Samba**, not just compiled. A running
build identifies itself in the logs via `kaimo_bridge build [<date+tag>]` (emitted
by `vfs_kaimo_bridge_init`) — check it after a rebuild to confirm the deployed
module matches the source.

> **Note on the old A/B/C grouping:** the previous "A — must-do before production"
> heading actually mixed a cosmetic item (#4) with three genuine blockers (#1–#3).
> They are now split by severity below.

### P0 — Blocks production

- **#14 — Bridge SPOF + unauthenticated h2c + fail-**open** authz.**
  `kaimo_failmode_allow()` ([`vfs_kaimo_bridge.c`](../samba-vfs/module/vfs_kaimo_bridge.c))
  previously **allowed** on infrastructure error unless `KAIMO_AUTHZ_FAILCLOSED=1`;
  the sidecar dials the bridge with `InsecureChannelCredentials()` (plaintext, no
  mTLS). A bridge outage meant *every* access was granted.
  **Status: transport/authentication fixed 2026-07-23; redundancy remains.**
  The default is fail-closed (`KAIMO_AUTHZ_FAILOPEN=1` is the explicit emergency
  override); Compose sets fail-closed, isolates Samba/bridge on a private control
  network, and keeps bridge DB access on a bridge-only internal DB network. All C++
  clients use mTLS with one shared Samba workload identity and an explicit
  server-side RPC allow-list, while bulk NT-hash export is rate-limited and
  audited. Separate client certificates would not create a meaningful boundary
  while all helpers share the same container and credential mount.

- **#1 (A.1) — @GMT snapshots: verified end-to-end (2026-07-19).** Live Windows +
  `smbclient` testing now confirms the full path: file- and folder-level "Previous
  Versions" enumerate correctly, and **View / Copy / Restore return the correct
  historical content** for every version. `smbclient get` of a versioned file returns
  the exact per-version bytes.
  Two real bugs were found and fixed during this pass:
  - **Point-in-time resolve** (bridge): `ResolveVersion` matched the version by an
    *exact* timestamp, but folder snapshots enumerate one *share-wide* @GMT token that
    is almost never a file's exact version time → opens failed. Fixed to "newest
    version at or before the token" (same semantics as the folder listing).
  - **openat data-path redirect** (VFS module, see #2).
  **Known non-issue:** Explorer's *"Open"* button on a file version (which launches
  Notepad directly on the `\\host\share\@GMT-…\file` path) shows "file not found".
  This is a **Windows client limitation** — Win32 apps don't reliably resolve the @GMT
  timewarp token when launched with such a path. The server serves the identical path
  correctly (`smbclient` + every SMB op returns `NT_STATUS_OK`); *Copy* and *Restore*,
  the actual Previous-Versions use cases, work. Nothing to fix server-side.
  **Status: fixed / verified.**

- **#3 (A.3) — Snapshot cache grows unbounded.** The former `<share>/.kaimo-snapshots/` cache had no
  TTL/size cap and was never cleaned on share/version deletion → slow disk fill.
  **Status: fixed 2026-07-19** — `SnapshotCacheCleanupService` (background service in
  the bridge) evicts entries by age and enforces a per-share size cap; configurable
  via `Snapshots:Cache:*`. P0-06 later moved the bounded cache to the isolated global
  `<cache-root>/<share-id>/...` layout and added explicit orphan/legacy cleanup.

### P1 — Correctness / feature gaps

- **#8 (B.8) — `mkdirat` close-hook unreliable** on Samba's SMB2 dir-create path →
  new directories may not be indexed/owner-stamped.
  **Status: fixed 2026-07-19** — the `create_file` hook now also emits a `MKDIR`
  notify when it created a directory (`*pinfo == FILE_WAS_CREATED` +
  result is a directory), which is the reliable SMB2 path; the `mkdirat` hook stays
  as a fallback (`NotifyMkdir` is idempotent, so a double notify is harmless).
  Deployed in the rebuilt image; not isolated-tested on its own (a dedicated
  new-dir-over-SMB → index/owner-stamp check is still worth running).

- **#7 (B.7) — WS-Discovery & audit-log toggles inert.** `EnableWsDiscovery` /
  `EnableAuditLog` exist in `SmbProtocolSettings` but were not transported or applied.
  **Status: fixed / verified 2026-07-19** — added `enable_ws_discovery` /
  `enable_audit_log` to the `ProtocolSettingsReply`, populated in `ConfigGrpcService`,
  emitted by `kaimo_configsync`, and applied in `sync-config.sh` (start/stop `wsdd`;
  toggle the `full_audit` VFS + audit params in the global registry). Image now ships
  `wsdd` + `procps` (`Dockerfile.vfs`). Audit logging confirmed live (full_audit
  `user|ip|share|op|...` lines appear).
  > **⚠️ Footgun found & fixed the same day:** `full_audit` **fails every
  > `TREE_CONNECT` (incl. IPC$ → all logins broken)** if its `full_audit:success` /
  > `:failure` op list contains an invalid operation name. The first version used
  > legacy names (`open`/`rename`/`unlink`/`mkdir`); Samba 4.19 (ABI 49) only accepts
  > the `*at` names (`openat`/`renameat`/`unlinkat`/`mkdirat`). Since `EnableAuditLog`
  > defaults to **true**, a fresh system loaded `full_audit` immediately and locked
  > everyone out. Fix: correct op names **plus** a **self-test guard** in
  > `sync-config.sh` — after enabling audit it probes a real connect (`smbclient -L`)
  > and auto-strips `full_audit` if that fails, so the audit toggle can never again
  > take the whole service down.

- **#11 (C.11) — Elasticsearch indexing system-wide inactive.** ES is commented out in
  `docker-compose.yml`; SMB writes index through the same `SearchServiceRouter` as web.
  **Status: OPEN — operational** (re-enable ES, then re-verify SMB indexing).

- **#9 (B.9) — Rapid successive writes may collapse into one version.** The version is
  re-read from disk on `close`, not captured from the write stream, so multiple writes
  within one open/close window fold into a single version.
  **Status: OPEN — needs versioning-path design change** (not a quick fix).

### P2 — Operational hardening

- **#12 (C.12) — `enabled` enforced only at TREE_CONNECT.** Disabling SMB blocked new
  connects but did not drop established sessions.
  **Status: fixed 2026-07-19** — `sync-config.sh` now force-closes clients off every
  registry share via `smbcontrol … close-share` when `enabled=0`. smbd still listens
  on 445 (by design; a full port teardown would require stopping the container).

- **#10 (C.10) — Storage write perms on ACL-less filesystems.** Works via `kaimo`
  group + setgid + `umask 002`; fragile on `drvfs`/`9p` dev mounts without POSIX ACLs.
  **Status: mitigated** (ACL default + create-mask + container `umask 0002` fallback
  already in place); no further code change — a prod ext4/xfs mount removes the risk.

- **#13 (C.13) — Samba version / ABI-49 pinning, no CI guard.** The module is bound to
  the exact Samba build (`SMB_VFS_INTERFACE_VERSION`, ABI 49 = Samba 4.19.5); an
  upgrade needs a module rebuild and nothing guards the version drift.
  **Status: OPEN** — add a CI check asserting the pinned Samba version in
  `Dockerfile.src`/`Dockerfile.vfs` matches the expected ABI.

- **#6 (B.6) — Per-user ABE share visibility.** Only the hidden flag (`browseable = no`)
  is honored; inaccessible shares are still *listed* (access denied on connect).
  **Status: DEFERRED by design** (Risk 2, decision (b)). Full ABE (option c) remains a
  later option if hiding inaccessible shares in enumeration becomes a requirement.

### P3 — Cosmetic / by-design

- **#5 (B.5) — No recycle bin on SMB delete.** Deliberate parity with the old path.
  **Status: DEFERRED by design** — add an `unlinkat` → `.RECYCLE_BIN` path if product
  requires it.

- **#4 (A.4) — `connect` deny surfaced as `NT_STATUS_UNSUCCESSFUL`** instead of
  `ACCESS_DENIED` because Samba hardcoded the generic status for VFS connect
  errors. **Status: fixed / verified 2026-07-23.** The pinned Samba source now
  maps the hook's preserved `errno`; Kaimo's `EACCES` therefore becomes
  `NT_STATUS_ACCESS_DENIED`. A focused live `smbclient` regression reproduces
  the old status and verifies the new denial in under 1.5 seconds.

- **#2 (A.2) — Snapshot file open served the LIVE file (data-path redirect).**
  Live testing showed that opening a versioned file returned the current content (or
  `OBJECT_NAME_NOT_FOUND`). Bridge logs proved the version was resolved and materialized
  correctly (`ResolveVersion … -> FILE … onDisk=True`), so the break was in the VFS
  module: Samba obtains the real fd via **`openat`** (relative to a parent dirfsp), not
  via the `create_file` `base_name` the module rewrote — so the redirect was ignored and
  the live file opened. (Folder listings only *looked* right because they show file
  names, identical live vs. snapshot.)
  **Status: fixed / verified 2026-07-19** — added an `openat` hook (`kaimo_openat`)
  that, for a timewarp open, reconstructs the logical path, asks the bridge to
  materialize the version, and opens the isolated cache copy by absolute path — the same
  place Samba's own `vfs_shadow_copy2` redirects. Normal (non-twrp) opens pass straight
  through (only a `twrp==0` check). Kill-switch `KAIMO_SNAPSHOT_OPENAT=0`.
  Two follow-on fixes made it robust across Samba's call patterns:
  - **share-relative normalization** (`kaimo_share_rel`): some `openat`/`stat` calls
    arrive with the absolute connectpath-prefixed path (Samba's realpath / non-widelink
    verification) — strip the connectpath so the bridge always gets a share-relative
    path, applied identically in `openat` and `stat`/`lstat` (or their snapshot views
    diverge and Samba rejects on the stat-vs-fd inode mismatch).
  - **`.`/`..` pass-through**: opened relative to the already-redirected dir fsp rather
    than resolved as version paths.
  Verified via live Windows (View/Copy/Restore) and `smbclient get`. See the #1 note on
  Explorer's "Open" button (a Windows client-side limitation, not a server bug).
