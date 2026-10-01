# Dropbox Setup

Dropbox connections use the OAuth 2.0 authorization-code flow with PKCE and **no redirect URI**:
Dropbox shows a short code that the administrator pastes back into Kaimo. Only the public app key is
required — no app secret and no inbound callback URL. The provider itself is described in
[Providers](../external-storage/providers.md#dropbox).

## One-time Dropbox app registration

Performed once by the owner of the Dropbox app:

1. In the [Dropbox App Console](https://www.dropbox.com/developers/apps), create an app with
   **Scoped access**.
2. Choose **Full Dropbox** or **App folder**; syncs and virtual shares operate within the granted
   scope.
3. On the **Permissions** tab, enable and submit:
   - `account_info.read`
   - `files.metadata.read`
   - `files.content.read`
   - `files.content.write`
4. Copy the **App key** from the **Settings** tab.

## Configuring the app key

- **Per installation:** `ExternalStorage__Dropbox__AppKey=<app key>`.
- **Shipped default:** `DropboxOAuthDefaults.DefaultAppKey` in
  `src/Kaimo_File_Server.Infrastructure/Clouds/DropboxConnection.cs`.

The environment variable takes precedence. Without either, Dropbox connections cannot be authorized;
other providers are unaffected.

## Authorization

When a connection is authorized, Kaimo opens the Dropbox consent page with a PKCE challenge and
`token_access_type=offline`, the administrator copies the displayed code back, and the server
exchanges it with the server-side verifier for a refresh token. The connection becomes `Ready` only
after both an account lookup and a root folder listing succeed. Reauthorization keeps the connection
ID, so referencing syncs and virtual shares stay attached.

Runtime requirement: outbound HTTPS from the Web container to the Dropbox API.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Browsing fails with provider code `missing_scope` | The token lacks a scope, typically `files.metadata.read`. Connecting needs only `account_info.read` | Enable all four scopes in the App Console, submit, then reauthorize the connection (existing tokens keep their original scopes) |
| Browsing fails with provider code `http_400` | Dropbox returned a plaintext transport error, most often for an unusable access token; its redacted message is appended to the error | Reauthorize the connection and confirm that the app key belongs to an app with the four scopes |
