# samba-vfs — Kaimo Samba VFS bridge

> **License: GPL-3.0-or-later** (see [`LICENSE`](LICENSE)), unlike the rest of
> the repository, which is AGPL-3.0-or-later. This module is compiled against
> and loaded into Samba's `smbd` and is therefore a derivative work of Samba
> (GPL). It talks to the .NET control plane only over a local socket / gRPC
> boundary, which keeps the two license domains separate. This part cannot be
> relicensed commercially.

Serves SMB directly from Samba while Kaimo stays the source of truth for users,
shares, ACLs, versions and search. A custom VFS module hooks the points where
Kaimo must decide or be notified; everything else stays native Samba I/O.

The phase-by-phase history and rationale live in the
[overall plan](../../docu/smb-samba-vfs-migration.md) and the
[hardening roadmap](../../docu/samba-vfs-hardening-roadmap/README.md). This file
describes only how the result is put together.

## Architecture

Two planes, both driven by the same .NET bridge over gRPC (mTLS):

**1. Provisioning plane (periodic pull, Kaimo DB → Samba).** C++ clients fetch
desired state from the bridge; shell reconcilers apply it idempotently and read
it back before reporting success.

```
 Kaimo DB ─► SmbBridge (.NET, gRPC :5080) ─► authsync / sharesync / configsync (C++)
                                                     │
              users ─► sync-users.sh  ─► pdbedit ─────┼─► tdbsam
              shares─► sync-shares.sh ─► net conf ────┼─► registry.tdb  (live, no smbd restart)
              config─► sync-config.sh ─► net conf ────┘   global protocol/signing/encryption
```

**2. Decision plane (live, per SMB operation).** A Samba VFS hook does a short
Unix-socket roundtrip to the `kaimo_authd` sidecar, which turns it into a gRPC
call to the bridge. Data I/O itself stays native (`SMB_VFS_NEXT_*`).

```
 smbd  ──►  vfs_kaimo_bridge.so  ──Unix socket (KAIM frame)──►  kaimo_authd  ──gRPC──►  SmbBridge (.NET) ──► Core
 (pure C hook)                     (12-byte envelope,            (C++ sidecar:            (facade over
                                    local_protocol.h)             gRPC, cache, spool)      Kaimo services)
```

Hooks and what they call:

| VFS hook | Bridge RPC | Effect |
|---|---|---|
| `connect` | `AuthorizeConnect` | share access via genuine Kaimo ACL (fail-closed) |
| `create_file` | `AuthorizeOpen` | path ACL → exact granted access mask back to Samba |
| `readdir` | `AuthorizeOpen` (per entry, cached) | hides entries without read permission |
| `renameat` | `AuthorizeRename` | rename authorization + recycle-bin move |
| `unlinkat` | `AuthorizeDelete` | delete authorization; recycle-bin or permanent |
| `close` / `unlinkat` / `renameat` / `mkdirat` | `Notify{Close,Delete,Rename,Mkdir}` | versioning, ownership, search index (durable, acked) |
| `get_shadow_copy_data`, `stat`/`create_file` (twrp) | `EnumerateSnapshots`, `ResolveVersion` | @GMT "Previous Versions" from Kaimo's versioned blobs (read-only) |

## Implementation map

| Path | Role |
|---|---|
| [`module/vfs_kaimo_bridge.c`](module/vfs_kaimo_bridge.c) | the VFS module loaded into `smbd` — pure C, only local-socket roundtrips (no gRPC/threads/fork in smbd) |
| [`module/local_protocol.h`](module/local_protocol.h) | local wire format: 12-byte `KAIM` envelope, length-prefixed UTF-8 fields, 8 KiB req / 64 KiB resp caps |
| [`module/authd.cpp`](module/authd.cpp) | `kaimo_authd` sidecar (C++): local socket ↔ gRPC, decision LRU cache, durable event spool |
| [`module/authsync.cpp`](module/authsync.cpp), [`sharesync.cpp`](module/sharesync.cpp), [`configsync.cpp`](module/configsync.cpp) | gRPC clients that pull users / shares / protocol config |
| `sync-*.sh`, [`run-sync.sh`](run-sync.sh), [`supervise-samba.sh`](supervise-samba.sh) | shell reconcilers + PID-1 supervisor that starts `authd` before `smbd` |
| [`protos/kaimo_smb_bridge.proto`](protos/kaimo_smb_bridge.proto) | single gRPC contract → generates C# (bridge) and C++ (clients) |
| [`../Kaimo_File_Server.SmbBridge`](../Kaimo_File_Server.SmbBridge) | .NET gRPC facade over `IAuthenticationLookup`, `IShareRepository`, `IFileService`, `IFileVersionService`, `ISmbConfigStore` — no logic duplicated |
| [`conf/smb.conf.vfs`](conf/smb.conf.vfs), [`Dockerfile.vfs`](Dockerfile.vfs), [`samba-build.env`](samba-build.env) | Samba config, image, and the pinned version/ABI/digest contract |

### Design decisions

- **Sidecar keeps the module pure C** — gRPC, threads and retries live in
  `kaimo_authd`, not inside an `smbd` worker.
- **Native data path** — hooks only authorize and notify; bytes move through
  `SMB_VFS_NEXT_*` straight to storage.
- **Fail-closed** — an unreachable bridge/sidecar denies (`KAIMO_AUTHZ_FAILOPEN=1`
  to override). Every roundtrip has a bounded monotonic deadline.
- **Local peer trust** — `authz.sock` is group-restricted; `authd` checks
  `SO_PEERCRED` and the trusted `smbd` executable identity. Bridge link is mTLS
  with an RPC allow-list.
- **Durable events** — lifecycle notifications are spooled with `fsync`+atomic
  rename and acked, then de-duplicated by the bridge, so a crash never loses or
  double-applies a version/index effect.

## Testing

- **Component/unit tests** in [`tests/`](tests): C++ for the header libraries
  (`test-local-protocol`, `test-decision-cache`, `test-event-spool`,
  `test-recycle-move`, `test-rename-event`, `test-share-path`,
  `test-snapshot-enumeration`, `test-sync-json`), Python for the live VFS/authd
  behavior (`test-vfs-connect-status`, `test-vfs-io-deadline`,
  `test-vfs-operation-compatibility`, `test-vfs-snapshot-readonly`,
  `test-authd-*`), and shell for the reconcilers (`test-sync-*`,
  `test-revocation-policy`, `test-operational-credentials`).
- **CI gate** [`.github/workflows/samba-vfs-compatibility.yml`](../../.github/workflows/samba-vfs-compatibility.yml)
  runs on every `src/samba-vfs/**` change: builds Samba + `kaimo_bridge` from the
  verified pinned source, asserts the `smbd` version and VFS ABI, then exercises
  the real stack with `full_audit` through connect / mkdir / create / write /
  read / rename / unlink / rmdir / list / close and requires an audit record for
  each.
- **.NET side** is covered by the solution's unit tests
  ([`../../tests`](../../tests)).
- **Manual release checks** (not automated): Windows Explorer "Previous Versions"
  UX, broad client interoperability, and revocation timing.

Pinned build: self-built upstream **Samba 4.19.5**, `SMB_VFS_INTERFACE_VERSION = 49`.
A Samba upgrade means bumping [`samba-build.env`](samba-build.env) (version +
archive digest + ABI), rebasing [`patches/`](patches), and re-reviewing every
callback signature and access-mask constant — see the CI workflow and the
hardening roadmap for the full checklist.

## Build & run

Integrated as service `kaimo_samba` in the central
[`../../docker-compose.yml`](../../docker-compose.yml); it owns port **445**.

```bash
cd ..                                # docker-compose.yml directory
docker compose up -d kaimo_smb_bridge kaimo_samba   # bridge + Samba
```

- **PKI:** control-plane certs are generated by the one-shot `kaimo_smb_pki_init`
  service into `./secrets/smb-control-plane` (git-ignored); set
  `KAIMO_SMB_CONTROL_PKI` to an externally managed directory in production.
- **Storage:** the bridge needs the same storage mount as host/web (existence and
  type checks mirror `OpenAsync`).
- **Healthcheck** passes only when the `authd`/`smbd` PID files identify the
  expected live processes, the private socket is ready, `smbcontrol smbd ping`
  succeeds and sync is current — no reusable SMB account is created.
- First build compiles Samba from source (~7 min); afterwards the BuildKit layer
  cache applies.

Manual protocol smoke test (needs a root-owned mode-0600 auth file mounted at
`/run/secrets/smbclient-auth`):

```bash
docker compose exec -e KAIMO_SELFTEST_AUTH_FILE=/run/secrets/smbclient-auth \
  kaimo_samba bash /usr/local/bin/selftest.sh
docker compose logs kaimo_samba | grep "kaimo_bridge:"
```
