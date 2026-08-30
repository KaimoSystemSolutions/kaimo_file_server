# File browser create hash

The web file browser can compute a cryptographic checksum of a single file and show it
in a small dialog. This lets a user verify a file's integrity — for example, comparing
it against a published checksum after a download — without leaving the browser.

## How the dialog is triggered

Right-clicking a file offers a **Create hash** command (German: *Hash erstellen*). It is
available for single files of any type; it is not offered for folders, the background,
or a multi-selection. Choosing it opens the **Create hash** dialog for that file.

The command is only shown on local Kaimo shares, because the hash is computed
server-side by streaming the file's content. Remote backends (Cloud Access virtual
shares) do not expose it.

## The dialog

- An **Algorithm** dropdown lists the common file-integrity checksums: SHA-256, SHA-1,
  SHA-512, MD5, and SHA-384. **SHA-256 is preselected** as the most widely used file
  checksum.
- The digest is computed as soon as the dialog opens and again whenever a different
  algorithm is selected, and is shown as a lowercase hex string in the **Hash value**
  field.
- A **Copy** button places the digest on the clipboard.

Each algorithm's result is cached per open file, so re-selecting an algorithm that was
already computed is instant. Selecting a new algorithm while a previous computation is
still running discards the stale result — only the digest for the currently selected
algorithm is ever displayed.

## Hashing flow

`FileBrowserViewModel.ComputeFileHashAsync(file, algorithm)` streams the file through
the ACL-enforcing `IFileService.ReadFileAsync` and an `IncrementalHash`, so a file of
any size is hashed in a single streaming pass and is never buffered whole in memory. It
returns the lowercase hex digest, or `null` when the backend cannot read file content
(the default `IFileBrowserViewModel` implementation returns `null`; only the local
view model overrides it). A `null` result is surfaced in the dialog as an error message.

## Localization

The context-menu label uses the `Context_Menu_CreateHash` resource key; the dialog uses
the `Web_Hash_Algorithm`, `Web_Hash_Value`, `Web_Hash_Computing`, `Web_Hash_Error`,
`Web_Hash_Copy`, and `Web_Hash_Copied` keys. All are defined in `Resources.resx`
(English) and `Resources.de.resx` (German). The **Create hash** command appears in the
default context-menu layout for files and can be reordered or hidden per scope in
**Settings → Context menu** like any other command.
