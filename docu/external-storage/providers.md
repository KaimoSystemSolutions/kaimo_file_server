# External Storage Providers

This document describes each registered external-storage provider: how it authenticates, which
capabilities it offers and how it reaches the remote side. All providers implement
`IStorageConnectionProvider` and are registered in the Web process (`AddExternalStorageProviders` in
`src/Kaimo_File_Server.Infrastructure/ServiceCollectionExtensions.cs` and the cloud adapters in
`src/Kaimo_File_Server.Web/Program.cs`).

Related: [Connections and credentials](connections-and-credentials.md) ·
[Sync engine](sync-engine.md) · [Virtual shares](virtual-shares.md)

## Overview

| Provider ID | Transport | Authorization mode | Item-level file store (`IRemoteFileStore`) | Sync | Virtual share |
|---|---|---|---|---|---|
| `onedrive` | Microsoft Graph (HTTPS) | `DeviceCode` | Yes | Pull, Push, TwoWay | Yes |
| `dropbox` | Dropbox API (HTTPS) | `DelegatedAuthorizationCode` (PKCE, no redirect) | Yes | Pull, Push, TwoWay | Yes |
| `google` | Google Drive API (HTTPS) | Stored OAuth user grant | Yes | Pull, Push, TwoWay | Yes |
| `smb` | `smbclient` | `UsernamePassword` | Yes | Pull, Push, TwoWay | Yes |
| `sftp` | SSH.NET (in process) | `SshKey` | Yes | Pull, Push, TwoWay | Yes |
| `webdav` | HTTPS (in process) | `UsernamePassword` | Yes | Pull, Push, TwoWay | Yes |
| `rsync-ssh` | `rsync` over `ssh` | `SshKey` | No (`IOptimizedStorageSync`) | Pull, Push | No |

The Web image installs `smbclient`, `rsync` and `openssh-client` for the helper-process providers.

## Cloud providers

The cloud providers are implemented as `ICloudConnection` / `ICloudProvider` classes in
`src/Kaimo_File_Server.Infrastructure/Clouds/` and exposed through `LegacyCloudStorageConnectionProvider`
adapters, which bind them to `StorageConnection` and the credential vault.

### Microsoft OneDrive

- **Authorization:** OAuth 2.0 device-code flow against a public client (no client secret). Defaults:
  the shipped public client ID and the `common` authority; scope
  `offline_access Files.ReadWrite User.Read`. Overrides: see
  [Microsoft OneDrive setup](../operations/microsoft-onedrive-setup.md) (`MicrosoftIdentityConfiguration`).
- **Device sessions** are persisted in `storage_device_authorization_sessions`, so polling survives a
  restart and works across instances.
- **Tokens:** refresh tokens rotate; each rotation is persisted under the connection lease before it
  is acknowledged (see [Connections and credentials](connections-and-credentials.md)).
- **Operations:** list, download, upload, create folder, delete (recursive for folders) via Graph.

### Dropbox

- **Authorization:** OAuth 2.0 authorization-code flow with PKCE (`S256`) and **no redirect URI**:
  Dropbox shows a code that the administrator pastes back; the server exchanges it with the
  server-side verifier for an offline refresh token. Only the public app key is needed
  (`ExternalStorage__Dropbox__AppKey` or the shipped default), no app secret and no inbound callback.
- **Verification:** a new connection is marked `Ready` only after both the account
  (`account_info.read`) and a root listing (`files.metadata.read`) succeed; a grant that cannot
  browse is marked `NeedsReauthorization`.
- **Tokens:** Dropbox refresh tokens are long-lived and not rotated; access tokens live in memory.
- **Operations:** list, download, upload (upload sessions above ~140 MB), create folder, delete.
- **Errors:** structured Dropbox errors are reduced to allow-listed codes; plaintext HTTP 400 bodies
  are redacted, length-bounded and appended to the sanitized code.
- Code: `DropboxConnection.cs`, `DropboxAuthorizationService`, `CloudAccessDropboxController`.

### Google Drive

- **Authorization:** uses a stored OAuth user grant (refresh token) held in the credential vault.
  Google connections originate from the import of legacy share-embedded sync mappings
  ([Sync engine](sync-engine.md#legacy-import)); the Web host does not offer an interactive Google
  authorization flow.
- **Configuration validation:** `GoogleIdentityConfiguration` reads `ExternalStorage__Google__*`
  settings at startup and rejects contradictory secret configuration (a direct secret and a secret
  file at the same time).
- **Operations:** list, download, upload, create folder, delete (resolves the item ID, then
  `Files.Delete`).

## Protocol providers

Protocol providers live in `src/Kaimo_File_Server.Infrastructure/ExternalStorage/`. Non-secret
settings are stored in `StorageConnection.SettingsJson`; passwords and private keys in the protected
credential payload.

### SMB (`MountedProtocolProviders.cs`)

- Executes `smbclient` through `ProtocolCommandRunner` with a private, short-lived authentication
  file (`-A`) instead of command-line credentials; supports domain, port and a minimum protocol
  version.
- Implements `IRemoteFileStore` and therefore backs virtual shares and syncs
  (`RemoteFileStoreSyncAdapter`).

### SFTP (`SftpStorageConnectionProvider.cs`)

- Uses the managed SSH.NET client. The private key is loaded from the vault into memory and never
  written to disk.
- **Host identity** is pinned in process: the server key is accepted only if its SHA-256 fingerprint
  equals `ExpectedHostKeySha256`. A mismatch is an identity failure, not a transient error.
- Paths are normalized, traversal is rejected, every request is resolved beneath `RemoteRoot`, and
  symbolic links are not traversed.

### rsync over SSH (`RsyncSshStorageConnectionProvider.cs`, `RsyncSshSetupService.cs`)

- Sync-only bulk transport via `IOptimizedStorageSync`; **Pull** or **Push** only (two-way is
  rejected because rsync has no conflict detection). No browsing, no virtual shares. The rsync daemon
  protocol is not offered.
- `--delete` is never passed. File-size limits, extension exclusions and bandwidth limits are
  translated to rsync arguments; transfers run with `--timeout`.
- SSH runs with batch mode, strict host-key checking, and password and keyboard-interactive
  authentication disabled. Each operation materializes the private key into a random mode-`0600`
  temporary file that is deleted when the session is disposed. Passphrase-protected keys are rejected.
- The host key is pinned against a dedicated known-hosts entry for the exact host and port, stored
  under `/data/kaimo-system/.external-storage/rsync-ssh/`; hashed known-host entries are rejected.
  `ssh-keyscan` is used only to discover candidate keys; the administrator confirms the fingerprint.
- Alternatively, key and known-hosts file can be operator-managed secret files
  (`PrivateKeySecretReference`, `KnownHostsSecretReference`, absolute paths with restrictive
  permissions).
- Local paths are resolved under the share root; symbolic or reparse-point roots are rejected before
  a helper process starts.

SFTP and rsync share the settings shape:

```json
{
  "Host": "backup.example.test",
  "Port": 22,
  "Username": "kaimo_backup",
  "RemoteRoot": "/srv/archive",
  "ExpectedHostKeySha256": "SHA256:BASE64_FINGERPRINT"
}
```

### WebDAV (`WebDavStorageConnectionProvider.cs`)

- HTTP(S) client with username/password. The server URL is operator input, so the client checks the
  resolved address when the socket connects (after DNS, defeating rebinding) and refuses loopback and
  link-local targets such as cloud metadata endpoints; private LAN addresses are allowed. Redirects
  are not followed.
- Long transfers use an inactivity timeout between chunks rather than a total timeout.
- Implements `IRemoteFileStore`; backs virtual shares and syncs.

## Health checks

`IStorageConnectionProvider.TestAsync` returns only sanitized result codes. Helper-process output and
provider response bodies are never surfaced.
