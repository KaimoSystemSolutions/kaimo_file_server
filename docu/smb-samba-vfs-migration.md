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
| **2a — Connect Auth** ✅ | VFS `connect` hook → Sidecar `kaimo_authd` (Unix socket) → gRPC `AuthorizeConnect` → `CanAccessShareAsync` | Share access per real Kaimo ACLs — **works** (dept-based allow/deny verified) |
| **2b — Open/Path ACL** ✅ | VFS `create_file` hook → gRPC `AuthorizeOpen` (exact `OpenAsync` parity) + `readdir` filter (`ListAsync` parity, sidecar cache) | File open (read/write/create) and listing per real ACLs — **works** (write-deny, per-file-deny + hiding verified) |
| **3 — Close Hooks** ✅ | `close`/`unlinkat`/`renameat`/`mkdirat` → Sidecar → gRPC `EventService` → `FileService.NotifyExternal*` (versioning, ownership, search index, ACL realign) | Parity to `FileSession.DisposeAsync` — **works** (versioning/ownership verified; see [`../samba-vfs/README.md`](../samba-vfs/README.md)) |
| **4 — Dyn. Shares & Visibility** ✅ | ShareControl sync (`ListShares` → `kaimo_sharesync` → `sync-shares.sh` → `net conf`) ✅; ABE = **hidden flag only** (`browseable`) ✅; Protocol settings from `ISmbConfigStore` (`GetProtocolSettings` → `kaimo_configsync` → `sync-config.sh` → `net conf setparm global`) ✅ | dynamic shares + protocol config live — **works** |
| **5 — Snapshots & Cutover** ✅ | @GMT snapshots via `get_shadow_copy_data` + timewarp resolve (`SnapshotService` → `IFileVersionService`); `enable/disable SMB` now enforced by the bridge `connect` hook (deny-all when `services.smb.enabled=false`); `Kaimo_File_Server.Smb` **removed** (host runs `SambaSmbControlService` for status only); Samba owns port 445 | **old lib removed, migration complete** |

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

The migration is functionally complete, but the following items remain. Ordered
roughly by priority.

### A — Validation & hardening (must-do before production)
1. **@GMT hooks not yet exercised end-to-end.** The C snapshot hooks
   (`get_shadow_copy_data`, twrp resolve in `create_file`/`stat`/`lstat`) are
   written against Samba's `shadow_copy2` conventions but compile+run only in the
   Samba source build. Needs: build the `kaimo_samba` image, drive a real Windows
   "Previous Versions" → *view* and *restore*, and `smbclient` snapshot ops.
2. **Folder-level snapshot browsing over SMB.** Only file-level restore is wired
   (enumerate + open/stat of a versioned file). Browsing a whole folder "as of" a
   snapshot may need extra path hooks (`openat`/`readdir`/`fstatat` twrp handling).
   The web UI already does folder snapshots (`GetFolderSnapshotAsync`).
3. **Snapshot cache lifecycle.** `<share>/.kaimo-snapshots/` grows unbounded — no
   eviction/TTL/size cap yet, and it is not cleaned when a share/version is deleted.
4. **`connect` deny still surfaces as `NT_STATUS_UNSUCCESSFUL`** to clients (Samba
   hardcodes this on VFS connect errors) instead of `ACCESS_DENIED`. Cosmetic.

### B — Feature parity gaps
5. **Recycle bin on SMB delete.** Deliberately not implemented (parity with the old
   path, which also didn't recycle) — but if product wants SMB deletes to land in
   `.RECYCLE_BIN`, add an `unlinkat` → recycle path.
6. **Per-user ABE share visibility.** Only the hidden flag (`browseable = no`) is
   honored; shares a user cannot access are still *listed* (access is denied on
   connect). Full ABE (hide inaccessible shares in enumeration) is unimplemented.
7. **WS-Discovery & audit log settings are inert.** `EnableWsDiscovery` /
   `EnableAuditLog` exist in `SmbProtocolSettings` but are not wired to Samba
   (would need `wsdd` and the `full_audit` VFS). The UI toggles currently do nothing.
8. **`mkdirat` close-hook unreliable** in Samba's SMB2 dir-create path → new
   directories created over SMB may not be indexed/owner-stamped.
9. **Rapid successive writes may collapse into one version** (version re-read from
   disk on close, not from the write stream).

### C — Operational / correctness
10. **Storage write permissions on ACL-less filesystems.** Group-writable
    (`kaimo` group + `2775`) works, but on filesystems without POSIX ACLs, files
    later created by web/host (UID 1654) need umask `002` or SMB can read but not
    overwrite them. Fragile on drvfs/9p dev mounts.
11. **Elasticsearch indexing system-wide inactive.** SMB writes index via the same
    `SearchServiceRouter` as web; once ES is re-enabled this should be re-verified.
12. **`enabled` flag enforced only at TREE_CONNECT.** Disabling SMB blocks new
    connections but does not drop already-established sessions; smbd keeps
    listening on 445. Acceptable, but not a hard stop.
13. **Samba version pinning / ABI 49.** The VFS module is bound to the exact Samba
    build (`SMB_VFS_INTERFACE_VERSION = 49`); Samba upgrades require a module
    rebuild. No CI guard for this yet.
14. **Bridge is a single point of failure & unauthenticated (h2c).** If the bridge
    is down, authz fails open by default (`KAIMO_AUTHZ_FAILCLOSED=1` to flip).
    gRPC is plaintext on the internal network with no mTLS.
