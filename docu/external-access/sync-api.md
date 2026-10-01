# Client Sync API

> **Audience:** Native client apps — Android, iOS, and desktop (Linux/Windows/macOS)
> **Base path:** `/api/v1/sync`
> **Controller:** `src/Kaimo_File_Server.Web/Controllers/Api/SyncApiController.cs`

Part of the [REST API v1](rest-api.md); authentication, devices, browsing (uploads,
downloads, conditional requests, idempotency keys), error envelope and path rules are
described there.

The server stores *what* each device syncs; the sync *loop* (diffing, conflict
handling) runs on the client. The server only exposes efficient primitives — delta
enumeration, an incremental change feed and a long-poll wait — and stores each
device's sync selection.

Related: [Storage and persistence](../architecture/storage-and-persistence.md) (change log) ·
[Security model](../architecture/security-model.md)

## Contents
1. [Design rules](#1-design-rules)
2. [Concepts](#2-concepts)
3. [Devices & connections](#3-devices--connections)
4. [Delta enumeration](#4-delta-enumeration)
5. [Incremental change feed (`change_seq`)](#5-incremental-change-feed-change_seq)
6. [Change wait (long-poll)](#6-change-wait-long-poll)
7. [Change notification & battery model](#7-change-notification--battery-model)

---

## 1. Design rules

These rules are fixed and must be preserved by all clients and by the server:

1. **Device sync only — not cloud sync.** This API creates *device ↔ server* sync
   connections. It is unrelated to the server ↔ cloud-provider sync
   (`SyncDefinition`/OneDrive/Google); the two never mix.
2. **Connections are created only from the client.** A sync connection is created,
   edited, and deleted exclusively by a client app through this API
   (`/api/v1/sync/profiles`). Nothing else creates them.
3. **The server side is read-only for connections.** No server-side component creates or
   edits a connection. Administratively, an operator holding the `ManageClientDevices`
   permission can list every user's devices (and, for reference, each device's sync
   selections) and revoke a device (see rule 5). Revoking drops that device's
   connections but never mints or edits them.
4. **Two endpoints per connection.** Every connection pairs a **remote** endpoint
   (`shareId` + `relativePath`, a share subtree) with a **local** endpoint
   (`localPath`, a folder on the device). The client picks both: the remote folder
   from the shares it may browse, and the local folder from its own filesystem.
   `localPath` is opaque to the server — it is stored verbatim and never resolved,
   validated, or accessed server-side. Direction (`Pull`/`Push`/`TwoWay`) applies to
   the pair.
5. **Admin revocation is immediate.** Revoking a device marks it revoked, revokes all
   its refresh tokens, and deletes its sync selections. Because every client-API token
   carries a `device_id` claim that the server re-checks against the device's active
   state on each request, a revoked device's *access* token stops working at once —
   not only when it expires — and it can no longer refresh. The device must sign in
   again to obtain a fresh registration. Once a device is revoked, the operator can
   also **delete** it outright instead of waiting for the retention prune; deleting
   cascades to its refresh tokens, sync selections, and idempotency receipts
   (`ISyncDeviceRepository.DeleteAsync`). Only revoked devices may be deleted — an
   active one must be revoked first so its tokens are invalidated.
6. **Retired registrations are pruned automatically.** So the device list does not grow
   without bound, a registration that can no longer reach the server is deleted whenever
   the administrative device list is loaded. A device is retired once it has
   been revoked for more than `RevokedRetentionDays` (30), or has not made a single
   authenticated request for `InactivityRetentionDays` (90) — well beyond the 30-day
   refresh-token lifetime, so only devices that would have to sign in from scratch are
   removed. Deleting a device cascades to its refresh tokens, sync selections, and
   idempotency receipts (`ISyncDeviceRepository.DeleteRetiredAsync`). Both windows are
   constants on `ClientDeviceAdminViewModel`.

---

## 2. Concepts

| Term | Meaning |
|---|---|
| **Sync connection** | `DeviceSyncProfile`: one connection pairing a **remote** endpoint (a share + share-relative folder) with a **local** endpoint (a folder on the device, opaque to the server), plus a direction — `Pull` (download-only), `Push` (upload-only), or `TwoWay`. |
| **Change token** | Opaque string fingerprinting a subtree; changes whenever anything under it is added, modified, renamed, or deleted — through any transport. Derived from the change sequence (below). |
| **Change sequence** | A monotonic per-share `seq` (an opaque, ever-increasing integer) assigned to every mutation and recorded in a path-level change log. A client stores the highest `seq` it has seen for a share and fetches only newer entries via `changes?since=`. Bootstrap it from a full `delta` (which returns the current `seq`). |

Shares, devices, tokens, item tags and idempotency keys are defined in the
[REST API concepts](rest-api.md#1-concepts).

---

## 3. Devices & connections
| Method & path | Purpose | Caller |
|---|---|---|
| `GET /api/v1/sync/devices` | The caller's registered devices. | client |
| `GET /api/v1/sync/profiles?deviceId=` | Sync connections (all the caller's, or one device's). | client |
| `POST /api/v1/sync/profiles` | Create a connection. | **client only** |
| `PUT /api/v1/sync/profiles/{id}` | Update a connection. | **client only** |
| `DELETE /api/v1/sync/profiles/{id}` | Remove a connection. | **client only** |

```jsonc
// POST /api/v1/sync/profiles   (client only — see Design rules)
{
  "deviceId": "6f9…",
  "shareId": "1a2…",
  "relativePath": "projects/2026",   // remote endpoint; "" = whole share
  "localPath": "/home/alice/Projects/2026", // local endpoint on the device (opaque to server)
  "mode": "TwoWay",                  // Pull | Push | TwoWay
  "enabled": true
}
```
`relativePath` is the remote endpoint (validated against the share and the user's
ACLs — `403 forbidden` without list access). `localPath` is the device-local
endpoint: the server stores it verbatim and never touches it. The connection roams
across the user's devices and is visible read-only to operators with
`ManageClientDevices`.

---

## 4. Delta enumeration
```
GET /api/v1/sync/{shareId}/delta?path=projects/2026
```
Returns every readable item under the subtree plus a change token and the current
change **sequence**:
```jsonc
{
  "entries": [
    { "path": "projects/2026", "isDirectory": true,  "size": 0,      "modifiedAtUtc": "…" },
    { "path": "projects/2026/a.pdf", "isDirectory": false, "size": 1234, "modifiedAtUtc": "…" }
  ],
  "token": "638…",
  "seq": 4211
}
```
The client diffs `entries` against its last-known state to compute adds/updates/
deletes, then transfers according to the profile's direction:
- **Pull** — download server→device only.
- **Push** — upload device→server only.
- **TwoWay** — both; the client resolves conflicts (e.g. keep-both) as it sees fit.

Transfers use the browse endpoints; replay-safe uploads, renames and deletes use the
[conditional requests and idempotency keys](rest-api.md#31-safe-mutations-conditional-requests--idempotency-keys).

`seq` is the baseline cursor: after this one full enumeration, switch to the
incremental change feed below (`changes?since=<seq>`) and re-enumerate only when
you need to reconcile from scratch.

---

## 5. Incremental change feed (`change_seq`)
```
GET /api/v1/sync/{shareId}/changes?since=4211&path=projects/2026&limit=1000
```
Returns just the mutations recorded under the subtree with a sequence greater than
`since`, in ascending `seq` order — renames and net-zero (one-deleted-one-created)
changes included, which the coarse token alone cannot see:
```jsonc
{
  "changes": [
    { "seq": 4212, "path": "projects/2026/b.pdf", "oldPath": "projects/2026/a.pdf",
      "changeType": "Renamed", "isDirectory": false, "size": 1234, "modifiedAtUtc": "…" },
    { "seq": 4213, "path": "projects/2026/c.pdf", "oldPath": null,
      "changeType": "Modified", "isDirectory": false, "size": 5678, "modifiedAtUtc": "…" }
  ],
  "seq": 4213,
  "truncated": false,
  "reset": false
}
```
- `changeType` is one of `Created`, `Modified`, `Deleted`, `Renamed`, or
  `SubtreeChanged` (a bulk op — archive/unzip/folder change — that the client
  reconciles with a scoped `delta` of `path`).
- `oldPath` is set only on `Renamed` (the source). A rename that moves an item
  **out** of the watched subtree still appears (matched on its old path), so the
  client can apply the disappearance.
- `size` / `modifiedAtUtc` accompany creates and modifies so the client can rebuild
  the item tag without a metadata round trip.
- `seq` in the envelope is the cursor to store next. It advances even when a page is
  empty or was entirely hidden by ACLs, so you never re-request the same range.
- `truncated: true` means more entries remain past `limit` — call again immediately
  with the new `seq`. `limit` defaults to 1000 (max 5000).
- `reset: true` means your `since` cursor is older than the retained change log (the
  device was offline longer than the server's change-log retention window, default
  90 days). The incremental page then has a gap — **discard the cursor and re-bootstrap
  with a full `delta`** rather than trusting `changes`. Normal, regularly-syncing
  clients never see this.

The change log is pruned on a schedule so it cannot grow without bound; the window is
configurable via `ClientSync:ChangeLogRetentionDays` (idempotency receipts via
`ClientSync:RequestReceiptRetentionDays`, default 30; expired refresh tokens via
`ClientSync:RefreshTokenRetentionDays`, default 7 days past expiry). The window is set
comfortably above any realistic offline period, and `reset` is the safety net when a
client exceeds it.

Entries are ACL-filtered like a listing: a live item you may not list is omitted;
deletes and rename-sources (whose ACL is already gone) are included because they
fall under the subtree you were authorized to watch. Requires list access on `path`.

---

## 6. Change wait (long-poll)
```
GET /api/v1/sync/changes/wait?shareId={id}&path=projects/2026&since=638…:42
```
Blocks until the subtree's token differs from `since`, then returns
`{ "token": "…", "changed": true }`; if nothing changes within ~30 s it returns
`changed: false`. The token is the subtree's change **sequence** head, so the wait
wakes on renames and content overwrites too (not only adds/deletes). When
`changed` is true, call `changes?since=<your stored seq>` (or a full `delta`), then
wait again with the new token. The token stays opaque — pass back whatever you last
received. See the battery model below.

The wait is served from the process that holds the request; it polls the change log, so
changes written by any process (Web, SmbBridge) wake it.

`changes/wait` requires list access on the watched subtree, so it cannot be used
to probe for hidden content.

---

## 7. Change notification & battery model

The goal is *fast propagation both ways* without draining mobile batteries.

- **No busy polling.** Instead of repeatedly hitting `delta`, a client issues one
  long-poll `changes/wait` per synced root. It is a single idle HTTP request that
  returns the moment something changes.
- **Transport-agnostic detection.** Every mutation — through the web UI, SMB, or the
  API itself — appends to a per-share change log and advances the subtree's change
  **sequence**, so an upload, rename, or overwrite from one device is noticed by
  every other device watching that subtree.
- **Immediate uploads.** Device→server changes are `PUT` straight away, so the other
  side sees them within one long-poll cycle.
- **Coarse then precise.** The wait only says "the sequence advanced"; the client
  then fetches the precise deltas with `changes?since=`. This keeps the wait cheap
  and the transfer minimal.
- **Renames and net-zero changes are covered.** Because each mutation appends its own
  change-log entry (rather than only nudging an aggregate), a pure **rename/move** and
  a net-zero "one deleted + one created" both advance the sequence and appear in
  `changes?since=`. A periodic full `delta` reconcile remains a useful safety net
  because change-log appends are best-effort.

Client-side guidance: back off `changes/wait` on repeated failures, pause syncing on
metered networks or low battery if the user opts in, and honor each profile's
direction to avoid needless transfers.
