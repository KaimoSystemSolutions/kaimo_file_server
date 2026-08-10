# Cloud Access Virtual Shares — Architecture and Delivery Plan

## 1. Objective

Cloud Access exposes a remote folder as a virtual share in the Kaimo Files web browser without synchronizing it to local storage.

The browser lists remote metadata such as names, sizes, and timestamps. File content is transferred only when a user previews, downloads, uploads, or explicitly copies a file. Rename, move, copy, and delete operations should run directly at the provider whenever the provider supports them.

Virtual shares are deliberately separate from local `ShareDefinition` records:

- they are visible only in the web file browser;
- they are never exported through Samba or NFS;
- they do not participate in local file indexing, versioning, lifecycle hooks, or Cloud Sync;
- access is granted for the complete virtual share rather than through per-file Kaimo ACLs.

## 2. Feasibility

The design is realistic. OneDrive and Google Drive provide suitable metadata and streaming APIs. SMB and NFS are also possible, but their deployment and failure characteristics require a separate technical evaluation.

The main engineering challenge is not directory listing. It is preserving correct authorization, streaming, cancellation, conflict handling, token protection, cache invalidation, and provider-specific semantics while keeping remote storage isolated from local-share behavior.

## 3. User experience

Administrators manage two related objects under **Cloud Access**:

1. **Connection** — authenticated access to a provider account, assigned to a department.
2. **Virtual share** — a selected remote folder, display name, read-only setting, and complete-share access grants.

The management page follows the normal Shares layout:

- virtual shares are listed on the left;
- the selected share is edited on the right;
- **General** and **Access** are separate tabs;
- providers are selected from a dropdown when a connection is created;
- users and groups are added through a principal dropdown;
- the remote root is selected in a real provider folder picker.

## 4. Access model

Access is intentionally simple:

- default deny;
- a user may access a virtual share through a direct user grant or group membership;
- a grant applies to the entire mapped remote folder;
- read-only is configured for the whole virtual share;
- every list, read, write, and download request checks the grant again;
- direct URLs must never bypass authorization.

Provider permissions are an additional boundary. Kaimo cannot grant more access than the account used for the connection already has.

## 5. Domain model

### Cloud access connection

Stores:

- stable connection ID;
- provider key;
- display name;
- owning department;
- connection state and last error;
- encrypted provider credentials;
- provider account display information;
- creation and update timestamps.

### Cloud access share

Stores:

- stable virtual share ID;
- connection ID;
- globally unambiguous share name;
- provider root item ID and normalized remote path;
- read-only state;
- enabled state and timestamps.

### Cloud access grant

Stores the virtual share and the granted user or group identity. There are no nested Kaimo ACL entries below this level.

## 6. Provider abstraction

The web file browser consumes `IFileBrowserViewModel`. Local and remote implementations expose capabilities instead of pretending to have identical features.

Expected remote capabilities include:

- list one directory level;
- stat and resolve stable provider IDs;
- stream read and write;
- create directory;
- delete;
- rename and move;
- provider-side copy when supported;
- cancellation and structured provider errors.

Local-only features such as Samba ACLs, local version history, search indexing, snapshots, and Cloud Sync remain unavailable for virtual shares.

## 7. OneDrive implementation

OneDrive uses Microsoft Graph with a public-client device-code flow:

- the application client ID and `common` authority are supplied by the project and cannot be overridden at runtime;
- no client secret or callback URL is required;
- delegated scopes are `Files.ReadWrite`, `User.Read`, and `offline_access`;
- refresh tokens are encrypted with ASP.NET Data Protection;
- the selected root is stored by stable Graph item ID;
- directory listings use Graph pagination;
- downloads and uploads are streamed;
- move and rename stay provider-side;
- OneDrive copy uses the asynchronous provider operation and validates its monitor URL.

## 8. Metadata caching

Directory metadata is cached in memory for a short period. The cache never stores file content, OAuth tokens, or authorization decisions.

Current policy:

- default TTL: 20 seconds;
- configurable from 1 to 300 seconds in the global **Settings → Cloud Access** tab;
- key: virtual share ID plus normalized relative directory path;
- concurrent requests for the same uncached directory are coalesced;
- returned objects are cloned so UI state cannot mutate cached snapshots;
- Create, Upload, Rename, Move, Copy, and Delete invalidate every directory entry for the affected virtual share;
- explicit Refresh invalidates the share cache and revalidates the remote root;
- externally made provider changes may remain invisible only until the short TTL expires.

This cache accelerates back-and-forth navigation while preserving on-demand content transfer.

## 9. File transfer semantics

### Within the same remote provider context

Rename, move, and copy should use provider-side operations. No file content should pass through Kaimo unless the provider lacks the requested operation.

### Remote to local

Copy checks local authorization and streams data from the provider into the local file service through a bounded in-memory pipe. The local file service remains responsible for its normal write and cleanup semantics; Kaimo never buffers the complete remote file in memory.

### Local to remote

This is technically possible through a streamed local read and provider upload. It requires a cross-backend transfer coordinator and is not part of the current implementation.

### Cross-backend move (Cut)

A true atomic move cannot be guaranteed between storage systems. It must be implemented as:

1. stream Copy;
2. verify completion and, where possible, size or checksum;
3. delete the source only after successful verification;
4. retain the source if verification or deletion fails;
5. report a recoverable partial-success state.

For that reason, cross-backend Cut should be introduced only after bidirectional Copy is reliable.

## 10. Security requirements

- Credentials must never be stored in plaintext.
- The Data Protection key ring and its automatically generated internal certificate must be persistent and backed up with the database.
- Download URLs must be short-lived, single-use tickets containing no provider credentials.
- Remote paths must be normalized and constrained below the configured root.
- Graph response bodies and tokens must not be exposed in user-facing errors or logs.
- Access must be checked on every operation, not only during directory listing.
- Read-only must be enforced in both the UI and backend.
- Provider redirects and asynchronous monitor URLs must be allow-listed.

## 11. Reliability requirements

- use asynchronous streaming instead of buffering complete files;
- propagate cancellation to the provider;
- honor provider rate-limit responses and `Retry-After`;
- avoid automatic retries for ambiguous mutations unless idempotency is proven;
- use short metadata TTLs and explicit invalidation;
- preserve the source during failed cross-backend transfers;
- keep the web process responsive during provider failures.

## 12. Provider assessment

| Provider | Feasibility | Main concern |
|---|---:|---|
| OneDrive | High | Graph throttling and tenant consent policies |
| Google Drive | High | Google-native document export semantics |
| SMB | Medium | Timeouts, credentials, reconnects, and container isolation |
| NFS | Medium to low for generic Docker | Host privileges, UID/GID mapping, mounts, and Kerberos |

SMB and NFS should not be implemented by mounting arbitrary remote paths directly into the main web container. A controlled helper process or operator-managed mount is safer.

## 13. Delivery phases

### Phase 1 — Domain and security

- additive database migration;
- encrypted credential storage;
- repositories and management permission;
- root containment and path normalization;
- share-level authorization tests.

### Phase 2 — OneDrive backend

- device-code authorization;
- provider adapter;
- stable remote root selection;
- metadata cache;
- list, stream, upload, mkdir, delete, move, rename, and copy;
- token rotation, pagination, cancellation, and throttling tests.

### Phase 3 — Management UI

- provider dropdown;
- connection management;
- folder picker;
- share details tabs;
- access principal dropdown;
- English and German resource files;
- responsive layout.

### Phase 4 — File browser integration

- shared browser UI with backend-specific view models;
- local and virtual share overview;
- on-demand preview and download;
- remote-to-local copy;
- capability-based action visibility.

### Phase 5 — Hardening

- large-file and long-running transfer tests;
- external-change and conflict tests;
- audit and metrics;
- rollout and rollback verification;
- optional bidirectional cross-share transfer coordinator.

## 14. Testing strategy

### Unit and contract tests

- path normalization and root escape attempts;
- access denied without a grant;
- read-only enforcement;
- local/virtual share-name collisions;
- directory-cache hits, invalidation, cloning, and concurrent-load coalescing;
- no secret serialization;
- provider capability behavior.

### Provider integration tests

- pagination;
- Unicode and special-character names;
- zero-byte and large files;
- expired token and token rotation;
- throttling;
- cancellation during upload and download;
- external rename between List and Open;
- move/copy conflicts.

### End-to-end tests

- administrator creates and authorizes a connection;
- administrator selects a remote folder and grants a group;
- an authorized user can browse and an unauthorized user cannot;
- content is transferred only on a real content operation;
- remote-to-local copy produces a complete local file;
- a virtual share never appears in Samba enumeration;
- deleting a mapping never deletes its remote root.

## 15. Operational visibility

Recommended metrics:

- provider listing latency and error rate;
- metadata cache hit rate;
- provider throttling and authentication failures;
- active and failed transfers;
- transferred bytes;
- cancellation and partial-transfer cleanup failures;
- connection and virtual-share health.

Logs must identify the provider, connection, share, and operation without logging tokens or sensitive remote paths.

## 16. Main risks

| Priority | Risk | Mitigation |
|---|---|---|
| P0 | Plaintext remote credentials | Data Protection before persistence |
| P0 | Remote-root escape | Central path normalization and stable root IDs |
| P0 | Authorization checked only at listing time | Check every operation and download ticket |
| P1 | Whole files buffered in memory | Streaming with bounded buffers |
| P1 | Ambiguous mutation retried twice | Re-read state and avoid unsafe retries |
| P1 | Provider outage blocks the UI | Async I/O, cancellation, timeout policy, isolation |
| P1 | Virtual share exported by Samba | Separate entities and regression tests |
| P1 | Cross-backend Cut loses data | Copy, verify, then delete; preserve on uncertainty |
| P2 | Stale remote metadata | Short TTL, invalidation, and explicit Refresh |

## 17. Current implementation status

Implemented for OneDrive:

- encrypted public-client authorization;
- virtual share and grant persistence;
- provider/root picker;
- complete-share read/write or read-only access;
- local and virtual browser integration;
- streamed preview, download, and upload;
- remote operations within a virtual share;
- streamed OneDrive-to-local copy;
- short-lived metadata caching with mutation invalidation;
- English and German UI resources.

Not yet implemented:

- local-to-remote copy;
- copy between two different virtual shares or providers;
- cross-backend Cut;
- Google Drive, SMB, or NFS providers;
- distributed metadata cache for multiple web instances.

## 18. Recommendation

Continue treating Cloud Access as a web storage gateway rather than a local filesystem extension. The next valuable functional step is a backend-neutral transfer coordinator for Copy between local and virtual shares in both directions. Cross-backend Cut should follow only after streamed Copy, verification, cancellation, conflict handling, and partial-failure reporting are proven.

## 19. References

- [Microsoft Graph driveItem](https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0)
- [Microsoft Graph move driveItem](https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0)
- [Microsoft Graph copy driveItem](https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0)
- [Google Drive API v3](https://developers.google.com/drive/api/reference/rest/v3)
- [Google Drive API limits](https://developers.google.com/workspace/drive/api/guides/limits)
