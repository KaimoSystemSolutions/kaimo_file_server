# Source Evidence and Final Assessment

[← Table of contents](README.md)

## 15. Source evidence index

| Finding area | Primary source locations |
|---|---|
| Context allocation fail-open | `src/samba-vfs/module/vfs_kaimo_bridge.c:526-574` |
| Path reconstruction/truncation | `vfs_kaimo_bridge.c:68-228`, `:320-382`, `:510-522`, `:583-991` |
| Strict bridge path validation and containment | `Core/Helpers/ShareRelativePath.cs` (`TryNormalizeStrict`, `ToContainedAbsolutePath`); `Infrastructure/Storage/FileSystemStorage.cs` (`ToAbsolutePath`); `SmbBridge/Services/AuthzGrpcService.cs` (`TryResolveClientPath`); `FileEventGrpcService.cs` (`TryEventPath`); `SnapshotGrpcService.cs` (`GetScopedCachePath`, persisted-version validation); `ShareRelativePathTests.cs`; `AuthzGrpcServiceAccessMaskTests.cs`; `FileEventGrpcServiceIdempotencyTests.cs`; `SnapshotGrpcServiceAclTests.cs` |
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
| Bounded snapshot enumeration parsing | `module/local_protocol.h` (`KAIMO_LOCAL_MAX_SNAPSHOT_LABELS`, token-size contract); `module/snapshot_enumeration.h`; `authd.cpp` (`do_snapenum`); `vfs_kaimo_bridge.c` (`kaimo_get_shadow_copy_data`); `tests/test-snapshot-enumeration.cpp`; `Dockerfile.vfs` |
| Snapshot response capacity / newest-N policy | `SmbBridge/Services/SnapshotGrpcService.cs` (`MaxEnumerationLabels`, ordered/deduplicated label projection); `module/local_protocol.h` (`KAIMO_LOCAL_MAX_SNAPSHOT_ENUMERATION_PAYLOAD`, compile-time frame guard); `authd.cpp` (all-or-error serialization); `tests/SnapshotGrpcServiceAclTests.cs` (`EnumerateSnapshots_OverProtocolLimit_ReturnsNewestLabels`); `tests/test-snapshot-enumeration.cpp` |
| Snapshot cache content identity | `SmbBridge/Services/SnapshotGrpcService.cs` (`ValidateVersionContentMetadata`, `HasExpectedContentAsync`, `HasExpectedProjectionAsync`, `WriteVerifiedTemporaryFileAsync`, `MaterializeVersionAsync`); `tests/SnapshotGrpcServiceAclTests.cs` (verified cache reuse, same-sized concrete-file and folder corruption repair, source hash/length mismatch rejection) |
| Exception-safe snapshot cleanup enumeration | `SmbBridge/Services/SnapshotCacheCleanupService.cs` (`SafeEnumerateDirectories` materializes inside its exception boundary); `tests/SnapshotCacheLeaseManagerTests.cs` (stable enumeration snapshot and deferred-I/O-failure containment) |
| Startup-validated snapshot cache policy | `SmbBridge/Services/SnapshotCacheOptions.cs` (defaults, bounds, and validator); `SmbBridge/Program.cs` (`ValidateOnStart` registration); `SnapshotCacheCleanupService.cs` (immutable validated options consumption); `docker-compose.yml`; `.env.example`; `tests/SnapshotCacheOptionsTests.cs` |
| Bridge RPC cancellation boundaries | `SmbBridge/Services/AuthGrpcService.cs`; `AuthzGrpcService.cs` (request-bound dependencies and cancellation-aware ACL loops); `ShareGrpcService.cs`; `ConfigGrpcService.cs`; `SnapshotGrpcService.cs` (enumeration plus existing deep materialization cancellation); `FileEventGrpcService.cs` (pre-claim cancellation and post-mutation durable completion); `tests/BridgeRpcCancellationTests.cs`; `SnapshotGrpcServiceAclTests.cs` |
| Bounded batched NT-hash export | `Core/Repositories/IUserRepository.cs` (`SambaCredentialSource` projection contract); `Core/Security/IAuthenticationLookup.cs` (`SambaCredentialBatch`); `Infrastructure/Repositories/UserRepository.cs` (ordered active-user projection); `Infrastructure/Services/AuthenticationLookup.cs` (per-row decrypt/filter/validation); `SmbBridge/Services/AuthGrpcService.cs` (page/total bounds and count-only audit); `SmbBridge/Security/HashExportRateLimiter.cs` (client/offset-bound HMAC continuation); `protos/kaimo_smb_bridge.proto` (pagination metadata); `module/authsync.cpp` (bounded continuation and all-pages-before-output); `tests/AuthenticationLookupNtHashTests.cs`; `AuthGrpcServiceUserExportTests.cs`; `SambaCredentialRepositoryTests.cs`; `ControlPlaneSecurityTests.cs` |
| Minimized and cleared NT-hash handling | `Core/Security/INtHashProtector.cs` (`UnprotectToBytes` contract); `Infrastructure/Security/AesGcmNtHashProtector.cs` (raw decode and temporary-buffer clearing); `Infrastructure/Services/AuthenticationLookup.cs` (raw fixed-time empty-hash check and rejected-buffer clearing); `SmbBridge/Services/AuthGrpcService.cs` (post-copy source clearing); `module/authsync.cpp` (move-only fixed credential buffer and direct hex streaming); `sync-users.sh` (no shell capture and phase-local unlink); `docker-compose.yml` (private credential-staging tmpfs); `tests/AesGcmNtHashProtectorTests.cs`; `AuthGrpcServiceUserExportTests.cs`; `src/samba-vfs/tests/test-sync-users.sh` |
| Coupled authd/smbd supervision and health | `src/samba-vfs/supervise-samba.sh` (bounded readiness, PID-1 signal forwarding, peer termination, status propagation, reaping); `authd-health.sh` (socket/PID-file/process/executable identity); `entrypoint.vfs.sh` (supervisor handoff); `docker-compose.yml` (restart policy and composed healthcheck); `Dockerfile.vfs`; `tests/test-authd-supervisor.sh` |
| Snapshot materialization/cleanup leases | `SnapshotCacheLeaseManager.cs`; `SnapshotGrpcService.cs` (`PublishHandoff`, `ReleaseVersionLease`); `SnapshotCacheCleanupService.cs` (`TryDeleteTokenDir`); `vfs_kaimo_bridge.c` (`kaimo_snapshot_lease_acquire`, FSP extensions); `authd.cpp`; `local_protocol.h`; `kaimo_smb_bridge.proto` |
| Disabled share lookup and revocation | `SmbBridge/Services/EnabledShareResolver.cs`; `AuthzGrpcService.cs`; `FileEventGrpcService.cs`; `SnapshotGrpcService.cs`; `entrypoint.vfs.sh`; `sync-shares.sh`; `tests/Kaimo_File_Server.Tests/DisabledShareBridgeTests.cs` |
| Event reliability/TOCTOU | `vfs_kaimo_bridge.c` (`kaimo_capture_close_content`, `kaimo_close`); `authd.cpp` (capture-ID framing/delivery); `FileEventGrpcService.cs` (capture resolution/cleanup); `FileService.NotifyExternalCloseAsync`; `sync-shares.sh` |
| Rename duplicate destruction | `Infrastructure/Repositories/FileVersionRepository.cs:142-178` |
| Private NT-hash import / original predictable temp file | `src/samba-vfs/sync-users.sh`; `src/samba-vfs/tests/test-sync-users.sh`; `src/samba-vfs/Dockerfile.vfs` |
| Disabled/deleted user reconciliation | `src/samba-vfs/sync-users.sh` (managed state, stale passdb removal, group revocation, UID retention); `src/samba-vfs/tests/test-sync-users.sh`; `src/samba-vfs/Dockerfile.vfs` |
| Sync error handling and convergence health | `sync-users.sh`, `sync-shares.sh`, `sync-config.sh`, `run-sync.sh`, `sync-health.sh`; `entrypoint.vfs.sh`; `docker-compose.yml`; `tests/test-sync-users.sh`; `tests/test-sync-shares.sh`; `tests/test-sync-config.sh`; `tests/test-sync-runner.sh` |
| Bounded polling and active-handle revocation | `entrypoint.vfs.sh` (mandatory initial convergence, periodic scheduling, fatal escalation); `validate-sync-interval.sh`; `sync-users.sh` (pre-publication session revocation); `revoke-samba-sessions.sh`; `sync-cycle.sh`; `docker-compose.yml`; `.env.example`; `tests/test-revocation-policy.sh`; `tests/test-sync-users.sh` |
| Structured synchronization records | `module/sync_json.h`; `module/authsync.cpp`; `module/sharesync.cpp`; `module/configsync.cpp`; `sync-users.sh`; `sync-shares.sh`; `sync-config.sh`; `tests/test-sync-json.cpp`; synchronization shell regressions |
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
