# Package 6: Unified External-Storage Administration

Package 6 makes the first-class `StorageConnection` and `SyncDefinition`
records the administration surface. The primary navigation now exposes one
**External Storage** destination with **Syncs** and **Connections** tabs. The
Shares page separately presents **Local Shares** and **Virtual Shares**.

## First-class sync editor

The new editor reads and writes `SyncDefinition` directly. It supports reusable
connections, local and provider-neutral remote folder selection, direction,
scheduling, filters, transfer limits, enablement, deletion, and manual runs.
Every mutation revalidates the actor's share permission and the connection's
department-scoped `UseConnections` permission. A connection and local share
must have the same owning department.

Editing an imported definition clears its `MigrationSource`. Deleting an
imported definition retains a disabled authoritative tombstone. The legacy
importer recognizes both states and no longer overwrites or recreates a sync
from rollback JSON after the first-class UI has taken ownership. Native Package
6 definitions can be removed normally. Path edits and deletes acquire the same
operation lease as running syncs.

Remote browsing uses the provider-neutral sync contract. Provider credential
rotation during browsing is merged into the current grant, protected by the
context-bound credential vault, persisted, and only then acknowledged.

## Connection administration

Connection details show provider, authorization mode, account and tenant,
effective scopes, lifecycle state, last verification time, profile assignment,
credential protection health, and sync/virtual-share usage. Supported actions
include test, authorize or reauthorize, disable or enable, guarded deletion,
and connection-first creation links for syncs and virtual shares.

The connection list never exposes credential payloads. Provider failures shown
to administrators continue to pass through the existing sanitized error
boundary; unexpected UI failures use a generic localized message.

Provider-declared capabilities and an immutable audit-summary link remain
explicit follow-up items. The current provider stack has not yet completed the
final `IStorageConnectionProvider` capability contract, and external-storage
audit events do not yet have durable queryable persistence. The UI deliberately
does not infer capabilities from provider IDs or mislabel ordinary application
logs as an audit trail.

## Compatibility and routing

The former `/sync` and `/cloud-access` pages remain available as migration
compatibility routes, but the main navigation points to `/external-storage`.
Legacy share JSON is still retained for rollback and is removed only by the
verified irreversible cleanup package.

All Package 6 user-facing strings are present in the English and German
resource files. Code comments, XML documentation, tests, and this operational
note are written in English.
