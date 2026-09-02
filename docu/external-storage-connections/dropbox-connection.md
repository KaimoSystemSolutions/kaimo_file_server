# Dropbox External Storage Connection

Status: **implemented**
Audience: operators and contributors
Last reviewed: **2026-08-31**

Kaimo Files supports Dropbox as an external-storage connection alongside
Microsoft OneDrive and Google Drive. A single Dropbox connection can back both a
**sync** and a **virtual share**, exactly like OneDrive, because syncs and
virtual shares reference a reusable `StorageConnection` rather than owning their
own credentials.

## Authorization model

Dropbox does not offer an OAuth device-code flow. Kaimo therefore uses the
**OAuth 2.0 authorization-code flow with PKCE and no redirect URI**:

1. The operator registers one Dropbox app and obtains its **app key**. A Dropbox
   app key is a public identifier, not a secret, so it can ship in source and be
   shared by every installation — the same principle Kaimo already applies to the
   Microsoft public client ID.
2. When a user creates a Dropbox connection, Kaimo opens Dropbox's consent page
   with a PKCE challenge and `token_access_type=offline`. Because no redirect URI
   is supplied, Dropbox displays a short authorization code.
3. The user copies that code back into Kaimo. The server exchanges it — together
   with the server-side PKCE verifier — for a long-lived refresh token.

This flow requires **no application secret** and **no inbound callback URL**, so
it works on any self-hosted instance without per-instance configuration in the
Dropbox App Console.

## One-time operator setup

These steps are performed once by the person who owns the Dropbox app. Kaimo
cannot automate them because they require a Dropbox account.

1. Open the [Dropbox App Console](https://www.dropbox.com/developers/apps) and
   **Create app**.
2. Choose **Scoped access**.
3. Choose the access type: **Full Dropbox** (browse the whole account) or **App
   folder** (a dedicated per-app folder). Either works; virtual shares and syncs
   operate within whatever the granted scope allows.
4. On the app's **Permissions** tab, enable these scopes and submit:
   - `account_info.read`
   - `files.metadata.read`
   - `files.content.read`
   - `files.content.write`
5. Copy the **App key** from the app's **Settings** tab.

No redirect URI and no app secret are required for the PKCE no-redirect flow.

## Configuring the app key

Provide the app key in either of two ways:

- **In source (shipped default):** set `DefaultAppKey` in
  [`DropboxConnection.cs`](../../src/Kaimo_File_Server.Infrastructure/Clouds/DropboxConnection.cs)
  (`DropboxOAuthDefaults.DefaultAppKey`).
- **Per installation (no rebuild):** set the environment variable
  `ExternalStorage__Dropbox__AppKey=<your app key>`.

The environment variable takes precedence over the shipped default. If neither
is set, Dropbox connections cannot be authorized and the connection page shows a
clear "Dropbox is not configured" message; all other providers are unaffected.

## Connecting a Dropbox account

1. Go to **External Storage → Connections** and choose **Add connection**.
2. Select **Dropbox**, enter a name, and choose the owning department.
3. Choose **Connect**. Kaimo opens the Dropbox authorization page.
4. Select **Open Dropbox sign-in**, approve access, and copy the code Dropbox
   shows.
5. Paste the code into Kaimo and choose **Connect**. Kaimo verifies both the
   account (`account_info.read`) and browse access (`files.metadata.read`, via a
   root folder listing) before marking the connection **Ready**. A grant that can
   read the account but not browse is flagged **Needs reauthorization** with a
   clear message instead of appearing usable and failing later.

## Using the connection

- **Virtual share:** In **Shares → Virtual Shares**, create a share from the
  Dropbox connection and select the remote folder to expose. Browsing, download,
  and (for writable shares) upload are provider-neutral.
- **Sync:** In **External Storage → Syncs**, create a sync from the Dropbox
  connection, then pick the local share, local path, remote path, and mode
  (push, pull, or two-way). The generic sync engine handles Dropbox through the
  same contract as every other provider.

Reauthorization uses the same connection ID and preserves every referencing sync
and virtual share.

## Troubleshooting

**Connecting fails, or browsing fails with `provider code 'missing_scope'`.** The
granted token is missing a scope the operation needs — typically
`files.metadata.read` for browsing. Connecting can succeed while browsing cannot,
because connecting only needs `account_info.read`. A token is granted only the
scopes the Dropbox app actually has enabled, so:

1. In the [Dropbox App Console](https://www.dropbox.com/developers/apps), open the
   app for the key in use and enable all four scopes on its **Permissions** tab
   (`account_info.read`, `files.metadata.read`, `files.content.read`,
   `files.content.write`), then **Submit**.
2. **Re-authorize** the Kaimo connection. Existing tokens keep the scopes they were
   granted with; only a fresh authorization picks up newly enabled scopes.

**Browsing a remote folder fails with `provider code 'http_400'`.** Dropbox
returns HTTP 400 with a plaintext body — rather than a structured JSON error —
for transport/route-level failures. The most common cause is an empty
`Authorization: Bearer` value, which Dropbox reports as
`Invalid authorization value in HTTP header/URL parameter`. Kaimo now guards the
access-token exchange so a blank token fails with a clear
"Dropbox did not return an access token" message instead of reaching Dropbox,
and it appends Dropbox's redacted framing message to the sanitized error so the
real cause is visible. If the message persists, re-authorize the connection (the
refresh-token exchange may have stopped returning a usable access token) and
confirm the app key in use still belongs to a Dropbox app with the four scopes
listed above.

## Security notes

- The refresh token is stored only in the context-bound credential vault,
  encrypted at rest. It is never written to share JSON, logs, or API responses.
- Authorization uses PKCE with `S256`. The code verifier stays server-side; the
  browser sees only an opaque session id persisted as a SHA-256 hash, and the
  one-time authorization ticket is bound to the initiating user and department.
- Dropbox provider error bodies are reduced to allow-listed, non-sensitive codes
  before they can reach the UI, health state, exceptions, or logs. Structured
  Dropbox errors still expose nothing but their code. A transport/route-level
  failure carries no structured code (its body is a short framing message such as
  `Invalid authorization value in HTTP header/URL parameter`); that message is
  redacted for secrets, length-bounded, and appended to the sanitized error so an
  otherwise opaque `http_400` stays diagnosable.
- Dropbox refresh tokens are long-lived and are not rotated on refresh, so the
  connection has no rotated-credential persistence path. Access tokens are held
  in memory only.

## Implementation summary

| Concern | Location |
| --- | --- |
| File operations (list, download, upload, mkdir, delete) | `DropboxConnection` in [`DropboxConnection.cs`](../../src/Kaimo_File_Server.Infrastructure/Clouds/DropboxConnection.cs) |
| Sync provider registration | `DropboxProvider` (registered as `ICloudProvider`) |
| Browse / virtual-share adapter | `LegacyCloudStorageConnectionProvider("dropbox", …)` in [`Program.cs`](../../src/Kaimo_File_Server.Web/Program.cs) |
| PKCE authorization | `DropboxAuthorizationService` and `CloudAccessDropboxController` |
| App-key resolution | `DropboxIdentityConfiguration` |

Large uploads (over ~140 MB) use a Dropbox upload session; smaller files use a
single request.
