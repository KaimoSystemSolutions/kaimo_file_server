# File browser streaming download

The web file browser can hand a file straight to the browser as a download without
first loading it into the preview dialog. This keeps large files off the server's
memory and lets a user retrieve a file that is too big to preview inline.

## How a download is triggered

There are two entry points, both of which stream the same way:

- **Context menu → Download.** Right-clicking a file offers a **Download** command
  (German: *Herunterladen*). It is available for single files of any type; it is not
  offered for folders, the background, or a multi-selection. Choosing it downloads the
  file immediately, without opening the preview.
- **Preview fallback → Download.** When a file is larger than the inline-preview cap
  (25 MB) or has no inline preview, the preview dialog shows a download button instead
  of content. That button now streams the file even when its bytes were never buffered
  for a preview.

Both paths ask the view model for a one-time download URL and then let the browser
fetch it, so nothing is held in the Blazor server circuit.

## Ticket flow

`FileBrowserViewModel.GetDownloadUrlAsync` issues a single-use ticket through
`FileDownloadTicketStore` and returns `/api/files/download?ticket=…`. The ticket
carries the share id, the signed-in user id, the share-relative path, and the file
name. It is consumed on first use and expires after five minutes.

`FileDownloadController` resolves the ticket back to the user and share, re-normalizes
the path, and streams the file through the ACL-enforcing `IFileService`. Because
access is re-checked at download time — not only when the ticket was issued — a
permission that was revoked in the meantime still blocks the download. The file is
piped directly to the response with `Content-Disposition: attachment`; it is never
read into a server-side byte array.

This mirrors the existing Cloud Access download flow
(`CloudAccessDownloadTicketStore` / `/api/cloud-access/download`); the Cloud Access
file browser continues to use its own ticket path.

## Recycle-bin icon

The trash icon is shown only for the recycle-bin root itself (a top-level
`.RECYCLE_BIN` folder). Folders nested inside the recycle bin are ordinary
directories and keep the normal folder icon.

## Localization

The context-menu label uses the `Context_Menu_Download` resource key, defined in
`Resources.resx` (English) and `Resources.de.resx` (German). The **Download** command
appears in the default context-menu layout and can be reordered or hidden per scope in
**Settings → Context menu** like any other command.
