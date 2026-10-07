# REST API v1

> **Audience:** Native client apps — Android, iOS, and desktop (Linux/Windows/macOS)
> **Base path:** `/api/v1`
> **Transport:** REST/JSON over HTTPS, JWT bearer auth
> **OpenAPI document:** `GET /openapi/v1.json` (use it to generate a typed client per platform)
> **Controllers:** `src/Kaimo_File_Server.Web/Controllers/Api/` (`AuthApiController`, `BrowseApiController`, `SearchApiController`, `SyncApiController`)

This API lets end-user apps browse the file tree live — exactly what a user is
allowed to see through their ACLs — search it, and sync folders to their devices. It
is a thin HTTP transport over the existing, ACL-checked
[`IFileService`](../../src/Kaimo_File_Server.Core/Services/File/IFileService.cs); it
never bypasses permissions.

This document covers what all endpoints share: authentication and devices, browsing,
the error format and security. The feature-specific parts are documented separately:

- [Client Sync API](sync-api.md) — `/api/v1/sync`: device sync connections, delta, change feed, long-poll
- [Search API](search-api.md) — `POST /api/v1/search`

Related: [Security model](../architecture/security-model.md)

## Contents
1. [Concepts](#1-concepts)
2. [Authentication & devices](#2-authentication--devices)
3. [Browsing](#3-browsing) · [3.1 Safe mutations: conditional requests & idempotency keys](#31-safe-mutations-conditional-requests--idempotency-keys)
4. [Error format](#4-error-format)
5. [Security notes](#5-security-notes)

---

## 1. Concepts

| Term | Meaning |
|---|---|
| **Share** | A named root the user has access to. Identified by a GUID. |
| **Share-relative path** | Forward-slash path under a share; root is the empty string `""`. Always validated server-side against traversal. |
| **Device** | One app installation (`SyncDevice`), registered on first login. Refresh tokens and sync selections hang off it. |
| **Access token** | Short-lived JWT presented as `Authorization: Bearer <token>` on every call. |
| **Refresh token** | Long-lived opaque secret used to mint new access tokens. Rotated on every use. |
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
  "platform": "android",
  "hardwareId": "android:9774d56d682e549c" // optional, stable per machine; only its hash is stored
}
// 200 response
{
  "accessToken": "<jwt>",
  "refreshToken": "<opaque>",
  "expiresInSeconds": 86400,
  "deviceId": "6f9…"            // persist this and send it on subsequent logins/refreshes
}
```
- Device resolution (only among the user's own active devices): the sent `deviceId`
  is reused unless its stored hardware hash contradicts `hardwareId`; otherwise a
  device with the same `hardwareId` is reused (e.g. after the client lost its stored
  id); otherwise a new device is created. Different machines are never merged.
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
| `PUT /api/v1/browse/{shareId}/content?path=` | Upload/overwrite (body = bytes). Optional `X-Kaimo-Modified-At` preserves the source mtime. | `If-Match`, `If-None-Match: *`, `Idempotency-Key` |
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

**Preserving the modification time.** By default an upload's modified time is the
write time. A sync client sends `X-Kaimo-Modified-At: <Unix epoch milliseconds, UTC>`
on `PUT content` to keep the source file's mtime; the file, the returned item tag /
`modifiedAtUtc`, and the change-feed entry then carry that time.

- A **new** file always takes the sent time.
- An **overwrite** takes it only when it is *newer* than the stored mtime; otherwise the
  write time is kept (check the returned `modifiedAtUtc`). This keeps the stored time from
  ever moving backwards, which other devices rely on for "newest wins", and guarantees that
  every overwrite changes the `size:mtime` item tag, so `If-Match` and listing ETags still
  detect it.
- A value that is not a non-negative integer (e.g. a pre-1970 time) →
  `400 invalid_modified_at`; omit the header for such files.
- Creation time is not transferred (Linux cannot set it).

### 3.1 Safe mutations: conditional requests & idempotency keys

The mutating browse endpoints support two **opt-in** mechanisms that make an
offline-first client's replay safe. Both are strictly opt-in: omit the headers and
behavior is unchanged.

**Conditional requests (`If-Match` / `If-None-Match`) — optimistic concurrency.**
The *item tag* of a file/directory is `"{size}:{modifiedTicks}"` (quoted; `modifiedTicks`
is the UTC `DateTime.Ticks` of its modified time). It is returned as the `ETag` on
`metadata`, `content`, and a successful upload, and is reproducible on the client from
a [`delta`](sync-api.md#4-delta-enumeration) entry (`size` + `modifiedAtUtc`), so no extra round trip is needed to build it.

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

## 4. Error format

Every error uses a uniform envelope with a **stable, non-localized machine code**:
```jsonc
{ "code": "forbidden", "message": "Access denied." }
```
Common codes: `unauthorized` (401), `forbidden` (403), `not_found` (404),
`invalid_path` / `invalid_request` / `invalid_timestamp` / `invalid_idempotency_key` /
`invalid_query` (400), `precondition_failed` (412), `idempotency_key_conflict` (422),
`conflict` (409), `locked_out` / `rate_limited` (429), `search_timeout` (503). Clients should branch on `code`, not on `message` (the message
is an English developer hint; user-facing text is localized in the app). A body or
parameter that cannot be bound at all (malformed JSON, wrong types, invalid GUIDs)
yields `invalid_request` (400) in the same envelope.

> **OpenAPI note.** The generated `/openapi/v1.json` describes the routes, bodies, and
> success shapes. The opt-in `If-Match` / `If-None-Match` / `Idempotency-Key` headers
> and the `412` / `422` responses from [§3.1](#31-safe-mutations-conditional-requests--idempotency-keys)
> are defined by this document.

---

## 5. Security notes

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
