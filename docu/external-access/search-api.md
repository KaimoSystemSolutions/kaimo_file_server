# Search API

> **Audience:** Native client apps — Android, iOS, and desktop
> **Endpoint:** `POST /api/v1/search`
> **Controller:** `src/Kaimo_File_Server.Web/Controllers/Api/SearchApiController.cs`

Part of the [REST API v1](rest-api.md); authentication, error envelope and path rules are
described there.

Related: [Search indexing](../subsystems/search-indexing.md) ·
[Security model](../architecture/security-model.md)

## Request and response

`POST /api/v1/search` searches file names and content (Elasticsearch, falling back to
a file-name-only search when the index is disabled or unreachable). Every hit has
passed the same ACL filter as the web UI (`ListReadData`, fail-closed), so a caller
only ever sees items they may read.

The request is a JSON body rather than a query string, so search terms never land in
URL or reverse-proxy access logs:
```jsonc
{ "q": "invoice 2026",   // required, 2–100 chars, no control characters
  "shareId": "…",        // optional: restrict to one share
  "path": "projects/a",  // optional, requires shareId: that folder and below
                         // (share-relative, no leading "/"; "" = share root)
  "limit": 25 }          // optional, 1–50 (default 25)
```
Response (`Cache-Control: no-store`):
```jsonc
[ { "shareId": "…", "shareName": "docs", "path": "projects/a/invoice.pdf",
    "name": "invoice.pdf", "isDirectory": false, "fileType": "pdf",
    "size": 48213,
    "snippet": [ { "text": "… the ", "highlighted": false },
                 { "text": "invoice", "highlighted": true },
                 { "text": " for …", "highlighted": false } ] } ]
```
- Open a hit through the browse endpoints with `shareId` + `path`. Hits in the
  caller's home folder carry the home share's id and the display name `user`; their
  `path` includes the `RootPath` from `browse/shares`.
- Hits carry no modification time (the index only knows when an item was indexed);
  read timestamps from the browse endpoints.
- `snippet` is pre-split into plain-text segments. Render them as text (e.g. styled
  `TextSpan`s); the API never returns markup.
- An unknown or disabled `shareId` returns an empty list, exactly like a share the
  caller cannot read, so share ids cannot be probed for existence.
- Errors: `invalid_query` / `invalid_path` (400), `rate_limited` (429, honor
  `Retry-After`), `search_timeout` (503, a search is capped at 10 s).
- Rate limit: per user, a burst of 10 requests, then one every 2 s. Clients should
  debounce input (≥ 250 ms), require at least 2 characters, and cancel the previous
  request when the query changes.

## Security notes

Search results are ACL-filtered before they leave the search service and never
include server paths, internal ids or indexed full text. Search text is only ever
used in `match` queries (no Elasticsearch query-DSL injection) and is not logged.
Requests are rate-limited per user and time-boxed, and concurrent file-name walks
(fallback mode) are capped process-wide.
