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

## Provider consolidation packages

### Package 3: Microsoft provider consolidation — implemented foundation

- [x] Preserve device authorization as the zero-configuration default.
- [x] Add validated tenant-owned public-client ID and authority overrides for Cloud Access and legacy Cloud Sync token requests.
- [x] Route Cloud Access authorization verification through the shared OneDrive connection factory.
- [ ] Adapt Cloud Access OneDrive to the final `IStorageConnectionProvider` contracts.
- [ ] Move Cloud Sync OneDrive to connection references. This depends on Package 5's `SyncDefinition` migration; the legacy reader now shares the same Microsoft identity configuration but still owns its legacy grant.

The optional override uses `ExternalStorage__Microsoft__PublicClientId` and
`ExternalStorage__Microsoft__Authority`. Both values are public identifiers. The application validates the
client ID as a GUID and restricts the authority to a tenant identifier, domain, or Microsoft authority alias;
invalid configuration fails startup rather than sending tokens to an arbitrary endpoint. The default remains
the shipped public client and `common` authority, so a normal installation requires no Microsoft OAuth settings.

### Package 4: Google provider consolidation — implemented

- [x] Replace the manual controller token exchange with Google's maintained PKCE-capable .NET client.
- [x] Load customer-owned OAuth and Workspace credentials from absolute, protected secret-file paths.
- [x] Use the configured external base URL instead of request headers to construct the exact callback URI.
- [x] Add selected-item, read-only, and read/write Drive scope profiles.
- [x] Store the PKCE verifier, callback URI, and scopes in a Data-Protection-protected database transaction.
- [x] Use the opaque single-use transaction token directly as OAuth state and bind it to the actor, department, share, and path.
- [x] Add an explicit Workspace service-account mode with optional impersonated subject.
- [x] Add English and German authorization responses plus operator documentation.

Migration `GoogleOAuthProtectedContext` adds a nullable protected-context column to the existing shared
authorization transaction table. This is additive and does not rewrite active connection or sync records.
The Google callback atomically consumes this row before exchanging the authorization code, so replay and
cross-instance completion use the same database guarantee as the earlier shared runtime.

New delegated sync grants record their selected scope profile and authorization mode. The legacy Cloud Sync
shape still owns the refresh token until Package 5 can create the corresponding encrypted `StorageConnection`
and `SyncDefinition` in one restart-safe migration. Workspace credential files are supported by a dedicated
runtime factory that cannot be selected through legacy share JSON. Activation and its department-scoped
authorization checks depend on Package 5's first-class connection workflow rather than copying key material
or deployment identity selection into legacy settings.

## Current focus

### Package 5: first-class sync definitions — implemented additive cutover

- [x] Add `SyncDefinition` and separate runtime state.
- [x] Import schedules, paths, filters, and bandwidth settings from share JSON.
- [x] Create one connection per legacy sync mapping and encrypt its grant.
- [x] Switch manual execution and scheduling to the first-class definitions.
- [x] Count sync consumers and block deletion through repository checks and restrictive foreign keys.
- [ ] Remove plaintext fallback token material after a verified production cutover.

Migration `FirstClassSyncDefinitions` adds the configuration and runtime tables without deleting or rewriting
legacy share JSON. The restart-safe compatibility importer uses a unique share/path key and source checksum,
creates every connection in the local share's department, and protects the complete legacy provider grant with
the context-bound vault. Removed legacy mappings disable their imported definition while retaining the
connection for recovery. Runtime refresh-token rotation is persisted to the protected connection before the
provider acknowledges it. See
[`package-5-first-class-sync-definitions.md`](package-5-first-class-sync-definitions.md) for rollout details.

The final unchecked item is deliberately deferred: the legacy editor remains the compatibility writer until
the unified Package 6 administration UI is available. Package 8 performs the irreversible JSON cleanup only
after operational verification. No user-facing text was added in Package 5, so no new resource keys were needed.

### Package 6: unified administration UI — implemented foundation

- [x] Add one External Storage navigation entry with Syncs and Connections tabs.
- [x] Move virtual-share administration under Local Shares / Virtual Shares tabs.
- [x] Make the sync editor read and write first-class `SyncDefinition` records.
- [x] Add connection-first creation for syncs and virtual shares.
- [x] Show connection identity, authorization, scope, health, verification, and usage details.
- [x] Add test, authorize/reauthorize, disable/enable, and guarded delete actions.
- [x] Add provider-neutral remote-folder browsing with safe rotated-grant persistence.
- [x] Add all Package 6 labels and messages to English and German resource files.
- [x] Render provider-declared capabilities after the final `IStorageConnectionProvider` contract replaces the legacy cloud adapter.
- [ ] Link a connection audit summary after durable external-storage audit events are introduced.

The additive compatibility boundary remains intact. Editing an imported sync makes the first-class row
authoritative; deleting one retains a disabled tombstone. The Package 5 importer now observes all share/path
keys and cannot overwrite or recreate a Package 6-owned record from retained legacy JSON. See
[`package-6-unified-administration-ui.md`](package-6-unified-administration-ui.md) for behavior and rollout notes.
Package 7 completed the capability-driven detail and action rendering without provider-ID branches. The remaining
audit item intentionally does not present normal application logs as an immutable audit trail; it depends on the
still-open durable audit work.

### Package 7: protocol providers — implemented

- [x] Add the provider-neutral capability, health, session, remote-file, and optimized-sync contracts.
- [x] Add SMB 3+ through verified operator-managed mounts with mandatory signing and encryption assurances.
- [x] Add NFSv4+ only through verified operator-managed mounts and host-allowlist assurances.
- [x] Add rsync/SSH with pinned host identity and absolute secret-file references.
- [x] Drive generic connection and sync actions from capabilities and enforce unsupported operations in the backend.
- [x] Add provider contract and fail-closed identity/configuration tests.

SMB and NFS participate in the existing ACL-aware reconciliation engine through a neutral remote-file adapter.
The rsync/SSH process adapter is registered and health-checkable, but deliberately advertises only optimized
sync rather than generic sync: direct process execution would bypass application ACL and version-history rules.
The UI therefore does not offer a sync action for it until an isolated helper preserves those guarantees. See
[`package-7-protocol-providers.md`](package-7-protocol-providers.md) for settings, attestation, and rollout details.

### Package 8 — planned

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
| 2026-08-18 | Package 3 Microsoft identity tests | 16 passed, 0 failed, 0 skipped. |
| 2026-08-18 | Package 3 full `Kaimo_File_Server.Tests` suite | 784 passed, 0 failed, 0 skipped. |
| 2026-08-18 | Package 4 Google identity and authorization transaction tests | 15 passed, 0 failed, 0 skipped. |
| 2026-08-18 | Package 4 full `Kaimo_File_Server.Tests` suite | 797 passed, 0 failed, 0 skipped. |
| 2026-08-18 | Package 5 first-class sync migration and execution tests | 16 passed, 0 failed, 0 skipped. |
| 2026-08-18 | Package 5 full `Kaimo_File_Server.Tests` suite | 801 passed, 0 failed, 0 skipped. |
| 2026-08-18 | Package 5 EF Core model check | No pending model changes after `FirstClassSyncDefinitions`. |
| 2026-08-19 | Package 6 focused sync cutover, repository, and connection UI tests | 10 passed, 0 failed, 0 skipped. |
| 2026-08-19 | Package 6 full `Kaimo_File_Server.Tests` suite | 804 passed, 0 failed, 0 skipped. |
| 2026-08-19 | Package 7 protocol provider contract tests | 6 passed, 0 failed, 0 skipped. |
| 2026-08-19 | Package 7 full `Kaimo_File_Server.Tests` suite | 810 passed, 0 failed, 0 skipped. |
