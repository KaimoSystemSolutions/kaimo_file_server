# Connection caching and revocation

`CloudProviderFactory` caches one authenticated `ICloudConnection` per
`(ShareId, ProviderId, ConnectionId?, CredentialFingerprint)`. The cache exists so a
sync run reuses an authenticated connection instead of re-authenticating on every
operation. This document describes the two very different ways a cached connection is
torn down, and why the distinction matters.

## Close vs. revoke — the contract

`ICloudConnection` has two teardown methods, and they are **not** interchangeable:

| Method | What it does | When to use |
| --- | --- | --- |
| `CloseAsync()` | Releases **local** resources only — HTTP clients, SDK service objects, locks. Never contacts the provider, never invalidates a credential. | Cache eviction, losing a construction race, share/sync teardown. |
| `RevokeAndCloseAsync()` | Revokes the persisted grant **at the provider** (where supported), then closes. | A deliberate, user-initiated disconnect. |

**Evicting a cached connection never revokes a grant. Only an explicit disconnect
does.** This is the whole point of the split: before it, a single `Dispose()` meant
both things, and on Google it revoked the refresh token — so the cache could not be
cleaned up safely at all (eviction would have logged the user out of their own drive).

## Eviction methods on the factory

- `EvictAsync(shareId, folder)` — drop one cached connection, closing it.
- `EvictShareAsync(shareId)` — drop every connection for a share (share delete, sync
  delete). Coarse but safe; re-creation on next use is cheap.
- `EvictConnectionAsync(connectionId)` — drop every connection carrying a connection id.
- `RevokeAndEvictAsync(shareId, folder)` — the only method that revokes; used by the
  storage-connection *disconnect* path.

## Call sites

- **Storage-connection delete** (`CloudAccessViewModel.DeleteConnectionAsync`) →
  `provider.RevokeAsync` → `RevokeAndEvictAsync`. The user asked to disconnect, so the
  grant is revoked.
- **Share delete** (`ShareListViewModel`) and **sync-definition delete**
  (`ExternalStorageSyncViewModel`) → `EvictShareAsync`. The connection is closed, never
  revoked — the user did not ask to give up their cloud authorization.
- **Re-authorization** of an existing connection id should call
  `EvictConnectionAsync(connectionId)` so the next use rebuilds with the fresh
  credential instead of the cached connection's stale one. Note that an ordinary token
  *rotation* (silent refresh) must **not** evict: the cache key keeps a stable
  fingerprint while a connection id is present precisely so rotation reuses the live
  connection.

## The construction race

`CreateOrLoad` may build a connection and then lose the `GetOrAdd` to another thread.
The loser is closed with `CloseAsync` (never revoked — that would invalidate the
credential the winner is now using) and the winner is returned.
