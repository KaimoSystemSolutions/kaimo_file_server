# Public Upload Links — Implementation Plan

Status: implemented (2026-09-25)

## Goal

Anonymous **upload** links ("file request" / drop box) as the mirror image of the existing
public **download** links (`ShareLink`, `/shared/{token}`). A visitor opens a link and uploads
files into one folder of a share; the upload runs under the link creator's identity and is
ACL-checked like every other write.

Same feature set as download links, reversed:

| Download link (today)              | Upload link (new)                                   |
|------------------------------------|-----------------------------------------------------|
| File or folder target              | Folder target only                                  |
| Optional password (BCrypt, throttled) | same                                             |
| Start / end window, enable switch  | same                                                |
| Max downloads (`MaxAccessCount`)   | Max uploaded files (same column)                    |
| Max rate (`MaxBytesPerSecond`)     | Max upload rate (same column, throttles the read)   |
| —                                  | Max file size, max total bytes, allowed extensions  |
| Base address allowlist             | same                                                |
| Overview page `/share-links`       | same page, second tab **Upload**                    |
| Emblem in file browser             | same, different icon                                |

## Design decision: one entity, a `Kind` discriminator

Upload links reuse `ShareLink` with a new `Kind` column instead of a parallel entity. Token
hashing/protection, password throttling, time window, base address, repository, list view
model, settings, emblem and permission scoping all work unchanged. The alternative (a separate
`UploadLink` table) would duplicate all of that for no gain.

## 1. Domain & persistence

`Kaimo_File_Server.Core/Domain/ShareLink.cs`

```csharp
public enum ShareLinkKind { Download = 0, Upload = 1 }

public ShareLinkKind Kind { get; set; } = ShareLinkKind.Download;   // existing rows = Download

// Upload-only policy (ignored for download links)
public long? MaxFileSizeBytes { get; set; }        // per file; null = global upload limit
public long? MaxTotalBytes { get; set; }           // quota across all uploads; null = unlimited
public long UploadedBytes { get; set; }            // bytes accepted so far
public string? AllowedExtensions { get; set; }     // ".pdf;.jpg"; null = any
```

- `MaxAccessCount` / `AccessCount` = number of uploaded files for an upload link.
- `IsCurrentlyActive` additionally checks `MaxTotalBytes` for upload links.
- EF migration `AddUploadLinks` (default `Kind = 0`, so existing links stay download links).

`IShareLinkRepository` — one new method, mirroring `TryConsumeAccessAsync`:

```csharp
/// Atomically reserves one file slot and `bytes` of quota; null when inactive/exhausted.
Task<ShareLink?> TryReserveUploadAsync(string token, long bytes);
/// Compensates a reservation whose write failed or was cancelled.
Task ReleaseUploadAsync(Guid id, long bytes);
```

Implemented as a single guarded `ExecuteUpdateAsync` like the existing consume
(`Kind == Upload`, enabled, window, `AccessCount < MaxAccessCount`,
`UploadedBytes + bytes <= MaxTotalBytes`) — race-free under parallel visitors.

## 2. Creation (file browser)

- **Context menu**: new entry "Create upload link" shown for **folders** only, same
  `ManageShareLinks` gate as "Share as public link".
- **`ShareLinkDialog.razor`**: gets a `Kind` parameter. For `Upload` it hides nothing of the
  common fields and relabels "Max downloads" → "Max files" / "Max rate" → "Max upload rate",
  and adds max file size, total quota and allowed extensions.
- **`CreateShareLinkRequest`** / **`ShareLinkService.CreateAsync`**: carry `Kind` + the new
  fields. Server-side re-check additionally requires that the creator has
  `FilePermission.CreateWriteData` on the target folder (otherwise the link could never work)
  and that the target is a directory.
- **Emblem**: `_sharedRoots` already covers all links; the emblem shows an upload icon when
  the nearest link is an upload link. `?select=` deep link opens the right tab.

## 3. Overview page `/share-links` — two tabs

`ShareLinkList.razor` gets the shared `<Tabs>` / `<Tab>` strip: **Download** | **Upload**
(counts in the tab label). The list, detail pane and edit form are the same markup, filtered
by `VM.Links.Where(r => r.Link.Kind == _tab)`. Per kind only the labels and a few fields differ:

- List column "Downloads" → "Uploads" (`AccessCount / MaxAccessCount`) plus used quota
  (`UploadedBytes / MaxTotalBytes`).
- Detail "Limits" group: max file size, quota, allowed extensions.
- Edit form: the three upload-only inputs.
- Status badge: `exhausted` also when the byte quota is used up.
- `?select={id}` switches to the tab of the selected link.

No new page, no new view model — `ShareLinkListViewModel` loads all links as today.

## 4. Public page `/shared/{token}`

`PublicShareLink.razor` gets a new state `State.Upload` (after the existing window /
share / creator / password checks, which apply unchanged):

- A drop zone + `<InputFile multiple>` placed **directly on the page** (the public page uses
  `EmptyLayout`, so the global upload input of `MainLayout` / `FileUploadCoordinator` is not
  available — and is not needed).
- Shows the link name, the limits (max size, allowed types, remaining files) and a per-file
  progress list of **this visitor's** uploads (reuses `ProgressStream`).
- **No listing of the folder's existing content** (drop-box semantics — visitors must not see
  what others uploaded).

Upload flow per file (`Services/PublicUploadService.cs`):

1. Validate name: strip any path, `WindowsFileNameHelper.IsValid`, extension allowlist.
2. `size = file.Size`; reject if `> MaxFileSizeBytes` (or the global upload limit).
3. `TryReserveUploadAsync(token, size)` → reject when null (inactive / exhausted).
4. `file.OpenReadStream(maxAllowedSize: size)` — the client-claimed size becomes a hard cap,
   so a lying client cannot exceed its reservation.
5. Wrap with `RateLimitedStream.Wrap(stream, link.MaxBytesPerSecond)`.
6. Target name = first free "name (n).ext" in the folder listing, claimed in an in-process set
   against parallel visitors — **never overwrite**. If the creator cannot list the folder, a
   random suffix is used.
7. `IFileService.WriteFileAsync(target, stream, creatorContext, ct)` — ACL enforced, change log
   → search indexing, versioning as usual.
8. On failure/cancel: `ReleaseUploadAsync(id, size)`. The storage layer writes to a temp file
   and publishes only complete files, so nothing partial remains.

Why the circuit (`InputFile`) and not an HTTP `POST` endpoint: the whole upload stack already
streams over the Blazor circuit (up to 1.1 GB), the password unlock already lives on the
circuit, and there is no ticket/endpoint to secure. A `PublicUploadController` with one-time
tickets (mirroring `PublicDownloadController`) is the upgrade path if very large files or
resumable uploads are ever needed.

## 5. Settings & security

- **Admin setting** in `ShareLinkSettings`: `AllowUploadLinks` (default **false**) and
  `MaxUploadFileSizeBytes` (global per-file ceiling, default and maximum 4 GiB). The context-menu entry, dialog and public page
  honour it; existing upload links stop accepting files when it is switched off.
- **Permission**: separate flag `ManageUploadLinks` (`1L << 57`, part of `ShareAdmin`), so
  download and upload links can be delegated independently. The overview shows each tab only
  with its permission; the nav entry appears with either.
- Creator disabled / share disabled / link disabled → upload refused (same checks as download,
  re-evaluated per file, not only at page load).
- Read-only demo mode: public upload state shows "unavailable" (writes are blocked anyway).
- Each accepted upload is logged (link id, path, size, creator) via `ILogger`; the file itself is
  attributed to the creator in the change log. (`SecurityMonitor` has no channel for this yet.)
- Rate limiting of anonymous circuits beyond the per-link limits (per-IP) is out of scope;
  the byte/file quotas bound the damage per link.

## 6. Localization

All new strings in **both** `Resources.resx` and `Resources.de.resx`, e.g.
`Web_ShareLinks_Tab_Download`, `Web_ShareLinks_Tab_Upload`, `Web_ShareLink_CreateUpload`,
`Web_ShareLink_MaxFiles`, `Web_ShareLink_MaxFileSize`, `Web_ShareLink_MaxTotal`,
`Web_ShareLink_AllowedTypes`, `Web_ShareLinks_Col_Uploads`, `Web_PublicUpload_Title`,
`Web_PublicUpload_Drop`, `Web_PublicUpload_TooLarge`, `Web_PublicUpload_TypeNotAllowed`,
`Web_PublicUpload_QuotaExceeded`, `Web_PublicUpload_Done`, `Web_Settings_AllowUploadLinks`.

## 7. Tests

- `ShareLinkRepositoryTests`: `TryReserveUploadAsync` respects file count, byte quota, window,
  `Kind`; parallel reservations never exceed the caps; `ReleaseUploadAsync` restores quota.
- `ShareLinkPolicyTests`: `IsCurrentlyActive` with byte quota.
- `ShareLinkCreatorTests`: upload link on a file or without write permission is refused.
- Public upload service: path stripping, extension allowlist, no overwrite, oversize stream
  aborted by `maxAllowedSize`.

## Implementation order

1. Domain fields + migration + repository methods + tests.
2. Service: create with `Kind`, upload flow, settings switch.
3. Public page upload state.
4. Dialog + context menu + emblem icon.
5. Overview tabs.
6. Resources (en + de).

## Deliberately out of scope (v1)

- Combined upload + download link on the same folder (use two links).
- Visitor sees folder content / subfolder creation by visitors.
- E-mail notification to the creator on upload.
- Resumable / chunked HTTP uploads (see upgrade path in §4).
- Virus scanning of uploaded files.
