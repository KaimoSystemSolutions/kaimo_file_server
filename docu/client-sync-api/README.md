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
3. **The web UI is display-only for connections.** The web page under `/devices`
   only *shows* which devices/instances are connected to the user's account and, for
   reference, the connections each has. It never creates or edits a connection. The
   one exception is administrative revocation: an operator holding the
   `ManageClientDevices` permission can, from `/admin/devices`, revoke another user's
   device (see rule 5). Revoking drops that device's connections but never mints or
   edits them.
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

## Contents
1. [Concepts](#1-concepts)
2. [Authentication & devices](#2-authentication--devices)
3. [Browsing](#3-browsing)
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
| **Change token** | Opaque string fingerprinting a subtree; changes whenever anything under it is added, modified, or deleted — through any transport. |

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

| Method & path | Purpose |
|---|---|
| `GET /api/v1/browse/shares` | Shares the caller may browse. |
| `GET /api/v1/browse/{shareId}/list?path=` | Directory children (+ `ETag`). |
| `GET /api/v1/browse/{shareId}/metadata?path=` | One item's metadata. |
| `GET /api/v1/browse/{shareId}/content?path=` | Download; supports `Range`. |
| `PUT /api/v1/browse/{shareId}/content?path=` | Upload/overwrite (body = bytes). |
| `POST /api/v1/browse/{shareId}/directory?path=` | Create a directory. |
| `POST /api/v1/browse/{shareId}/rename` | Body `{ "from": "...", "to": "..." }`. |
| `DELETE /api/v1/browse/{shareId}/item?path=` | Delete (recycle bin if the share has one). |
| `GET /api/v1/browse/{shareId}/versions?path=` | Stored versions of a file (newest first). |
| `GET /api/v1/browse/{shareId}/version-content?path=&timestamp=` | Download one version. |

**Caching / bandwidth.** `list` returns an `ETag`. Send it back as `If-None-Match`;
an unchanged directory answers `304 Not Modified` with an empty body — cheap on
battery and data.

**Large files.** `content` downloads honor HTTP `Range`, so a client can resume or
segment a transfer. Uploads stream straight to disk with an atomic replace.

---

## 4. Sync

The server stores *what* each device syncs; the client runs the actual sync.

### Devices & connections
| Method & path | Purpose | Caller |
|---|---|---|
| `GET /api/v1/sync/devices` | The caller's registered devices. | client + web UI (read) |
| `GET /api/v1/sync/profiles?deviceId=` | Sync connections (all the caller's, or one device's). | client + web UI (read) |
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
across the user's devices and is shown read-only in the web UI.

### Delta enumeration
```
GET /api/v1/sync/{shareId}/delta?path=projects/2026
```
Returns every readable item under the subtree plus a change token:
```jsonc
{
  "entries": [
    { "path": "projects/2026", "isDirectory": true,  "size": 0,      "modifiedAtUtc": "…" },
    { "path": "projects/2026/a.pdf", "isDirectory": false, "size": 1234, "modifiedAtUtc": "…" }
  ],
  "token": "638…:42"
}
```
The client diffs `entries` against its last-known state to compute adds/updates/
deletes, then transfers according to the profile's direction:
- **Pull** — download server→device only.
- **Push** — upload device→server only.
- **TwoWay** — both; the client resolves conflicts (e.g. keep-both) as it sees fit.

### Change wait (long-poll)
```
GET /api/v1/sync/changes/wait?shareId={id}&path=projects/2026&since=638…:42
```
Blocks until the subtree's token differs from `since`, then returns
`{ "token": "…", "changed": true }`; if nothing changes within ~30 s it returns
`changed: false`. Immediately call `delta` when `changed` is true, then wait again
with the new token. See the battery model below.

---

## 5. Change notification & battery model

The goal is *fast propagation both ways* without draining mobile batteries.

- **No busy polling.** Instead of repeatedly hitting `delta`, a client issues one
  long-poll `changes/wait` per synced root. It is a single idle HTTP request that
  returns the moment something changes.
- **Transport-agnostic detection.** The change token is derived from the persisted
  `file_metadata` (newest `ModifiedAt` + item count under the subtree). A change
  made through the web UI, SMB, or the API itself all move the token — so an upload
  from one device is noticed by every other device watching that subtree.
- **Immediate uploads.** Device→server changes are `PUT` straight away, so the other
  side sees them within one long-poll cycle.
- **Coarse then precise.** The token only says "something changed"; the client then
  fetches the precise `delta`. This keeps the wait cheap and the transfer minimal.
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
`invalid_path` / `invalid_request` / `invalid_timestamp` (400), `conflict` (409),
`locked_out` (429). Clients should branch on `code`, not on `message` (the message
is an English developer hint; user-facing text is localized in the app).

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

- **Native push** (FCM/APNs) for battery-optimal mobile wake-ups.
- **Content hashing on live files** so sync can skip unchanged content and detect
  true edits robustly (today it uses size + modified-time; stored versions already
  carry SHA-256).
- **Automation / remote-management API** as a separate surface with scoped API keys,
  tied to the management permission model.
