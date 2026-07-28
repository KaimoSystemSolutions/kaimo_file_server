# Source Evidence and Final Assessment

[← Table of contents](README.md)

## 15. Source evidence index

| Finding area | Primary source locations |
|---|---|
| Context allocation fail-open | `src/samba-vfs/module/vfs_kaimo_bridge.c:526-574` |
| Path reconstruction/truncation | `vfs_kaimo_bridge.c:68-228`, `:320-382`, `:510-522`, `:583-991` |
| Complete access mapping / original gap | `vfs_kaimo_bridge.c:89-197`, `:583-695`; `authd.cpp:53-130`, `:212-249`; `AuthzGrpcService.cs:25-327`; `Core/Security/FilePermissions.cs` |
| Rename authorization / original gap | `vfs_kaimo_bridge.c` (`kaimo_authz_rename`, `kaimo_renameat`); `authd.cpp` (`do_rename`); `AuthzGrpcService.cs` (`AuthorizeRename`) |
| Bounded sidecar workers / original detached-thread gap | `src/samba-vfs/module/authd.cpp` (`BoundedClientQueue`, socket deadlines, worker startup, overload rejection); `src/samba-vfs/tests/test-authd-capacity.py` |
| Bounded authorization cache / original growth gap | `src/samba-vfs/module/decision_cache.h`; `authd.cpp` (cache configuration and sampled counters); `src/samba-vfs/tests/test-decision-cache.cpp` |
| Private authenticated Unix socket / original world-writable gap | `src/samba-vfs/module/authd.cpp` (`configure_expected_peer_executable`, `inspect_peer`, `peer_matches_username`, secure socket publication); `entrypoint.vfs.sh`; `sync-users.sh`; `docker-compose.yml`; `src/samba-vfs/tests/test-authd-peer-security.py`; `test-authd-smb-peer.py` |
| Windows-compatible TREE_CONNECT denial | `src/samba-vfs/patches/0001-map-vfs-connect-errno.patch`; `Dockerfile.vfs`; `src/samba-vfs/tests/test-vfs-connect-status.py` |
| Framed local stream protocol / original partial I/O gap | `src/samba-vfs/module/local_protocol.h`; `vfs_kaimo_bridge.c` (`kaimo_roundtrip`, binary request builders and response parsers); `authd.cpp` (`handle_client`); `src/samba-vfs/tests/test-local-protocol.cpp`; `test-authd-protocol.py` |
| Snapshot ACL filtering / original leak | `SmbBridge/Services/SnapshotGrpcService.cs` (`GetFolderSnapshotAsync`, per-user reconciliation); `Core/Services/File/FileService.cs:884-899` |
| Snapshot cache isolation / original direct path | `SnapshotCache.cs`; `SnapshotGrpcService.cs` (`EnsureIsolatedFromShare`, cache-root-relative paths); `vfs_kaimo_bridge.c` (`kaimo_snapshot_cache_abspath`, reserved namespace checks); `sync-shares.sh`; `docker-compose.yml` |
| Snapshot read-only/VFS-stack enforcement | `vfs_kaimo_bridge.c` (`kaimo_snapshot_create_is_readonly`, `kaimo_snapshot_granted_access`, `kaimo_snapshot_open_how_readonly`, `kaimo_openat`); `src/samba-vfs/tests/test-vfs-snapshot-readonly.py` |
| Snapshot materialization/cleanup leases | `SnapshotCacheLeaseManager.cs`; `SnapshotGrpcService.cs` (`PublishHandoff`, `ReleaseVersionLease`); `SnapshotCacheCleanupService.cs` (`TryDeleteTokenDir`); `vfs_kaimo_bridge.c` (`kaimo_snapshot_lease_acquire`, FSP extensions); `authd.cpp`; `local_protocol.h`; `kaimo_smb_bridge.proto` |
| Disabled share lookup and revocation | `SmbBridge/Services/EnabledShareResolver.cs`; `AuthzGrpcService.cs`; `FileEventGrpcService.cs`; `SnapshotGrpcService.cs`; `entrypoint.vfs.sh`; `sync-shares.sh`; `tests/Kaimo_File_Server.Tests/DisabledShareBridgeTests.cs` |
| Event reliability/TOCTOU | `vfs_kaimo_bridge.c` (`kaimo_capture_close_content`, `kaimo_close`); `authd.cpp` (capture-ID framing/delivery); `FileEventGrpcService.cs` (capture resolution/cleanup); `FileService.NotifyExternalCloseAsync`; `sync-shares.sh` |
| Rename duplicate destruction | `Infrastructure/Repositories/FileVersionRepository.cs:142-178` |
| Private NT-hash import / original predictable temp file | `src/samba-vfs/sync-users.sh`; `src/samba-vfs/tests/test-sync-users.sh`; `src/samba-vfs/Dockerfile.vfs` |
| Disabled/deleted user reconciliation | `src/samba-vfs/sync-users.sh` (managed state, stale passdb removal, group revocation, UID retention); `src/samba-vfs/tests/test-sync-users.sh`; `src/samba-vfs/Dockerfile.vfs` |
| Sync error handling | `sync-users.sh`, `sync-shares.sh`, `sync-config.sh` |
| Authenticated gRPC control plane / original h2c gap | `SmbBridge/Program.cs`; `SmbBridge/Security/*`; `AuthGrpcService.cs`; `bridge_channel.h` and native clients; `docker-compose.yml` |

## 16. Final assessment

The bridge covers the intended high-level migration phases, and the protobuf surface itself is fully wired. The remaining problem is not missing RPC registration; it is that the native operation model, error behavior, resource management, and snapshot/event trust boundaries do not yet preserve the full Kaimo security and lifecycle semantics.

The recommended implementation order is deliberate:

1. Contain direct bypasses.
2. Make the native protocol and memory/resource behavior deterministic.
3. Complete authorization coverage.
4. Continue snapshot hardening after the completed per-file ACL and inaccessible-cache work.
5. Make events and synchronization durable and idempotent.
6. Add transport identity, operational controls, and release gates.

Completing only the snapshot fixes or only the memory fixes is insufficient. The system is safe only when the VFS hooks, sidecar protocol, bridge authorization, lifecycle events, cache, synchronization scripts, and container trust boundaries are treated as one end-to-end security mechanism.
