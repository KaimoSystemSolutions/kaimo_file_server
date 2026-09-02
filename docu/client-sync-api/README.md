# Client Sync & Browse API

> **Status:** Implemented (v1)
> **Audience:** Native client apps — Android, iOS, and desktop (Linux/Windows/macOS)
> **Base path:** `/api/v1`
> **Transport:** REST/JSON over HTTPS, JWT bearer auth
> **OpenAPI document:** `GET /openapi/v1.json` (use it to generate a typed client per platform)

This API lets end-user apps browse the file tree live — exactly what a user is
allowed to see through their ACLs — and configure, per device, which folders sync
and in which direction. It is a thin HTTP transport over the existing, ACL-checked
[`IFileService`](../../src/Kaimo_File_Server.Core/Services/File/IFileService.cs); it
never bypasses permissions. The sync *loop* (diffing, conflict handling) runs on the
client. The server only exposes efficient primitives and stores each device's sync
selection.

It is intentionally separate from the future automation / remote-management API,
which will live under its own surface with its own scoped credentials.

## Design rules (device sync)

These rules are fixed and must be preserved by all clients and by the server:

1. **Device sync only — not cloud sync.** This API creates *device ↔ server* sync
   connections. It is unrelated to the server ↔ cloud-provider sync
   (`SyncDefinition`/OneDrive/Google); the two never mix.
2. **Connections are created only from the client.** A sync connection is created,
   edited, and deleted exclusively by a client app through this API
   (`/api/v1/sync/profiles`). Nothing else creates them.
3. **The web UI is display-only for connections.** The web UI never creates or edits a
   connection. The one surface that touches devices is administrative: an operator
   holding the `ManageClientDevices` permission can, from **Settings → Client devices**,
   review every user's connected devices/instances (and, for reference, each device's
   sync selections) and revoke a device (see rule 5). Revoking drops that device's
   connections but never mints or edits them. (The former self-service `/devices` page
   was removed — a user configures their own sync connections in the client apps, not on
   the web.)
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
   again to obtain a fresh registration.
6. **Retired registrations are pruned automatically.** So the admin list does not grow
   without bound, a registration that can no longer reach the server is deleted when
   the operator opens **Settings → Client devices**. A device is retired once it has
   been revoked for more than `RevokedRetentionDays` (30), or has not made a single
   authenticated request for `InactivityRetentionDays` (90) — well beyond the 30-day
   refresh-token lifetime, so only devices that would have to sign in from scratch are
   removed. Deleting a device cascades to its refresh tokens, sync selections, and
   idempotency receipts (`ISyncDeviceRepository.DeleteRetiredAsync`). Both windows are
   constants on `ClientDeviceAdminViewModel`.

## Contents
1. [Concepts](#1-concepts)
2. [Authentication & devices](#2-authentication--devices)
3. [Browsing](#3-browsing) · [3.1 Safe mutations: conditional requests & idempotency keys](#31-safe-mutations-conditional-requests--idempotency-keys)
4. [Sync](#4-sync)
5. [Change notification & battery model](#5-change-notification--battery-model)
6. [Error format](#6-error-format)
7. [Security notes](#7-security-notes)
8. [Roadmap](#8-roadmap)

---

## 1. Concepts

| Term | Meaning |
|---|---|
| **Share** | A named root the user has access to. Identified by a GUID. |
| **Share-relative path** | Forward-slash path under a share; root is the empty string `""`. Always validated server-side against traversal. |
| **Device** | One app installation (`SyncDevice`), registered on first login. Refresh tokens and sync selections hang off it. |
| **Access token** | Short-lived JWT presented as `Authorization: Bearer <token>` on every call. |
| **Refresh token** | Long-lived opaque secret used to mint new access tokens. Rotated on every use. |
| **Sync connection** | `DeviceSyncProfile`: one connection pairing a **remote** endpoint (a share + share-relative folder) with a **local** endpoint (a folder on the device, opaque to the server), plus a direction — `Pull` (download-only), `Push` (upload-only), or `TwoWay`. |
| **Change token** | Opaque string fingerprinting a subtree; changes whenever anything under it is added, modified, or deleted — through any transport. Now derived from the change sequence (below), so it also moves on renames and content overwrites. |
| **Change sequence** | A monotonic per-share `seq` (an opaque, ever-increasing integer) assigned to every mutation and recorded in a path-level change log. A client stores the highest `seq` it has seen for a share and fetches only newer entries via `changes?since=`. Bootstrap it from a full `delta` (which returns the current `seq`). |
| **Item tag** | An `ETag`-style validator for one file/directory, `"{size}:{modifiedTicks}"` (quoted). Returned on `metadata`/`content`/upload and reproducible on the client from a delta entry, so it can be sent back as `If-Match` without a metadata round trip. See [§3.1](#31-safe-mutations-conditional-requests--idempotency-keys). |
| **Idempotency key** | A client-chosen, per-device stable id for one mutating operation, sent as the `Idempotency-Key` header. Lets a retried request replay its original outcome instead of re-executing. |

All timestamps are UTC ISO-8601. All enums serialize as their names (e.g. `"TwoWay"`).

---

## 2. Authentication & devices

Login uses the same credential service as the web UI, so brute-force lockout and
username-enumeration resistance are identical.

### `POST /api/v1/auth/login`
```jsonc
// request
{
  "username": "alice",
  "password": "…",
  "deviceId": null,              // send your stored device id on later logins; null on first
  "deviceName": "Alice's Pixel", // used when a new device is created
  "platform": "android"
}
// 200 response
{
  "accessToken": "<jwt>",
  "refreshToken": "<opaque>",
  "expiresInSeconds": 86400,
  "deviceId": "6f9…"            // persist this and send it on subsequent logins/refreshes
}
```
- `401 unauthorized` — invalid credentials.
- `403 forbidden` — account disabled.
- `429 locked_out` — too many attempts; honor the `Retry-After` header.

### `POST /api/v1/auth/refresh`
```jsonc
{ "refreshToken": "<opaque>" }
```
Returns a fresh `accessToken` **and a new `refreshToken`** (rotation) — replace your
stored one. Reusing an already-rotated refresh token is treated as theft: the whole
device's token chain is revoked and you must sign in again (`401`).

### `POST /api/v1/auth/logout`
```jsonc
{ "refreshToken": "<opaque>" }
```
Revokes just that refresh token (this device). `204 No Content`.

### Using the access token
Send `Authorization: Bearer <accessToken>` on every non-login call. When it expires,
call `/auth/refresh`. The server re-resolves the account (and its enabled flag and
ACLs) on refresh and on every request, so a disabled user loses access promptly.

---

## 3. Browsing

All browse endpoints are ACL-checked per call.

| Method & path | Purpose | Conditional / idempotent |
|---|---|---|
| `GET /api/v1/browse/shares` | Shares the caller may browse. | — |
| `GET /api/v1/browse/{shareId}/list?path=` | Directory children (+ `ETag`). | `If-None-Match` → 304 |
| `GET /api/v1/browse/{shareId}/metadata?path=` | One item's metadata (+ item-tag `ETag`). | — |
| `GET /api/v1/browse/{shareId}/content?path=` | Download; supports `Range` (+ item-tag `ETag`). | — |
| `PUT /api/v1/browse/{shareId}/content?path=` | Upload/overwrite (body = bytes). | `If-Match`, `If-None-Match: *`, `Idempotency-Key` |
| `POST /api/v1/browse/{shareId}/directory?path=` | Create a directory. | `Idempotency-Key` |
| `POST /api/v1/browse/{shareId}/rename` | Body `{ "from": "...", "to": "..." }`. | `If-Match`, `Idempotency-Key` |
| `DELETE /api/v1/browse/{shareId}/item?path=` | Delete (recycle bin if the share has one). | `If-Match`, `Idempotency-Key` |
| `GET /api/v1/browse/{shareId}/versions?path=` | Stored versions of a file (newest first). | — |
| `GET /api/v1/browse/{shareId}/version-content?path=&timestamp=` | Download one version. | — |

**Caching / bandwidth.** `list` returns an `ETag` over the whole listing. Send it
back as `If-None-Match`; an unchanged directory answers `304 Not Modified` with an
empty body — cheap on battery and data. `metadata`, `content`, and a successful
upload return a **per-item** `ETag` (the item tag) for use with `If-Match` on the
mutating endpoints — see [§3.1](#31-safe-mutations-conditional-requests--idempotency-keys).

**Large files.** `content` downloads honor HTTP `Range`, so a client can resume or
segment a transfer. Uploads stream straight to disk with an atomic replace.

### 3.1 Safe mutations: conditional requests & idempotency keys

The mutating browse endpoints support two **opt-in** mechanisms that make an
offline-first client's replay safe. Both are strictly opt-in: omit the headers and
behavior is unchanged.

**Conditional requests (`If-Match` / `If-None-Match`) — optimistic concurrency.**
The *item tag* of a file/directory is `"{size}:{modifiedTicks}"` (quoted; `modifiedTicks`
is the UTC `DateTime.Ticks` of its modified time). It is returned as the `ETag` on
`metadata`, `content`, and a successful upload, and is reproducible on the client from
a `delta` entry (`size` + `modifiedAtUtc`), so no extra round trip is needed to build it.

- `PUT content` with `If-Match: "<tag>"` — overwrite **only** if the current item still
  matches; a mismatch (or a missing file) → `412 precondition_failed`.
- `PUT content` with `If-None-Match: *` — **create-only**; if the file already exists → `412`.
- `POST rename` with `If-Match: "<tag>"` — validated against the **source** item; mismatch → `412`.
- `DELETE item` with `If-Match: "<tag>"` — delete only if the item still matches; mismatch → `412`.

`If-Match` also accepts `*` (matches any existing item) and a comma-separated list;
a weak-validator `W/"…"` prefix is tolerated. This is exactly the precondition a
two-way client uses to detect "the other side changed since I last saw it" instead of
silently overwriting.

**Idempotency keys (`Idempotency-Key`) — replay-safe retries.**
Send `Idempotency-Key: <opId>` (a stable, per-device id for the operation) on `PUT content`,
`POST directory`, `POST rename`, or `DELETE item`. The first request executes and the
server records the outcome, scoped to `(device, key)`. A repeat:

- **same key + same request** → the stored status and body are **replayed** (the operation
  does *not* run again) — so a retry after a lost response is safe;
- **same key + different request** → `422 idempotency_key_conflict` (a key can never mask a
  different operation);
- a key longer than 128 chars → `400 invalid_idempotency_key`.

Only deterministic outcomes are stored (`2xx`, `412`, `409`); transient failures
(`401`/`403`/`404`/`5xx`) are **not** stored, so a later retry can still succeed.
Receipts are per-device and are removed when the device is revoked.

**Natural idempotence** (independent of the header) makes replay robust even without a key:

- `DELETE item` on an already-gone item → `204` (not `404`).
- `POST rename` where the source is gone but the destination exists (the rename already
  applied) → `204`.
- `POST directory` on an existing directory → `204`.

**Recommended pattern for the client outbox:** persist a stable `opId` per queued mutation
and the item tag you last saw; on (re)connect, replay each op with
`Idempotency-Key: <opId>` and `If-Match: <tag>`. A `2xx` (or a replayed one) → done; a
`412` → a genuine conflict to resolve (e.g. keep-both); a network error → keep the op and
retry later.

---

## 4. Sync

The server stores *what* each device syncs; the client runs the actual sync.

### Devices & connections
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
across the user's devices and is shown read-only to operators under
**Settings → Client devices**.

### Delta enumeration
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

`seq` is the baseline cursor: after this one full enumeration, switch to the
incremental change feed below (`changes?since=<seq>`) and re-enumerate only when
you need to reconcile from scratch.

### Incremental change feed (`change_seq`)
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

### Change wait (long-poll)
```
GET /api/v1/sync/changes/wait?shareId={id}&path=projects/2026&since=638…:42
```
Blocks until the subtree's token differs from `since`, then returns
`{ "token": "…", "changed": true }`; if nothing changes within ~30 s it returns
`changed: false`. The token is the subtree's change **sequence** head, so the wait
now wakes on renames and content overwrites too (not only adds/deletes). When
`changed` is true, call `changes?since=<your stored seq>` (or a full `delta`), then
wait again with the new token. The token stays opaque — pass back whatever you last
received. See the battery model below.

---

## 5. Change notification & battery model

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
  `changes?since=` — the gap the earlier `newestModified:itemCount` token had is
  closed. A periodic full `delta` reconcile is still worthwhile as a belt-and-braces
  safety net (change-log appends are best-effort), but is no longer required to catch
  remote renames promptly.
- **Future: native push.** A device may register an FCM/APNs push token (stored on
  `SyncDevice`); a later package will send a silent push on change so a backgrounded
  phone can wake, sync briefly, and drop its socket — the most battery-efficient
  path. Long-poll remains the desktop and fallback path.

Client-side guidance: back off `changes/wait` on repeated failures, pause syncing on
metered networks or low battery if the user opts in, and honor each profile's
direction to avoid needless transfers.

---

## 6. Error format

Every error uses a uniform envelope with a **stable, non-localized machine code**:
```jsonc
{ "code": "forbidden", "message": "Access denied." }
```
Common codes: `unauthorized` (401), `forbidden` (403), `not_found` (404),
`invalid_path` / `invalid_request` / `invalid_timestamp` / `invalid_idempotency_key` (400),
`precondition_failed` (412), `idempotency_key_conflict` (422), `conflict` (409),
`locked_out` (429). Clients should branch on `code`, not on `message` (the message
is an English developer hint; user-facing text is localized in the app).

> **OpenAPI note.** The generated `/openapi/v1.json` describes the routes, bodies, and
> success shapes, but the opt-in `If-Match` / `If-None-Match` / `Idempotency-Key` headers
> and the `412` / `422` responses from [§3.1](#31-safe-mutations-conditional-requests--idempotency-keys)
> are documented **here**, not yet as OpenAPI annotations — treat this section as their
> authoritative definition until the annotations are added.

---

## 7. Security notes

- Access tokens are HMAC-SHA256 JWTs validated with the **same** parameters as the
  web UI token path (the algorithm is pinned, so an `alg:none`/asymmetric swap is
  rejected). Authorization is always re-resolved from the database — role claims in
  the token are never trusted for access decisions.
- Refresh tokens are stored only as SHA-256 hashes, are per-device, expire, and
  rotate on every use with reuse detection.
- Every `path` is validated with
  [`ShareRelativePath`](../../src/Kaimo_File_Server.Core/Helpers/ShareRelativePath.cs)
  — absolute paths, control characters, and `..` traversal are rejected before any
  file access.
- `changes/wait` requires list access on the watched subtree, so it cannot be used
  to probe for hidden content.

---

## 8. Roadmap

**Shipped**
- **Replay-safe mutations** — opt-in `If-Match`/`If-None-Match` conditional requests and
  `Idempotency-Key` replay on upload/rename/delete/mkdir, plus natural idempotence
  (see [§3.1](#31-safe-mutations-conditional-requests--idempotency-keys)). This is the server
  foundation for offline-first two-way sync.
- **`change_seq` delta feed** — a monotonic per-share change sequence and path-level change log,
  appended on every mutation through every transport, exposed as
  `GET /sync/{shareId}/changes?since=N` and as the token behind the seq-based `changes/wait`
  (see [§4](#4-sync)). Detects renames and net-zero changes, closing the coarse-token gap.

**Next**
- **Change-log retention** — a background job pruning old `file_change_log` entries
  (`IFileChangeLogRepository.PruneOlderThanAsync`, not yet wired). The retention window must
  exceed the longest expected client-offline period.
- **Client baseline + operation outbox** — a persisted last-synced snapshot and a durable
  device-side journal that replays queued renames/deletes on reconnect using the mechanisms in
  §3.1; enables true two-way delete/rename propagation and conflict handling.
- **Search endpoint** — a thin `GET /api/v1/search` over the ACL-checked `ISearchService`
  (Elasticsearch with filename fallback), server-side rate-limited; clients debounce input.
- **Idempotency receipt pruning** — a background job calling
  `IClientRequestReceiptRepository.PruneOlderThanAsync` (not yet wired).
- **Native push** (FCM/APNs) for battery-optimal mobile wake-ups.
- **Content hashing on live files** so sync can skip unchanged content and detect
  true edits robustly (today it uses size + modified-time; stored versions already
  carry SHA-256).
- **Automation / remote-management API** as a separate surface with scoped API keys,
  tied to the management permission model.
