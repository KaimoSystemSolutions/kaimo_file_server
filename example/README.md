# 🐢 Kaimo File Server — Deployment

Run a complete Kaimo File Server stack from the published container images. This
Compose file pulls the released images from the registry, so it needs **neither
the source repository nor a local image build**.

## 📦 What you get

| Service | Description | Exposed on host |
| --- | --- | --- |
| `web` | Web UI and REST/sync API | `8081` (HTTP), `8443` (HTTPS) |
| `samba` | Native SMB file access | `445` |
| `host` | Background worker; owns the database schema | — |
| `smb-bridge` | Authorization and configuration control plane for Samba | — |
| `db` | PostgreSQL database | internal only |
| `elasticsearch` | Full-text file search | internal only |
| `pki-init` | One-shot generator for the internal mTLS control-plane PKI | — |

Only `web` and `samba` are reachable from the host. The database and
Elasticsearch stay on the internal Compose network.

## 🚀 Quick start

1. Copy the example environment file:
   ```bash
   cp .env.example .env
   ```
2. Generate a separate random value for each secret and put them in `.env`
   (`POSTGRES_PASSWORD`, `JWT_SECRET`, `NT_HASH_ENCRYPTION_KEY`,
   `SEED_ADMIN_PASSWORD`). Use at least 32 random characters for the two keys:
   ```bash
   openssl rand -hex 32
   ```
3. Prepare the data directories (see [Data directory permissions](#-data-directory-permissions)):
   ```bash
   mkdir -p data/storage/pool01 data/kaimo-system data/logs data/backups data/elasticsearch
   sudo chown -R 1654:1654 data/storage data/kaimo-system data/logs data/backups
   sudo chown -R 1000:1000 data/elasticsearch
   ```
4. Start the stack:
   ```bash
   docker compose up -d
   ```

The web UI is then available at `https://localhost:8443` and — unencrypted, see
[Plain HTTP port 8081](#-plain-http-port-8081) — at `http://localhost:8081`; SMB
listens on port `445`. Sign in with user
`admin` and the `SEED_ADMIN_PASSWORD` you set; you are then asked to choose a new
password (at least 12 characters by default), because the initial one is stored
in `.env`.

## 🔐 Secrets

Generate a separate random value for each of these and store them **only** in
`.env` — never commit that file, and do not use ordinary passwords or the
example placeholders.

| Secret | Purpose | If changed / lost |
| --- | --- | --- |
| `JWT_SECRET` | Signs login tokens (min. 32 characters). | All existing login sessions are invalidated. |
| `NT_HASH_ENCRYPTION_KEY` | Encrypts the SMB password hashes stored in the database. | `host`, `web` and `smb-bridge` refuse to start (see [NT hash key mismatch](#nt-hash-key-mismatch)). **Keep this key permanently and back it up together with the database.** |
| `POSTGRES_PASSWORD` | Database password. | The database can no longer be opened with the old value. |
| `SEED_ADMIN_PASSWORD` | Password for the first administrator, created only in an empty database. | No effect after the admin exists — change it in the web UI instead. |

### Protecting stored credentials

Passwords and tokens of external storage connections are encrypted with ASP.NET
Data Protection. By default both the key ring (`.dp-keys`) **and** the
certificate that encrypts it (`.dp-certificate/key-encryption.pfx`) live in
`./data/kaimo-system`, so anyone with a copy of that directory or of its backups
can decrypt them (`web` logs a warning at startup). Keep the certificate
separately:

```bash
mkdir -p secrets/dp-certificate
sudo mv data/kaimo-system/.dp-certificate/key-encryption.pfx secrets/dp-certificate/
sudo chown 1654:1654 secrets/dp-certificate/key-encryption.pfx
sudo chmod 0400 secrets/dp-certificate/key-encryption.pfx
```

Then uncomment `DataProtection__CertificatePath` and the two `secrets:` blocks in
`docker-compose.yml` and run `docker compose up -d web`. On a fresh installation,
start once without the setting so the certificate is generated, then move it.
Back up `secrets/` separately from `data/` — **without the certificate the stored
credentials cannot be recovered**. `web` refuses to start if the configured file
is missing.

## 💾 Data & persistence

All persistent state lives under `./data` by default and survives
`docker compose down`:

| Path | Contents |
| --- | --- |
| `./data/postgres` | PostgreSQL database |
| `./data/elasticsearch` | Search index |
| `./data/storage/pool01` | File storage pool |
| `./data/kaimo-system` | Application data and snapshot cache |
| `./data/logs` | Archived service logs |
| `./data/backups` | Database backups |
| `./data/smb-control-plane` | Generated mTLS control-plane certificates |

Each location can be redirected with a `LOCATION_*` variable in `.env`.

### 🔑 Data directory permissions

Docker creates missing bind-mount directories as `root:root`, but the services
run as **non-root** users and cannot write to root-owned directories. Create and
own the directories on the host **before the first `docker compose up`**:

```bash
mkdir -p data/storage/pool01 data/kaimo-system data/logs data/backups data/elasticsearch
# App (host/web/smb-bridge) and Samba share UID/GID 1654 (the ".NET app" user
# and the shared "kaimo" storage group).
sudo chown -R 1654:1654 data/storage data/kaimo-system data/logs data/backups
# Elasticsearch runs as UID 1000 and does not fix ownership itself.
sudo chown -R 1000:1000 data/elasticsearch
```

`data/postgres` and `data/smb-control-plane` need no manual `chown`: PostgreSQL
adjusts its own data directory on startup, and `pki-init` runs as `root`.

If you redirect a location with a `LOCATION_*` variable, apply the matching
ownership to that path instead. On Windows/WSL bind mounts (drvfs/9p) POSIX
ownership is not enforced — the `chown` is a no-op there and can be skipped.

## 🔍 Search (Elasticsearch)

File search is served by Elasticsearch. The stack starts without it —
if Elasticsearch is unavailable the server automatically falls back to
filename-only search — so startup is never blocked on it.

On most Linux hosts Elasticsearch needs a raised virtual-memory map limit.
If the container keeps restarting, set it on the host:

```bash
sudo sysctl -w vm.max_map_count=262144
echo "vm.max_map_count=262144" | sudo tee /etc/sysctl.d/99-kaimo.conf
```

Heap size is capped at 512 MB via `ELASTIC_JAVA_OPTS`; raise it in `.env` for
larger deployments.

## ⚙️ Configuration

Values shared by multiple services (database settings, time zone, image tag,
data locations) are set in `.env`. One-off settings such as published ports are
kept directly in `docker-compose.yml`. A value placed only in `.env` is passed
to a container only when `docker-compose.yml` references it.

Additional service-specific overrides are documented in
[`OPTIONAL_ENVIRONMENT_VARIABLES.md`](OPTIONAL_ENVIRONMENT_VARIABLES.md).

## 🌐 Behind a reverse proxy

If a reverse proxy (nginx, Traefik, Caddy, …) terminates TLS in front of `web`,
the server takes the client address and scheme from the `X-Forwarded-For` and
`X-Forwarded-Proto` headers — but **only from proxies you list explicitly**.
Headers from any other peer are ignored.

**Trusted without any setting:** loopback only (`127.0.0.0/8`, `::1`). Private
networks are deliberately **not** trusted by default: every machine in your LAN
has such an address, so trusting them would let any LAN client choose its own
"client address" per request and sidestep the login lockout.

**Any other proxy** — in the same Docker network, on the LAN, or with a public
address — must be listed on the `web` service:

```yaml
services:
  web:
    environment:
      <<: *dotnet-environment
      # ... existing web variables ...
      ForwardedHeaders__KnownProxies__0: 172.18.0.10       # single proxy address
      # ForwardedHeaders__KnownProxies__1: 203.0.113.11    # more proxies: __1, __2, …
      # ForwardedHeaders__KnownNetworks__0: 172.18.0.0/16  # or a whole range (CIDR)
```

For a proxy container in the same Compose project, give the Docker network a
fixed subnet (or the proxy a fixed address) and list that. Only list networks
in which **every** host is a trusted proxy. Recreate the service afterwards:
`docker compose up -d web`.

Signs that your proxy is not trusted:

- WebDAV clients get `426 Upgrade Required` although you connect via `https://`
  (the server sees plain HTTP from the proxy).
- A few failed logins lock out *every* user of an account at once, because all
  requests appear to come from the proxy's address. Login lockout is tracked per
  user **and** client address, so with a trusted proxy a stranger guessing a
  password only locks out their own address.

The same applies without a proxy when Docker forwards published ports through its
userland proxy (e.g. IPv6 or hairpin connections): all clients then appear with
the bridge gateway address. The lockout still works, just per user instead of per
client address.

Independently of client addresses, each account also has an account-wide ceiling
of ten times the per-address attempt limit. Guessing spread over many (real or
forged) addresses therefore locks the account temporarily instead of going on
indefinitely.

## 🔓 Plain HTTP port 8081

Port `8081` serves the web UI, API and WebDAV **without encryption**: passwords
and login tokens cross the network in clear text. Use it only behind a reverse
proxy that terminates TLS, or in a network you fully trust. For direct access
use `https://<host>:8443`, or remove the `8081` mapping from
`docker-compose.yml` if you do not need it.

## 🩺 Troubleshooting

### NT hash key mismatch

`host`, `web` and `smb-bridge` check at startup that `NT_HASH_ENCRYPTION_KEY`
matches the key the stored SMB password hashes were encrypted with. On a
mismatch the service stops (and Docker keeps restarting it) with:

```text
NtHash:EncryptionKey does not match the key the stored SMB NT hashes were encrypted with. ...
```

Check with `docker compose logs host web smb-bridge`. Common causes:

- **`.env` was changed or recreated** with a new `NT_HASH_ENCRYPTION_KEY` →
  put the original value back and run `docker compose up -d`.
- **Database restored on a new machine** without the original `.env` → copy the
  original `NT_HASH_ENCRYPTION_KEY` from the old installation.
- **Services started with different values** (e.g. an override for only one
  service) → all three services must receive the identical key; the example
  `docker-compose.yml` already passes the same `.env` value to each of them.

**Only if the original key is irrecoverably lost:** reset all stored SMB
password hashes. Web logins are not affected, but **every user must set their
password again** (profile or user management in the web UI) before SMB access
works. Stop the application services, clear the hashes and the key check, then
start again with the new key:

```bash
docker compose stop host web smb-bridge samba
docker compose exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"' <<'SQL'
UPDATE users SET "NtHash" = '';
DELETE FROM config_settings WHERE "Key" = 'security.ntHash.keyCanary';
SQL
docker compose up -d
```

Take a database backup first. The next `host` start records the new key.

## 🔧 Operating the stack

```bash
docker compose pull        # fetch the latest images
docker compose up -d       # start / update the stack
docker compose ps          # show service status
docker compose logs -f web # follow a service's logs
docker compose down        # stop the stack (data is kept)
```

To move to a newer release, set `KAIMO_IMAGE_TAG` in `.env`, then run
`docker compose pull && docker compose up -d`.
