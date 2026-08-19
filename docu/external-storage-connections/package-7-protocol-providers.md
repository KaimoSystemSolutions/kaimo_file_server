# Package 7: Protocol Storage Providers

Package 7 provides capability-driven SMB, rsync-over-SSH, and SFTP connections.
NFS is intentionally not registered or exposed in the External Storage UI while
its security and deployment model is evaluated separately. The unencrypted rsync
daemon transport is intentionally not offered: rsync is exposed only over
pinned SSH.

SSH connectivity is offered through two complementary providers that share the
same pinned-SSH trust model (client key + SHA-256 host-key pinning + guided
setup): **rsync / SSH** is a sync-only bulk transport, and **SFTP** is a
browsable, read-write file store that can back a virtual share.

## Capability and execution model

`IStorageConnectionProvider` declares each provider's authorization modes and
capabilities. Browse-capable providers expose `IRemoteFileStore`; native rsync
providers expose `IOptimizedStorageSync`. The common execution service invokes
the optimized contract for manual and scheduled syncs and persists the same
runtime completion or failure state used by other providers.

Rsync jobs must select `Pull` or `Push`. Two-way mode is rejected because rsync
does not provide conflict detection or two-way conflict resolution. Native
rsync does not expose browsing or virtual shares.

Advanced file-size, extension-exclusion, and direction-specific bandwidth
limits are translated to rsync arguments. Destructive `--delete` behavior is
not enabled by the current administration model.

## Rsync over pinned SSH

Select **rsync / SSH** to use public-key authentication. The default UI is a
guided two-step setup:

1. upload an unencrypted OpenSSH private key or generate a new Ed25519 key;
2. copy the displayed public key to the remote account, retrieve the server's
   public host keys, independently verify the selected SHA-256 fingerprint,
   and explicitly confirm it.

Uploaded and generated private keys are protected in the connection's
context-bound Data Protection credential payload. Plaintext key bytes are kept
only while the setup request is active. Each SSH operation materializes a
random mode-0600 temporary key file and deletes it when the provider session is
disposed. Passphrase-protected keys are rejected because background jobs must
remain non-interactive.

`ssh-keyscan` is used only for discovery. Its result is never trusted
automatically: the administrator must compare the fingerprint using an
independent trusted channel before the connection can be created. The selected
public known-host entry is stored in the managed application-data directory;
it is removed when an unused connection is deleted.

The **external secret files** option preserves the operator-managed setup.
Both secret references must be absolute paths. On Unix, private keys must be
inaccessible to group and other users, and the known-hosts file must not be
group- or world-writable.

```json
{
  "Host": "backup.example.test",
  "Port": 22,
  "Username": "kaimo_backup",
  "RemoteRoot": "/srv/archive",
  "ExpectedHostKeySha256": "SHA256:BASE64_FINGERPRINT",
  "PrivateKeySecretReference": "/run/secrets/kaimo_rsync_key",
  "KnownHostsSecretReference": "/run/secrets/kaimo_known_hosts"
}
```

The configured fingerprint must match the key for the exact host and port in
the dedicated known-hosts file. Hashed host entries are intentionally rejected
because the application must independently bind the selected entry to the
configured endpoint. SSH runs with batch mode, strict host-key checking,
password authentication disabled, and keyboard-interactive authentication
disabled. A missing or mismatched host key is an identity failure, not a
transient connection error. Transfers also run with an rsync `--timeout` so a
stalled remote cannot pin an operation open indefinitely.

Use a dedicated unprivileged remote account. Restrict the authorized key and
remote filesystem permissions to the required backup root. Rotate the
known-hosts secret and configured fingerprint together only after verifying a
host-key change through an independent channel.

## SFTP virtual shares

Select **SFTP** to expose a remote directory as a browsable, read-write virtual
share. Unlike rsync — which is a sync-only bulk transport — SFTP implements the
full item-level `IRemoteFileStore` contract (list, read, write, create
directory, delete, rename/move), so a virtual share, the file browser, and
cross-share transfers work exactly as they do for SMB.

SFTP reuses the rsync-over-SSH setup verbatim: the identical settings shape, the
guided key/host-key wizard, and the vault-protected private key. It is
implemented with the managed **SSH.NET** client rather than an external binary,
so the private key is loaded straight from the credential vault in memory and
never written to a temporary file. Host identity is pinned **in process**: the
server key is trusted only when its SHA-256 fingerprint matches the value the
administrator confirmed during setup, and a mismatch is reported as an identity
failure rather than a transient outage. Because the fingerprint is pinned
directly, SFTP does not require an OpenSSH known-hosts file; the guided wizard
still records one for parity with rsync, but the advanced (external secret file)
mode may omit it.

Incoming paths are normalized and rejected on traversal before every request,
resolved beneath the configured remote root, and symbolic links are never
traversed. Settings use the same shape as rsync over SSH (`Host`, `Port`,
`Username`, `RemoteRoot`, `ExpectedHostKeySha256`, and the optional secret-file
references).

## Runtime requirements and rollout

The Web image installs `rsync` and `openssh-client` for the rsync provider. A
custom deployment must make both executables available on `PATH`, and the remote
SSH host also needs a compatible rsync executable. The SFTP provider has no such
dependency: it uses the managed SSH.NET client and needs only network access and
a running SFTP subsystem on the remote host.

Connection health checks return only sanitized codes. Helper stdout and stderr
are never returned to the UI or attached to exceptions because remote output
can contain sensitive paths or server-controlled text. Local paths are resolved
under the configured share root, and symbolic/reparse-point roots are rejected
before starting a helper process.

No database migration is required. Non-secret protocol configuration remains
in `StorageConnection.SettingsJson`; UI-managed SSH private keys remain in the
protected credential payload. Advanced deployments may continue to keep SSH key
material in operator-managed secret files.
