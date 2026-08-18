# Shared Authorization and Credential Runtime

This note documents the runtime infrastructure introduced by external-storage work package 2. It supplements
the target architecture and is intentionally limited to behavior that exists in the repository.

## Persistence model

Migration `SharedExternalStorageRuntime` adds the following additive tables:

| Table | Purpose | Sensitive-value handling |
| --- | --- | --- |
| `storage_authorization_transactions` | Short-lived, single-use hand-off between an authorized UI action and an OAuth endpoint | The 256-bit browser token is stored only as a SHA-256 hash. |
| `storage_device_authorization_sessions` | Microsoft device code, local authorization context, expiry, poll interval, and poll ownership | Device code and hand-off context are protected with ASP.NET Core Data Protection; the browser session ID is stored only as a hash. |
| `storage_connection_credential_leases` | Exclusive refresh or rewrap ownership for one storage connection | Contains identifiers and expiry only; it never contains credentials. |

Authorization transactions remain usable after a Web instance restart and can be consumed by another instance.
Consumption uses a conditional database update, so exactly one callback can win. Expired rows are removed
opportunistically when new transactions or device sessions are created. Current OAuth endpoints also resolve
the signed-in actor again and require the stored initiating user and department to match the live resource.

## Credential refresh contract

Every OneDrive client opened from a `StorageConnection` uses the following sequence when an access token is
required:

1. acquire the renewable database lease for the connection;
2. reload the latest encrypted grant from the connection repository;
3. decrypt it with its connection- and provider-bound credential context;
4. exchange the latest refresh token for an access token;
5. persist a rotated refresh token before acknowledging the rotation;
6. release the lease only after the persistence operation completes.

An abandoned lease expires after two minutes and can then be reclaimed. Active owners renew every 30 seconds.
Normal callers wait for up to 30 seconds for a concurrent refresh to finish before failing the operation.

## Provider errors and diagnostic redaction

Raw provider response bodies do not cross the provider boundary. `ProviderErrorSanitizer` extracts a restricted
provider error code, assigns a stable category, and discards the body. `ProviderRequestException` contains only
the provider ID, sanitized code, category, and HTTP status.

The shared redactor recognizes JSON and form/query representations of access tokens, refresh tokens, client
secrets, device codes, authorization codes, assertions, passwords, and private keys. It also removes Bearer
tokens and JWT-shaped values. Callers must still avoid logging request or response objects directly.

## Credential rewrap

The Web process runs one bounded rewrap pass after database migration and before accepting traffic. A pass
examines at most 100 legacy envelopes. For each candidate it acquires the connection lease, reloads the current
record, re-protects the plaintext with the current context-bound purpose, and writes only when the optimistic
concurrency version is unchanged.

A busy connection, a concurrent refresh, or a failed decrypt leaves the original ciphertext untouched. The
startup log reports counts only and never connection names, remote paths, or credential material.

## Backup and restore

The database and Data Protection key material are one recovery unit. Restoring only the database does not make
credentials decryptable. Operational backups must capture the database, Data Protection key ring, and its
key-encryption certificate from the same recovery point. Repository tests verify that ciphertext remains
decryptable after the persisted key ring is copied to a restored location.

Before rollout, operators should:

1. back up the database and application-data key material together;
2. apply `SharedExternalStorageRuntime` through the normal startup migration path;
3. start one Web instance and confirm the bounded rewrap log entry when legacy records exist;
4. start additional Web instances and verify that they share the same database and Data Protection key ring;
5. retain the pre-rollout backup until authorization and normal connection use have been verified.
