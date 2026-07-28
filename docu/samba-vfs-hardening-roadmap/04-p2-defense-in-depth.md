# P2 – Medium Severity and Defense in Depth

[← Table of contents](README.md)

## 7. Medium-severity and defense-in-depth findings

### P2-01: User/share context strings are silently truncated

The connection context stores user and share names in 128-byte arrays. The database username limit is character-based, so UTF-8 input can exceed 127 bytes. Silent truncation can cause denial, incorrect lookup, or prefix identity confusion.

**Fix:** dynamically allocate exact strings and enforce protocol byte-length limits at account/share creation and bridge ingress.

### P2-02: Share-relative canonicalization is inconsistent

`kaimo_share_rel()` is applied in selected snapshot and delete paths but not uniformly in create, readdir, close, rename, and mkdir. Some Samba call sites may produce connectpath-prefixed names.

Its prefix test also does not require a path-separator boundary after `connectpath`.

**Fix:** one canonical path routine, with boundary-aware connectpath stripping, called by every hook before authorization or event emission.

### P2-03: Bridge path validation is inconsistent

Delete checks `ShareRelativePath.IsValid()`, while Open and Snapshot primarily call `Normalize()` only. Snapshot materialization also uses `Path.Combine()` directly rather than the storage layer's containment-checked `ToAbsolutePath()`.

**Fix:** validate raw input before normalization, reject NUL/`..`/absolute/internal paths, then resolve through one containment-checked storage API. Validate persisted `FileVersion.FilePath` before using it as an output path.

### P2-04: Snapshot enumeration parsing trusts an unbounded decimal count

The native parser uses `atoi()` on a sidecar response and allocates based on the result. Invalid or overflowing input is not robustly handled.

**Fix:** use `strtol`/`strtoul` with full error checking, a maximum snapshot count, and a consistency check between count and received token records.

### P2-05: Snapshot response buffers impose undocumented truncation

The VFS uses a 64 KiB response buffer for enumeration. The header may advertise more tokens than fit, while only a subset is parsed. The protocol does not signal truncation or pagination.

**Fix:** add pagination/streaming or an explicit bounded count. Reject a response that exceeds the negotiated frame limit.

### P2-06: Snapshot cache validity uses size instead of content identity

A same-sized partial/corrupt/tampered file is accepted as a valid cache hit.

**Fix:** include version ID/content hash in the cache key or verify the stored hash before reuse.

### P2-07: Cleanup enumeration exception handling is incomplete

`SafeEnumerateDirectories()` returns a lazy enumerable from inside a `try`; exceptions may occur later during `foreach`, outside that local `try`.

**Fix:** materialize the directory list inside the protected block or use enumeration options with per-entry error handling.

### P2-08: Cache configuration is not validated

Negative, zero, NaN, or extreme TTL/sweep/cap values can cause unexpected deletion, service termination, or a tight failure loop.

**Fix:** bind validated options at startup with minimum/maximum values and fail configuration validation before serving requests.

### P2-09: RPC cancellation is not propagated

Repository, ACL, version, and file-copy work generally ignores `ServerCallContext.CancellationToken`.

**Fix:** add cancellation-aware interfaces where missing and pass the request token through database calls, loops, and stream copies.

### P2-10: Bulk user export is unbounded and sequential

`ListUsers` loads all users and calls `GetNtHashAsync` one at a time. One corrupt hash can abort the whole RPC, and a large response can exceed gRPC message limits.

**Fix:** validate hashes individually, isolate corrupt rows, paginate/stream users, enforce exactly 16 bytes, and avoid an N+1 lookup/decryption pattern.

### P2-11: Decrypted credentials are not minimized or cleared

NT hashes exist in managed byte arrays, protobuf copies, C++ strings, stdout capture, shell memory, and a temporary smbpasswd file.

**Fix:** minimize copies and lifetime, avoid shell text transport, zero mutable buffers where practical, and never persist hashes beyond the import transaction.

### P2-12: `authd` is not supervised independently

The entrypoint starts `kaimo_authd` in the background and then replaces itself with `smbd`. If `authd` exits, the container can remain nominally running while authorization fails closed indefinitely.

**Fix:** use a proper init/supervisor or merge process health into container health. Restart/fail the container when the sidecar is unavailable beyond a short threshold.

### P2-13: Disabled service/share behavior is polling-based

Configuration, share, and user sync run every 60 seconds. Existing open handles can continue even after desired state changes, and registry deletion alone does not revoke already-open file handles.

**Fix:** define a revocation SLA, add push/invalidation where needed, close affected shares/sessions immediately, and decide whether active file handles must be forcibly closed.

### P2-14: Hard-coded development credentials are present in operational paths

The entrypoint and health/audit probes use the default `kaimotest`/`Passw0rd!` credentials unless overridden. Passwords are also passed in command arguments.

**Fix:** remove production defaults, require secrets, avoid password-in-argv where possible, and use a dedicated health mechanism that does not depend on a reusable account.

### P2-15: Samba ABI pinning lacks a CI enforcement gate

The module is built against Samba 4.19.5/ABI 49. The source version is pinned, but the repository does not prove that changes or alternate Dockerfiles retain the same version and ABI expectations.

**Fix:** add CI checks for the pinned tarball/version, build the module and Samba together, run `testparm`, load the module, and execute a minimal SMB operation matrix on every relevant change.
