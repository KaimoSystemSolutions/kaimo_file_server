# External Storage Connections Implementation Status

This document tracks delivery against the adjacent
[`external-storage-connections-architecture.md`](external-storage-connections-architecture.md).
It records repository work only. Provider-console actions and deployment checks remain explicit operator tasks.

Last updated: **2026-08-18**

## Delivery rules

- Implementation is split into independently buildable work packages.
- New user-facing text must be added to both English and German resource files.
- Code comments, XML documentation, migration notes, and operational documentation are written in English.
- Database changes remain additive and reversible until the final cleanup package.
- The repository owner creates commits; implementation work does not create commits.

## Immediate containment

### Package 0A: immediate credential containment — in progress

- [x] Remove the exposed Google OAuth client ID and client secret from tracked runtime configuration.
- [x] Keep Google OAuth configuration empty by default so an operator must supply installation-local credentials.
- [ ] Revoke or rotate the exposed Google OAuth credential in Google Cloud. This requires the credential owner.
- [ ] Review Git history and published container layers for the exposed credential.
- [ ] Add an automated repository and CI secret scan.
- [ ] Prevent legacy Cloud Sync share JSON and API models from exposing refresh tokens.

The final item depends on the additive `StorageConnection` and `SyncDefinition` migration. Removing the
legacy JSON value before that migration would invalidate existing Cloud Sync connections.

During the compatibility period, an installation can provide the existing keys through
`GoogleOAuth__ClientId` and `GoogleOAuth__ClientSecret`. Secret-file and external-vault resolution is
scheduled for the Google provider consolidation package.

## Completed foundation packages

### Package 0B: context-bound credential vault foundation — implemented

- [x] Introduce the provider-neutral `ICredentialVault` contract and typed `CredentialContext`.
- [x] Bind new ciphertext to connection ID, provider ID, credential kind, and payload format version.
- [x] Wire existing Cloud Access authorization, browsing, download, and transfer paths to the vault.
- [x] Preserve read compatibility for existing `dp:v1` Cloud Access payloads.
- [x] Write all new and rotated Cloud Access grants as context-bound `dp:v2` payloads.
- [x] Add tests for round trips, tampering, cross-connection copies, cross-provider copies, and legacy reads.

Legacy `dp:v1` values are deliberately read-only compatibility input. They are upgraded to `dp:v2` the
next time the provider returns a rotated grant. A bounded rewrap job remains part of the later migration package.

No UI copy was introduced in this package, so no resource-file keys were required.

### Package 1: neutral connection domain — implemented

- [x] Add `StorageConnection`, `ProviderProfile`, connection states, and authorization modes.
- [x] Separate connection persistence from the virtual-share repository.
- [x] Add application-managed optimistic concurrency and stale-write rejection.
- [x] Add usage counts and block deletion while a virtual share references the connection.
- [x] Change the database foreign key from cascade to restrict.
- [x] Rename the existing table and credential column without recreating, decrypting, or logging records.
- [x] Assign existing OneDrive records to the built-in Microsoft public-client profile.
- [x] Add `ManageConnections` and `UseConnections` authorization checks.
- [x] Expose the new permissions and connection states through English and German resource files.
- [x] Add database tests for persistence, concurrency, usage counts, runtime updates, and restrictive deletion.

The migration keeps `ProviderProfileId` nullable during the additive rollout so future legacy provider records
can be classified safely instead of being assigned to the wrong profile. Existing OneDrive records are linked
to the built-in profile automatically.

### Package 2: shared authorization and refresh coordination — implemented

- [x] Persist short-lived authorization transactions with a TTL and store only hashes of browser-visible tokens.
- [x] Persist encrypted OneDrive device-code sessions and coordinate polling across Web instances.
- [x] Add renewable, database-backed per-connection credential leases with expired-owner recovery.
- [x] Reload the latest grant under the lease and persist refresh-token rotation before releasing it.
- [x] Add provider-error sanitization and centralized diagnostic secret redaction.
- [x] Add a bounded, concurrency-safe legacy credential rewrap pass.
- [x] Add backup/restore, rewrap, lease, replay, redaction, and multi-instance tests.

Migration `SharedExternalStorageRuntime` adds three runtime tables without changing existing connection or
consumer records. Authorization tickets and device sessions can therefore move between Web instances without
sticky sessions. Tickets are bound to the initiating user and department and are revalidated by the HTTP
endpoints. Device codes and hand-off context are protected with Data Protection, while ticket and session
identifiers are persisted only as SHA-256 hashes.

The bounded startup rewrap pass upgrades at most 100 legacy `dp:v1` connection grants per Web startup. It holds
the same distributed credential lease used by refresh-token rotation and applies an optimistic concurrency
check before writing. A busy or concurrently changed connection is retained unchanged for a later pass.

No new UI copy was required. Authorization failures continue to resolve through the existing English and German
resource keys. Provider response bodies are converted into allow-listed error codes before they can reach UI,
health state, exceptions, or logs.

## Current focus

### Package 3: Microsoft provider consolidation — planned

- Adapt Cloud Access OneDrive to the storage-provider contracts.
- Move Cloud Sync OneDrive to connection references.
- Preserve device authorization as the default path.
- Add tenant-owned client ID and authority overrides.

### Package 4: Google provider consolidation — planned

- Replace manual token exchange with the maintained Google client library.
- Load customer-owned OAuth credentials through protected deployment configuration.
- Add scope profiles, secure callback transactions, and Workspace service identities.

### Package 5: first-class sync definitions — planned

- Add `SyncDefinition` and separate runtime state.
- Migrate schedules, paths, filters, and bandwidth settings out of share JSON.
- Create one connection per legacy sync mapping and encrypt its grant.
- Remove plaintext token material only after verified cutover.

### Packages 6–8 — planned

- Unify the administration UI and virtual-share placement.
- Add SMB, rsync/SSH, and NFS provider adapters.
- Complete multi-instance hardening, key rotation, migration cleanup, and operational acceptance tests.

## Verification log

| Date | Check | Result |
| --- | --- | --- |
| 2026-08-18 | Project compilation through the solution build | Core, Infrastructure, Host, SMB bridge, Web, and test projects compiled; the solution command itself was blocked by sandbox access to the Docker Compose project SDK lookup. |
| 2026-08-18 | `Kaimo_File_Server.Tests` | 766 passed, 0 failed, 0 skipped. |
| 2026-08-18 | Neutral connection domain build and tests | 771 passed, 0 failed, 0 skipped. |
| 2026-08-18 | EF Core model check | No pending model changes after `NeutralStorageConnections`. |
| 2026-08-18 | PostgreSQL migration script review | Existing connection table and credential column are renamed in place; no connection table drop is emitted. |
| 2026-08-18 | Package 2 focused security and coordination tests | 27 passed, 0 failed, 0 skipped. |
| 2026-08-18 | Package 2 full `Kaimo_File_Server.Tests` suite | 780 passed, 0 failed, 0 skipped. |
