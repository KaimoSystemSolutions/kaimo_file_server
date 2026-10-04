# Samba VFS Test Catalog

What the Samba VFS test suite catches, grouped by behaviour. Each row names a situation, the
behaviour the suite enforces, and the test that proves it. How the suite is built and run is
described in [Samba VFS test suite](samba-vfs-test-suite.md).

Test references are relative to `src/samba-vfs/tests/`. `live/x.py::name` means a pytest test
(parametrized tests cover every listed variant individually).

## Core principles enforced throughout

Several rules hold across all VFS hooks. The suite checks each of them in many places:

1. **Fail closed by default.** If the authorization sidecar is missing, stalled, overloaded or
   returns an error, the operation is denied.
2. **A malformed answer is never an outage.** A reply that violates the protocol is treated as
   hostile and *always* denied, even when fail-open mode is configured.
3. **An explicit deny is final.** No configuration turns a `DENY` into an allow.
4. **Delete never guesses.** Delete authorization fails closed even in fail-open mode, because
   only the reply says whether the file goes to the recycle bin or is removed permanently.
5. **Events never block or fail the user's operation.** A rejected or stalled lifecycle event is
   logged, but the mkdir/delete/rename/write itself succeeds.
6. **Every wait is bounded.** No sidecar behaviour can pin an `smbd` worker beyond a deadline.
7. **Internal namespaces are invisible.** `.kaimo-*` paths can neither be reached nor listed over
   SMB, and are never sent to the bridge.

### Decision matrix

How an authorization reply becomes allow or deny (`module/authz_reply.h`, tested in
`test-authz-reply.cpp` and live in `live/test_vfs_connect.py` / `live/test_vfs_failopen.py`):

| Reply from authd | Verdict | Default (fail-closed) | `KAIMO_AUTHZ_FAILOPEN=1` |
|---|---|---|---|
| `ALLOW` with the expected payload | allow | allow | allow |
| `DENY` | deny | deny | deny |
| `ERROR`, `OVERLOADED`, socket missing, connection closed, deadline exceeded | unavailable | deny | allow – **except delete authorization**, which stays denied |
| `UNAUTHORIZED_PEER`, wrong magic/version/kind/operation, unexpected status, wrong payload, oversized or truncated frame | malformed | deny | deny |

## Tree connect (share access)

| Situation | Enforced behaviour | Test |
|---|---|---|
| authd allows | Connect succeeds; request carries exactly user and share; log `CONNECT ALLOW` | `live/test_vfs_connect.py::test_allow_records_user_and_share` |
| authd denies | `NT_STATUS_ACCESS_DENIED` in under 1.5 s; no later VFS operation reaches authd | `…::test_deny_maps_to_access_denied_promptly` |
| Wrong password | `NT_STATUS_LOGON_FAILURE`; authd is never asked | `…::test_wrong_password_never_reaches_vfs` |
| 22 broken reply shapes (error, overloaded, closed connection, unauthorized peer, payload on deny/allow/error, wrong operation/kind/magic/version, unknown status, oversized length, payload larger than buffer, truncated payload/header, …) | Always `NT_STATUS_ACCESS_DENIED`, with the correct reason in the log (`unreachable … fail-closed` vs. `malformed … denied`) | `…::test_bad_replies_fail_closed` |
| authd socket missing, or path is a regular file | Denied, logged as unreachable | `…::test_missing_socket_fails_closed`, `…::test_non_socket_path_fails_closed` |
| `IPC$` (share enumeration) | Needs no authorization | `…::test_ipc_share_needs_no_authorization` |
| Module start | Build marker and the effective deadlines are logged; module loads | `…::test_module_build_marker_and_deadlines_logged` |

## Fail-open mode

`KAIMO_AUTHZ_FAILOPEN=1`, see `live/test_vfs_failopen.py`.

| Situation | Enforced behaviour | Test |
|---|---|---|
| Connect: authd error or no sidecar at all | Allowed, logged `fail-open` | `test_connect_infrastructure_error_fails_open`, `test_connect_without_sidecar_fails_open` |
| Malformed reply | Still denied | `test_malformed_reply_still_fails_closed` |
| Explicit deny | Still denied | `test_explicit_deny_is_never_overridden` |
| Open: authd overloaded | Allowed with the access the client requested | `test_open_error_keeps_requested_access` |
| Rename: authd error | Allowed, logged `fail-open` | `test_rename_error_fails_open` |
| Delete: authd error | **Denied**, file kept, logged `recycle disposition is unknown` | `test_delete_error_fails_closed_despite_failopen` |

## Open and create

`live/test_vfs_files.py`

| Situation | Enforced behaviour | Test |
|---|---|---|
| Create a file | Request carries user, share, path, access mask, create/directory/listing flags | `test_open_request_carries_path_mask_and_intent` |
| Read or create denied | `NT_STATUS_ACCESS_DENIED`; nothing downloaded; nothing created | `test_open_deny_blocks_read`, `test_open_deny_blocks_create` |
| authd grants only read access (attenuated mask) | Writing through the handle is rejected; content unchanged | `test_granted_mask_blocks_writes_through_the_handle` |
| Read-only grant, client overwrites with `FILE_OVERWRITE_IF` | Must not truncate the file – **currently fails, known finding** | `test_read_only_grant_prevents_truncation` (`xfail`) |
| Malformed `ALLOW` (missing mask, generic bit, `MAXIMUM_ALLOWED` bit, trailing byte, short mask) or unauthorized peer | Denied, logged `malformed OPEN authorization response` | `test_malformed_open_reply_fails_closed` |
| Infrastructure error | Denied | `test_open_infrastructure_error_fails_closed` |

### Reserved namespace `.kaimo-*`

| Situation | Enforced behaviour | Test |
|---|---|---|
| Download from, upload to, create, list, rename into or delete `.kaimo-*` paths | Rejected; existing captures untouched; nothing created; no such path is ever sent to authd | `test_reserved_namespace_is_unreachable` |

## Directory listing filter

| Situation | Enforced behaviour | Test |
|---|---|---|
| A listed entry is denied | Entry hidden (`LIST hide`); others visible; one lookup per entry with access `0x1`; `.`, `..` and `.kaimo-*` never looked up; `.kaimo-*` hidden | `live/test_vfs_files.py::test_listing_hides_denied_and_reserved_entries` |
| Listing a subdirectory | Lookups use share-relative paths (`dir/inner.txt`) | `…::test_listing_in_subdirectory_uses_share_relative_paths` |
| Kill switch `KAIMO_LIST_FILTER=0` | All entries shown, no lookups at all | `live/test_vfs_kill_switches.py::test_disabled_list_filter_shows_every_entry_without_lookups` |

## Directory creation

`live/test_vfs_files.py`

| Situation | Enforced behaviour | Test |
|---|---|---|
| `mkdir`, also nested | Authorized as create + directory; `MKDIR` event with user, share and path | `test_mkdir_emits_mkdir_event`, `test_mkdir_nested_path_event` |
| `mkdir` denied | Nothing created, no event | `test_mkdir_denied_creates_nothing` |
| Event rejected | Directory still created; logged `not durably accepted` | `test_mkdir_event_failure_does_not_fail_operation` |
| Samba's internal temporary directory name | Must never be reported – **currently fails, known finding** | `test_mkdir_never_reports_samba_temporary_names` (`xfail`) |

## Close capture (versioning after write)

When a modified file is closed, the module copies the exact handle content into
`.kaimo-close-captures/<id>.cap` and emits a `CLOSE` event. See
[Lifecycle events and snapshots](../external-access/smb/lifecycle-events-and-snapshots.md#close-capture).

| Situation | Enforced behaviour | Test |
|---|---|---|
| File written and closed | Exactly one event; capture ID is 32 hex characters; capture has identical content and mode `0440`; no temporary files left | `live/test_vfs_files.py::test_close_captures_exact_content_and_emits_event` |
| File larger than the 128 KiB copy buffer | Every chunk copied | `…::test_close_of_large_file_copies_every_chunk` |
| File only read | No event | `…::test_close_of_unmodified_file_emits_nothing` |
| Event rejected by authd | Capture deleted again | `…::test_close_event_rejection_discards_capture` |
| Capture directory missing | Write succeeds; no event; logged | `…::test_close_without_capture_directory_suppresses_event` |
| Capture directory is a symlink | Not followed; nothing written outside; no event | `…::test_close_capture_directory_symlink_is_refused` |
| Copy internals: random ID, source changed during copy, every rejection leaves nothing behind, copy errors | Covered in isolation | `test-close-capture.cpp` |

## Delete and recycle bin

`live/test_vfs_delete_rename.py`. The `DELETE_AUTH` reply carries the disposition (recycle or
permanent) and the recycle root depth (0 = share root, 1 = first path component, e.g. a user's home
folder).

| Situation | Enforced behaviour | Test |
|---|---|---|
| Permanent delete of a file or empty directory | Removed; `DELETE` event with directory flag; no recycle bin created | `test_permanent_delete_emits_delete_event`, `test_rmdir_emits_directory_delete_event` |
| Delete denied | File kept; `NT_STATUS_ACCESS_DENIED`; no event | `test_delete_denied_keeps_file` |
| Malformed reply (missing/invalid disposition, depth too large, trailing or missing byte), error, overloaded | File kept; no recycle bin; correct log reason | `test_bad_delete_replies_fail_closed` |
| Recycle a file | Moved to `.RECYCLE_BIN/<original path>`; content intact; `RENAME` event (not `DELETE`) | `test_recycle_moves_file_and_emits_rename` |
| Name already exists in recycle bin (once / repeatedly) | New name `<leaf>_<timestamp>`; earlier copies untouched; every round gets a unique name | `test_recycle_collision_gets_timestamped_name`, `test_recycle_repeated_collisions_count_up` |
| Recycle a directory | Moved; event with directory flag | `test_recycle_directory_delete` |
| Depth 1 (home folder) | Moved to `<home>/.RECYCLE_BIN/<rest>` | `test_recycle_home_root_depth_one` |
| Depth 1 but the file has no home component | Fails; file kept | `test_recycle_depth_one_without_home_component_fails` |
| Delete inside a recycle bin | Permanent; no nested recycle bin | `test_delete_inside_recycle_bin_is_permanent` |
| `.RECYCLE_BIN` is a symlink or a regular file | Never followed; file kept; nothing written outside the share | `test_recycle_bin_symlink_is_never_followed`, `test_recycle_bin_regular_file_blocks_move` |
| Delete event rejected | Delete still succeeds | `test_delete_event_failure_does_not_fail_delete` |
| Path boundaries, collisions, symlinked destination, home roots, rename fallback | Covered in isolation | `test-recycle-move.cpp` |

## Rename

`live/test_vfs_delete_rename.py`

| Situation | Enforced behaviour | Test |
|---|---|---|
| Rename a file | Request carries source/destination and all flags; `RENAME` event | `test_rename_authorizes_and_emits_event` |
| Move a directory into another folder | Directory flag set; event emitted | `test_rename_directory_across_folders` |
| Replace an existing destination | `destination_exists` and `replace` flags set | `test_rename_replacing_existing_destination` |
| Rename denied | Source untouched; no destination; no event | `test_rename_denied_keeps_source` |
| Malformed reply, unauthorized peer, error | Source untouched; correct log reason | `test_bad_rename_replies_fail_closed` |
| **Race:** a file appears at the destination while authorization is pending | Rename aborted; intruder file untouched; logged `state changed during authorization` | `test_rename_toctou_destination_appears_during_authorization` |
| **Race:** the source is replaced by a different inode while authorization is pending | Rename aborted | `test_rename_toctou_source_replaced_during_authorization` |
| Rename event rejected | Rename still succeeds | `test_rename_event_failure_does_not_fail_rename` |

## Previous Versions (@GMT snapshots)

`live/test_vfs_snapshots.py`. Old versions are served from a local snapshot cache that the bridge
materializes; the module only reads it, guarded by a lease file.

| Situation | Enforced behaviour | Test |
|---|---|---|
| Enumerate versions | Labels shown to the client; request carries the path | `test_enumeration_returns_labels` |
| Enumeration reply malformed (count mismatch, invalid token, trailing data), denied, error, garbage | Client sees *no* versions (not an error); reason logged | `test_enumeration_failures_report_no_snapshots` |
| Open a version | Content comes from the cache, never from the live file; the lease is released; the next VFS layer sees the logical name; no memory corruption | `test_snapshot_read_is_redirected_to_cache` |
| Repeated metadata and reads in one session | Stable, no memory corruption | `test_repeated_metadata_and_reads_in_one_session` |
| Browse a folder inside a version | Children inherit the parent's lease | `test_snapshot_directory_listing_inherits_parent_lease` |
| Version does not exist | `NT_STATUS_OBJECT_NAME_NOT_FOUND` | `test_missing_version_is_not_found` |
| Bridge returns an unsafe or broken cache path (`..`, absolute, empty/dot component, backslash, control character, no lease scope, empty, truncated, too long, empty or too long lease, error, deny, payload on not-found) | Neither live content nor foreign content is ever served | `test_invalid_resolution_never_serves_live_or_foreign_content` |
| Lease file missing | Fails; lease is still released | `test_missing_lease_file_fails` |
| Lease file is a symlink or hard link | Refused | `test_lease_symlink_or_hardlink_is_refused` |
| Cache sweeper holds the exclusive lock | Open waits until the lock is released, then serves the content – never from a half-deleted tree | `test_sweeper_lock_defers_open_until_released` |
| Write, delete, rename or mkdir inside a version | `NT_STATUS_MEDIA_WRITE_PROTECTED`; cache and live file unchanged | `test_snapshot_mutation_is_write_protected` |
| Properties-style traversal (`allinfo`, `du`) | Completes | `test_properties_style_traversal_completes` |
| Kill switch `KAIMO_SNAPSHOT_OPENAT=0` | Opening a version fails closed | `live/test_vfs_kill_switches.py::test_disabled_snapshot_redirect_fails_closed` |
| Path validation, lease scope, read-only flags, resolve decoding, enumeration limits | Covered in isolation | `test-snapshot-access.cpp`, `test-snapshot-enumeration.cpp` |

## Deadlines and configuration

| Situation | Enforced behaviour | Test |
|---|---|---|
| Custom deadlines set; one value invalid (`5` ms, below minimum) | Valid values applied; invalid value falls back to the default (a deadline can never be disabled) and is logged | `live/test_vfs_deadlines.py::test_configured_deadlines_are_logged` |
| authd stalls on connect | Denied after the configured auth deadline (300 ms in the test), well under 1.5 s | `…::test_stalled_authorization_fails_closed_within_budget` |
| authd stalls on an event | `mkdir` still completes quickly | `…::test_stalled_event_does_not_block_mkdir` |
| authd stalls on delete authorization | File kept | `…::test_stalled_delete_authorization_keeps_file` |
| `KAIMO_AUTHD_SOCK` unset | Compiled-in default `/var/run/kaimo/authz.sock` used; fails closed when absent | `live/test_vfs_environment.py::test_default_socket_path_is_used_and_fails_closed` |
| Share name that is not a valid Kaimo name (`team$`) | Denied before authd is asked | `…::test_invalid_share_name_is_denied_before_authorization` |
| Socket path longer than `sun_path` (108 bytes) | Rejected, never truncated (a truncated path could address another socket) | `live/test_vfs_socket_path.py::test_overlong_socket_path_fails_closed` |
| Parsing rules for deadlines and kill switches | Covered in isolation | `test-vfs-env.cpp` |

Defaults: auth 6000 ms, snapshot 32000 ms, event 250 ms; accepted range 10–60000 ms.

## Authorization sidecar `kaimo_authd`

`live/test_authd_bridge.py` runs the real binary between a local protocol client (the test, acting
as `smbd`) and the fake bridge.

| Situation | Enforced behaviour | Test |
|---|---|---|
| Connect allowed / denied by the bridge | Mapped to `ALLOW` / `DENY` | `test_connect_maps_bridge_decision` |
| gRPC call fails | `ERROR` status; logged | `test_rpc_failure_becomes_error_status` |
| Open | Every field forwarded; granted mask returned | `test_open_forwards_every_field_and_returns_mask` |
| Repeated open within the cache TTL | Answered from cache (also denies); re-asked after expiry | `test_open_decisions_are_cached_until_ttl` |
| Open fails | Failure is not cached | `test_open_failures_are_not_cached` |
| Delete authorization | Disposition and depth encoded; depth clamped to one byte | `test_delete_authorization_disposition`, `test_delete_authorization_failure` |
| Rename authorization | All four flags forwarded | `test_rename_authorization`, `test_rename_authorization_failure` |
| 16 malformed local requests (truncated, trailing bytes, invalid booleans, invalid capture IDs, invalid user/share names, no identity) | `ERROR` without any gRPC call | `test_malformed_requests_get_error_without_rpc` |
| Lifecycle events (mkdir, delete, rename, close) | Acknowledged locally, spooled durably, delivered with a 32-hex event ID | `test_events_are_spooled_and_delivered` |
| Delivery fails temporarily | Retried with the **same** event ID (idempotency) | `test_failed_delivery_is_retried_with_same_event_id` |
| Delivery keeps failing | Dead-lettered after three attempts | `test_undeliverable_event_is_dead_lettered` |
| Snapshot enumeration / resolution / lease release | Correct encoding; invalid tokens, more than 2048 tokens, negative size, missing lease, overlong path and RPC failures become `ERROR` / `NOT_FOUND` | `test_snapshot_*` |
| Invalid configuration (0 workers, non-numeric capacity, retry base above max, unknown group, missing peer executable, missing certificate) | Refuses to start | `test_invalid_configuration_refuses_to_start` |
| Socket directory with unsafe mode (`0777`, `0755`, `0770`) | Refuses to start | `test_insecure_socket_parent_refuses_to_start` |
| Socket directory is a symlink | Rejected or canonicalized, never followed blindly | `test_socket_parent_symlink_refuses_to_start` |
| Stale socket from a crashed run / foreign file at the socket path | Stale socket replaced; foreign file refused and left untouched | `test_stale_socket_is_replaced_but_foreign_file_is_refused` |

Standalone runtime scripts (run inside pytest by `live/test_standalone_runtime.py`):

| Script | What it proves |
|---|---|
| `test-authd-protocol.py` | Framing does not depend on one `read()` per message (a request sent byte by byte is still parsed); truncated payloads, oversized frames (rejected before the payload is read) and unknown protocol versions close the connection |
| `test-authd-capacity.py` | Under 40 concurrent clients with 2 workers and a queue of 3, the thread count does not grow, descriptor growth stays bounded, excess clients get `OVERLOADED`, and silent clients are released after the receive deadline |
| `test-authd-peer-security.py` | Socket mode `0660`, directory `0750`, root-owned; only the trusted executable may claim a session identity; mismatches get `UNAUTHORIZED_PEER`; peers outside the group cannot connect |
| `test-authd-smb-peer.py` | A real `smbd` → VFS → authd hop is accepted as a trusted peer |
| `test-vfs-operation-compatibility.py` | Every configured `full_audit` operation works through the real VFS stack; both delete dispositions in one session; module loads with build marker (also part of the build gate) |

## Provisioning clients (`kaimo_authsync`, `kaimo_sharesync`, `kaimo_configsync`)

`live/test_sidecar_sync.py` runs the real binaries against the fake bridge over mTLS. The shell
reconciler tests stub these binaries, so this is where their output is proven.

| Situation | Enforced behaviour | Test |
|---|---|---|
| Users exported, across continuation pages, or none | Correct JSON document | `test_authsync_exports_users_as_json`, `…_follows_continuation_pages`, `…_empty_directory` |
| Inconsistent pagination (page too large, offset backwards, no progress, limits exceeded, more rows than offset advance, more without token, token on last page) | Exit 1, nothing printed | `test_authsync_rejects_invalid_pagination` |
| Invalid user record (bad/empty/too long name, wrong hash length) | Exit 1, nothing printed | `test_authsync_rejects_invalid_user_records` |
| Rate-limited with a valid retry hint | Retries exactly once | `test_authsync_retries_once_after_rate_limit`, `…_retries_rate_limit_only_once` |
| Rate limit without valid hint, other gRPC status, or on a later page | Fails without retry | `test_authsync_fails_without_valid_retry`, `…_rate_limit_on_later_page_fails` |
| Client certificate missing | Fails before any RPC | `test_missing_credentials_fail_before_any_rpc` |
| Bridge presents an untrusted identity | Rejected | `test_untrusted_bridge_identity_is_rejected` |
| Shares exported / invalid share (bad name, `global`, `IPC$`, dot-prefixed, longer than 64, relative or empty path, invalid allowed user) | Correct JSON / exit 1 with reason | `test_sharesync_*` |
| Config exported / invalid protocol range / each SMB dialect | Correct JSON / rejected / accepted | `test_configsync_*` |

## Shell scripts

| Test | Script(s) | What it proves |
|---|---|---|
| `test-sync-users.sh` | `sync-users.sh` | Distinct POSIX UIDs; private NT-hash import; stale credentials and groups revoked while the UID is retained and locked; reactivation; active sessions terminated on revocation; single-instance serialization |
| `test-sync-shares.sh` | `sync-shares.sh` | Path changes and removals force clients off the old share; unchanged shares stay connected |
| `test-sync-config.sh` | `sync-config.sh` | Failed mutations and unverifiable read-back fail the run; invalid documents rejected |
| `test-sync-config-samba-registry.sh` | `sync-config.sh` + real `net` | Handles Samba's canonical registry parameter names |
| `shell/test-sync-config-branches.sh` | `sync-config.sh` | Service disable with session teardown, audit toggle, reload, log-level mapping, WS-Discovery, bridge/size/read-back failures |
| `test-sync-runner.sh` | `run-sync.sh`, `sync-health.sh` | Reconciliation is serialized; health timestamps published; locks are not leaked to child processes |
| `test-revocation-policy.sh` | `revoke-samba-sessions.sh`, `sync-cycle.sh`, `validate-sync-interval.sh` | Revocation closes active handles, or fails the Samba unit |
| `test-authd-supervisor.sh` | `supervise-samba.sh`, `authd-health.sh`, `smbd-health.sh` | authd and smbd form one fail-fast unit: authd exit stops smbd; authd failure before readiness prevents smbd start; smbd exit stops authd; `SIGTERM` forwarded, both reaped |
| `shell/test-health-checks.sh` | `authd-health.sh`, `smbd-health.sh`, `sync-health.sh` | Every rejection branch (missing/symlinked/wrong-mode/foreign PID file, invalid PID, dead process, never converged) fails with a precise reason; only a verified live state passes |
| `shell/test-entrypoint-vfs.sh` | `entrypoint.vfs.sh` | Initial sync order; storage and socket groups; setgid share directories; internal directories untouched; private event spool; bounded retries and refusal to start without convergence; invalid interval stops before any sync; failed revocation terminates the unit |
| `shell/test-generate-control-plane-certs.sh` | `generate-control-plane-certs.sh` | Fresh PKI; idempotent re-run with key-mode repair; migration from the old four-client layout; refusal to overwrite an incomplete PKI; missing-openssl and foreign-UID fallbacks |
| `test-operational-credentials.sh` | entrypoints, `selftest.sh`, `sync-users.sh`, `sync-config.sh` | No reusable development credential in operational paths |
| `test-event-spool-permissions.sh` | `prepare-event-spool.sh` | Spool permissions repaired without following symlinks |

## Log forwarder

`python/test_log_forwarder.py` tests `kaimo-samba-log-forwarder.py`: environment parsing, Samba
log-level mapping, archive size limits, dated readable chunks, rotation on day change and size,
unique chunk names, flushing after 100 lines or one second, cleanup of expired files and total
size cap, repair of restrictive modes, tolerance of vanishing files and unlink failures, console
level re-read at most once per second, console filtering, untagged lines treated as
`Information`, and a quiet stop on a broken pipe.

## C++ component tests

Run in the build gate (optimized) and in the full suite (ASan/UBSan + coverage).

| Test | Header | Covers |
|---|---|---|
| `test-authz-reply.cpp` | `authz_reply.h` | Response header acceptance, OPEN and DELETE reply decoding, empty `ALLOW` and other statuses, complete fail-mode policy |
| `test-close-capture.cpp` | `close_capture.h` | Capture ID randomness, source stability check, exact content publish, cleanup on every rejection, copy errors |
| `test-decision-cache.cpp` | `decision_cache.h` | LRU eviction, byte budget, oversize entries skipped, expiry and statistics |
| `test-event-spool.cpp` | `event_spool.h` | Enqueue, recovery after restart, retry vs. dead letter |
| `test-local-protocol.cpp`, `test-local-protocol-edges.cpp` | `local_protocol.h` | Binary fields, identity bounds, UTF-8 validation, fragmented reads, builder overflow, embedded NUL, trailing fields, every header rejection, EOF semantics, send/receive deadlines |
| `test-recycle-move.cpp` | `recycle_move.h` | Path boundaries, collisions, symlinked destination, home recycle roots, checked rename fallback |
| `test-rename-event.cpp` | `rename_event.h` | Directory flag derived from the file mode |
| `test-share-path.cpp` | `share_path.h` | Share-relative path normalization (redundant slashes, `.` components) |
| `test-snapshot-access.cpp` | `snapshot_access.h` | Cache-relative path validation, lease scope, read-only open flags, resolve decoding |
| `test-snapshot-enumeration.cpp` | `snapshot_enumeration.h` | Enumeration payload validation and size limits |
| `test-sync-json.cpp` | `sync_json.h` | Username, share-name and path validators |
| `test-vfs-env.cpp` | `vfs_env.h` | Deadline parsing (range, invalid values) and kill switches |
