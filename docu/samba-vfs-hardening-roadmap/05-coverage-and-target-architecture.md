# Coverage and Target Architecture

[← Table of contents](README.md)

## 8. Functional coverage matrix

| Area | Current coverage | Gaps / risks |
|---|---|---|
| Protobuf services | All 13 RPC methods have .NET implementations | No protocol-level auth/version capability negotiation |
| NTLM user sync | Active users and raw NT hashes are exported/imported | Insecure temp storage, no removals, no pagination, corrupt row can abort sync |
| TREE_CONNECT | SMB enabled flag, user, share lookup, root ACL | Disabled share not checked; bridge trust and identity spoofing |
| File read | Raw/specific/generic masks map data, attributes, EA, execute/traverse, and read-control independently | Native/live SET_INFO and client compatibility verification pending |
| File write | Write, append, attributes, EA, DACL, and owner rights are distinct; returned mask is attenuated | Native/live SET_INFO verification and cold-cache performance measurement pending |
| File create | File parent requires `CreateWriteData`; directory parent requires `CreateAppendData`; future target mask is checked | Fallback mutating VFS paths still require a separate completeness inventory |
| Delete/rmdir | Pre-operation target/parent authorization uses exact dynamic canonical path | Race/handle identity semantics remain |
| Delete-on-open | Target `Delete` or parent `DeleteSubItems`; granted handle mask is attenuated | Native delete-on-close matrix pending |
| Rename/move | Pre-op source/destination/replacement authorization plus post-event | Implemented in source; native/runtime verification pending |
| Directory listing | Entry-by-entry read authorization with short cache; legacy snapshot namespace is hidden and access-denied | Unbounded decision cache, synchronous RPC volume, canonicalization |
| Close lifecycle | Modified files emit close event | Lossy, reads content later by path, concurrent attribution races |
| Mkdir lifecycle | `create_file` primary event plus `mkdirat` fallback | Fallback authorization and duplicate-event semantics need proof |
| Delete lifecycle | Metadata/version/search cleanup event | Event loss; no durable reconciliation |
| Rename lifecycle | ACL/version/search path update event | Not idempotent; wrong directory flag; event loss |
| Dynamic shares | Enabled shares mirrored to registry; path changes/removals close stale sessions | Errors ignored, polling delay, unvalidated path |
| Protocol config | Dialect/signing/encryption/wsdd/audit synchronized | Apply failures can be hidden; polling delay; probe credentials |
| SMB enable/disable | Connect gate plus periodic close-share | Existing handles and delay; bridge methods do not all enforce state |
| Snapshot enumeration | GMT tokens returned through VFS; folder timestamps pass through the central directory ACL boundary | Buffer/count limits, directory flag, live per-file visibility verification |
| Snapshot resolution | File ACL checks plus ACL-filtered, reconciled per-user projections in an external overlap-checked cache | Writable/raw open, materialization/cleanup races, unbounded per-request work |
| Recycle bin | Deliberately absent for SMB | Product decision, not memory-safety issue |
| Share enumeration ABE | Hidden flag only | Per-user share visibility intentionally deferred |

## 9. Recommended target architecture

### 9.1 Local Samba-to-sidecar protocol

Use a versioned, length-prefixed binary message rather than tab-separated lines.

Suggested request envelope:

```text
version
request_id
operation
authenticated_session_identity
share_id_or_name
source_path
destination_path
object_type
samba_access_mask
create_disposition
operation_flags
```

Requirements:

- Fixed maximum frame length before allocation.
- Explicit UTF-8 validation and byte-length limits.
- No identity accepted solely from an untrusted text field.
- Full read/write loops and end-to-end deadlines.
- Structured error classes: deny, not found, invalid request, unavailable, timeout.
- A protocol version/capability handshake so VFS and sidecar upgrades cannot silently disagree.

### 9.2 Central authorization API

Prefer one normalized authorization engine over independent ad hoc RPCs. It should:

- Resolve only enabled users and enabled shares.
- Validate and canonicalize paths exactly once.
- Evaluate all requested access bits independently.
- Express multi-path operations such as rename atomically in one decision.
- Return a short machine-readable decision code in addition to a log message.
- Support decision-cache invalidation when ACLs, users, shares, or service state change.
- Keep authorization and post-operation lifecycle notification as separate channels.

### 9.3 Snapshot service

The target snapshot flow should be:

1. Validate enabled user/share and canonical logical path.
2. Enforce read ACL for the exact file or filter every file in a folder snapshot.
3. Reserve cache quota and acquire a keyed materialization lock.
4. Read the immutable version blob with cancellation and a byte limit.
5. Write a temporary file outside the client namespace or within a protected internal namespace.
6. Verify size and content hash.
7. Apply read-only ownership/mode and historical timestamps.
8. Atomically publish the cache entry.
9. Return an opaque internal handle/path that cannot be requested directly by a client.
10. Open through the VFS stack with read-only flags.
11. Hold a lease until the open/traversal completes so cleanup cannot remove active content.

### 9.4 Durable lifecycle pipeline

The target event flow should be:

```text
VFS operation succeeds
  -> append event to local durable spool
  -> acknowledge SMB operation independently
  -> sidecar sends event with stable event_id
  -> bridge transactionally/idempotently applies lifecycle effects
  -> bridge acknowledges event_id
  -> sidecar removes spool record
```

Each event handler must define duplicate behavior:

- Close/version: one version per operation ID.
- Mkdir/owner/index: repeated processing is a no-op/update.
- Delete: already absent is success.
- Rename: already at destination is success; never delete destination history merely because the source is absent.

Periodic reconciliation should still exist to repair missed or partially applied side effects.
