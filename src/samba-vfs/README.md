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

The architecture is documented in
[Samba VFS integration](../../docu/external-access/smb/samba-vfs-integration.md),
[SMB control plane](../../docu/external-access/smb/control-plane-grpc.md) and
[lifecycle events and snapshots](../../docu/external-access/smb/lifecycle-events-and-snapshots.md).
This file covers how the container is built, tested and run.

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

Everything runs inside Docker builds of [`Dockerfile.vfs`](Dockerfile.vfs); nothing
needs a running container, a .NET bridge or a Windows client. There are two levels.
The design of the suite and a scenario-by-scenario list of what it catches are documented in
[`docu/testing/`](../../docu/testing/samba-vfs-test-suite.md).

**1. Build gate (stage `build-runtime`, always on).** Runs on every
`docker compose build kaimo_samba` and in the first job of the CI workflow. A
failure breaks the image build:

- every C++ component test `tests/test-*.cpp` (picked up automatically);
- the shell reconciler tests (`tests/test-sync-*`, `test-revocation-policy`,
  `test-authd-supervisor`, `test-operational-credentials`,
  `test-event-spool-permissions`);
- `verify-samba-build.sh` (pinned version and VFS ABI) and the live
  `test-vfs-operation-compatibility.py` matrix (real smbd + module + `full_audit`).

**2. Full suite with coverage (stages `test-env` → `coverage` → `coverage-report`).**
Never part of the runtime image. [`tests/run-suite.sh`](tests/run-suite.sh) rebuilds
the module, the sidecars and the C++ tests with gcov (C++ tests additionally with
ASan/UBSan), wraps every shell script with kcov, then runs:

| Suite | What it drives |
|---|---|
| `tests/test-*.cpp` | header libraries incl. the decision logic extracted from the module (`authz_reply.h`, `snapshot_access.h`, `close_capture.h`, `vfs_env.h`) |
| `tests/live/test_vfs_*.py` | real smbd + `kaimo_bridge` against a scriptable fake authd ([`kaimo_testlib.py`](tests/kaimo_testlib.py)): allow/deny, every malformed reply, fail-open/closed, listing filter, reserved namespace, recycle bin, rename TOCTOU, close capture, snapshots, kill switches, deadlines |
| `tests/live/test_authd_bridge.py`, `test_sidecar_sync.py` | the real `kaimo_authd` and `kaimo_*sync` binaries over mTLS against a fake gRPC bridge ([`kaimo_fakebridge.py`](tests/kaimo_fakebridge.py)) |
| `tests/live/test_standalone_runtime.py` | the self-contained `test-authd-*.py` scripts |
| `tests/test-*.sh`, `tests/shell/test-*.sh` | reconcilers, health checks, entrypoint, PKI generation |
| `tests/python/` | `kaimo-samba-log-forwarder.py` |

The protocol constants of the Python harness are parsed from
[`module/local_protocol.h`](module/local_protocol.h), so a protocol bump cannot
silently desynchronize the tests again.

Run it locally (only Docker needed; the Samba base is cached after the first build):

```bash
cd src/samba-vfs
docker buildx build -f Dockerfile.vfs --target coverage-report --output type=local,dest=./coverage-out .
```

Reports: `coverage-out/summary.md` (per area and per file), `gcovr/index.html`
(C/C++), `kcov/merged/index.html` (bash), `python/html/index.html`, JUnit XML in
`junit/`, and `status.txt`. For quick iteration build the `test-env` target once and
mount the test tree:

```bash
docker buildx build -f Dockerfile.vfs --target test-env --load -t kaimo-samba-test .
docker run --rm -v "$PWD/tests:/opt/kaimo-tests/tests" kaimo-samba-test \
  bash -c 'cd /opt/kaimo-tests/tests && python3.12 -m pytest -q live/test_vfs_connect.py'
```

The live suites need the resolved interpreter (`python3.12`): authd only trusts a
root peer whose process name matches its configured executable.

**CI.** [`.github/workflows/samba-vfs-compatibility.yml`](../../.github/workflows/samba-vfs-compatibility.yml)
runs on every `src/samba-vfs/**` change. Job 1 is the build gate above. Job 2
builds `coverage-report`, publishes `summary.md` as the job summary, uploads all
reports as the `samba-vfs-test-reports` artifact and fails on any test failure or
on a coverage area below [`tests/quality-gates.json`](tests/quality-gates.json).
Raise those minimums when coverage grows (ratchet); never lower them to make a
change pass.

Known findings are kept as `xfail(strict=True)` tests in `tests/live/` with the
reason in the marker; fixing the code turns them into hard failures until the
marker is removed.

- **.NET side** is covered by the solution's unit tests
  ([`../../tests`](../../tests)).
- **Manual release checks** (not automated): Windows Explorer "Previous Versions"
  UX, broad client interoperability, and revocation timing.

Pinned build: self-built upstream **Samba 4.19.5**, `SMB_VFS_INTERFACE_VERSION = 49`.
A Samba upgrade means bumping [`samba-build.env`](samba-build.env) (version +
archive digest + ABI), rebasing [`patches/`](patches), and re-reviewing every
callback signature and access-mask constant; the CI workflow verifies the
pinned version and ABI.

## Build & run

Integrated as service `kaimo_samba` in the central
[`../../docker-compose.yml`](../../docker-compose.yml); it owns port **445**.

```bash
cd ..                                # docker-compose.yml directory
docker compose up -d kaimo_smb_bridge kaimo_samba   # bridge + Samba
```

- **PKI:** control-plane certs are generated by the one-shot `kaimo_smb_pki_init`
  service into `./data/smb-control-plane` (git-ignored); set
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
