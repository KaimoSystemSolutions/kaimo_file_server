# 🐢 Kaimo File Server — Deployment details

Everything beyond the [quick start](README.md): secrets, data layout, backups,
search, mail, reverse proxy and troubleshooting.

## 📦 Services

| Service | Description | Exposed on host |
| --- | --- | --- |
| `web` | Web UI and REST/sync API | `8443` (HTTPS) |
| `samba` | Native SMB file access | `445` |
| `host` | Background worker; owns the database schema | — |
| `smb-bridge` | Authorization and configuration control plane for Samba | — |
| `db` | PostgreSQL database | internal only |
| `elasticsearch` | Full-text file search | internal only |
| `pki-init` | One-shot generator for the internal mTLS control-plane PKI | — |

Only `web` and `samba` are reachable from the host. Every other service stays
on the internal Compose networks.

### Networks

| Network | Members | Purpose |
| --- | --- | --- |
| `default` | `web`, `host`, `samba` | Published ports and outbound traffic (mail, external storage) |
| `database` (internal) | `db`, `web`, `host` | Database access |
| `bridge-database` (internal) | `db`, `smb-bridge` | Database access for the SMB control plane |
| `smb-control` (internal) | `samba`, `smb-bridge` | mTLS control plane for Samba |
| `search` (internal) | `elasticsearch`, `web`, `host` | Elasticsearch, which runs without authentication |

`internal` networks have no route to the outside. Elasticsearch is reachable
only from `web` and `host` — not from `samba`, the Docker host or the LAN.

**Upgrading from an older `docker-compose.yml`:** take over the new file and run
`docker compose up -d`. Compose creates the `search` network and recreates
`web`, `host` and `elasticsearch`; the search index is kept.

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

### Storing the encryption certificate separately (recommended)

Passwords of external storage connections are encrypted. The certificate that
decrypts them is created on first start inside `data/kaimo-system` — so anyone
with a copy of `data/` (or its backups) can read them. `web` warns about this at
startup. To keep the certificate elsewhere:

1. Start the stack once so the certificate exists.
2. Move it into `secrets/`:
   ```bash
   mkdir -p secrets/dp-certificate
   sudo mv data/kaimo-system/.dp-certificate/key-encryption.pfx secrets/dp-certificate/
   sudo chown 1654:1654 secrets/dp-certificate/key-encryption.pfx
   sudo chmod 0400 secrets/dp-certificate/key-encryption.pfx
   ```
3. In `docker-compose.yml`, uncomment the three commented `DataProtection` /
   `secrets:` blocks.
4. Run `docker compose up -d web`.

Back up `secrets/` separately from `data/` — **without the certificate the
stored passwords are lost**.

## 💾 Data & persistence

All persistent state lives under `./data` by default and survives
`docker compose down`:

| Path | Contents | Redirect via `.env` |
| --- | --- | --- |
| `./data/postgres` | PostgreSQL database | `LOCATION_DB` |
| `./data/elasticsearch` | Search index | `LOCATION_ES` |
| `./data/storage/pool01` | File storage pool | — |
| `./data/kaimo-system` | Application data and snapshot cache | — |
| `./data/logs` | Archived service logs | — |
| `./data/backups` | Database backups | `LOCATION_BACKUP` |
| `./data/smb-control-plane` | Generated mTLS control-plane certificates | `KAIMO_SMB_CONTROL_PKI` |

Paths without a variable are set in the `x-*` anchors at the top of
`docker-compose.yml`; change them there.

### Storage pools

Every bind mount below `/data/storage/` is detected as a storage pool. To add a
second pool, add another anchor (e.g. `./data/storage/pool02:/data/storage/pool02`)
and mount it into `host`, `smb-bridge`, `samba` and `web`, then run
`docker compose up -d`. Apply the same ownership as for `pool01`.

After the first login, **Settings → Storage → User home folders** creates a
private home folder for every user on a pool of your choice (SMB share `users`,
shown as "user" in the web UI). Details:
[`docu/subsystems/user-home-folders.md`](../docu/subsystems/user-home-folders.md).

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

## 🗄️ Database backup & restore

`host` backs up the database daily (and before every schema migration) into
`./data/backups`. Schedule and retention are set under **Settings → Backup**,
where you can also create and download a manual backup.

To restore, put the backup file into the backup folder, set its file name in
`.env` and restart the stack:

```bash
# .env
KAIMO_DB_RESTORE_FROM=kaimo_20260820-030000_manual.dump
```

On the next start `host` **overwrites** the current database with the backup,
applies newer migrations and writes a `<file>.done` marker so the restore runs
only once. Clear the variable again afterwards. Back up `NT_HASH_ENCRYPTION_KEY`
with the backups — see [Secrets](#-secrets). Details:
[`docu/operations/database-backup-and-restore.md`](../docu/operations/database-backup-and-restore.md).

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

## ✉️ Mail notifications

The `web` container sends notification mails through an SMTP server of your
choice (**Settings → Mail server**). It needs outbound access to that server,
usually on port `587` (STARTTLS) or `465` (SSL/TLS). Which events send which
mails to whom is configured under **Notifications**; nothing is sent until a
rule is enabled there. Details: [`docu/subsystems/mail-notifications.md`](../docu/subsystems/mail-notifications.md).

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

- Desktop/sync clients (client API) and WebDAV clients get `426 Upgrade Required`
  although you connect via `https://` (the server sees plain HTTP from the proxy).
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

Each client address additionally has its own budget across all user names
(default 15 failed attempts, then 30 minutes locked), and the client-API
endpoints `/api/v1/auth/*` accept at most 20 requests per minute per address.
Both are keyed by the client address, so **behind an untrusted proxy or the
Docker userland proxy they apply to all clients together**: 15 failed logins from
anyone lock out every login (web, client API, WebDAV) for 30 minutes. Make the
proxy trusted (see above) before exposing the server.

## 🔓 Plain HTTP port 8080

`web` serves HTTPS on `8443` and plain HTTP on container port `8080`. The
example stack publishes **only `8443`**; `8080` is reachable from other
containers in the Compose network, but not from the host or the LAN.

What happens on plain HTTP:

- **Browsers** opening the web UI are redirected to HTTPS
  (`https://<host>:8443`), because the login session cookie is only ever sent
  over HTTPS. If you publish HTTPS under a different port, set
  `HttpsRedirection__HttpsPort` on the `web` service to that port.
- **The client API (`/api/v1`)** always requires HTTPS. A request over plain
  HTTP — including login, token refresh and every bearer-authenticated call —
  is answered with `426 Upgrade Required` and the error code
  `https_required`, before any password or token is checked. There is no
  setting to turn this off. Native clients must use the `https://` address.
- **WebDAV (`/dav`)** keeps its own setting: Basic authentication over plain
  HTTP is refused with `426` while `services.webdav.requireHttps` is on (the
  default).

**Behind a reverse proxy** that terminates TLS and forwards to `8080`, the
proxy must be trusted (see [Behind a reverse proxy](#-behind-a-reverse-proxy))
and send `X-Forwarded-Proto: https`. Otherwise every page request is
redirected again (redirect loop) and every client-API request gets `426`.
Access restriction rules that allow a domain (Settings → Access restrictions)
need the original `Host` header, so let the proxy pass it through (nginx:
`proxy_set_header Host $host;`). If the proxy rewrites the `Host` header, also
list the public URL so the browser connection is accepted:
`Web__AllowedOrigins: https://files.example.com`.

- **Proxy container in the same Compose network:** forward to `web:8080`
  directly; no port needs to be published.
- **Proxy on the Docker host itself:** re-enable the commented mapping in
  `docker-compose.yml`, bound to loopback only, and recreate the service with
  `docker compose up -d web`:

  ```yaml
  services:
    web:
      ports:
        - "127.0.0.1:8081:8080"
        - "8443:8443"
  ```

  Docker forwards such connections through its userland proxy, so `web` sees
  the bridge gateway (e.g. `172.18.0.1`) as the peer, not `127.0.0.1`. List
  that address in `ForwardedHeaders__KnownProxies__0`; give the Compose network
  a fixed subnet so the gateway address does not change.

Never publish `8080` on a public or untrusted interface: browsers would be
redirected, but anything else sent there crosses the network in clear text.

## 🌍 Running on the internet

The example stack publishes `8443` (web) and `445` (SMB) on every host
interface. That is fine inside a trusted LAN, but **not** on a server reachable
from the internet. For internet operation:

| Port | Public? | How |
| --- | --- | --- |
| `443` (and `80`) | Yes | Reverse proxy with a Let's Encrypt certificate; `80` only answers the certificate challenge and redirects to HTTPS. |
| `8443` | No | Remove the mapping; the proxy talks to `web` inside Docker. |
| `8080` | No | Never published; the proxy forwards to `web:8080` over the Docker network. |
| `445` | **Never** | Only through a VPN or a firewall restricted to fixed source addresses (see [SMB](#smb-445)). |

### Reverse proxy with Caddy

Caddy obtains and renews the certificate automatically, passes the `Host` header
through unchanged, supports WebSockets (needed by the web UI) and sets
`X-Forwarded-For` / `X-Forwarded-Proto` itself, ignoring such headers sent by
clients. Put it on its own network together with `web` and give it a fixed
address, so exactly that address can be trusted.

`Caddyfile` next to `docker-compose.yml` (replace the domain; its DNS record must
point to the server):

```text
files.example.com {
	reverse_proxy web:8080
}
```

Changes in `docker-compose.yml`:

```yaml
services:
  caddy:
    image: caddy:2
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - ./data/caddy:/data          # certificates; keep across restarts
    networks:
      proxy:
        ipv4_address: 172.30.0.10

  web:
    # Remove the whole "ports:" block — neither 8443 nor 8080 is published.
    environment:
      <<: *dotnet-environment
      # ... existing web variables ...
      ForwardedHeaders__KnownProxies__0: 172.30.0.10   # Caddy, and only Caddy
    networks:
      - default
      - database
      - search
      - proxy

networks:
  # ... existing networks ...
  proxy:
    ipam:
      config:
        - subnet: 172.30.0.0/24
```

Then run `docker compose up -d`. Pick another subnet if `172.30.0.0/24` is
already used on the host.

**Traefik** works the same way: a router on the HTTPS entrypoint with a Let's
Encrypt certificate resolver, the service port set to `8080`
(`traefik.http.services.kaimo.loadbalancer.server.port=8080`), and Traefik's
fixed address listed in `ForwardedHeaders__KnownProxies__0`. Traefik also passes
the `Host` header through by default. **nginx** additionally needs the WebSocket
upgrade headers and a raised `client_max_body_size` for uploads.

Checklist for every proxy (details in
[Behind a reverse proxy](#-behind-a-reverse-proxy) and
[Plain HTTP port 8080](#-plain-http-port-8080)):

- The proxy's address is listed in `ForwardedHeaders__KnownProxies__N` (or its
  network in `ForwardedHeaders__KnownNetworks__N`). **An untrusted proxy is the
  most common misconfiguration:** every client then appears with the proxy's
  address, the client API answers `426`, and a few failed logins from anyone
  lock out *all* logins for that address — i.e. for everyone.
- The proxy sends `X-Forwarded-Proto: https` and passes the original `Host`
  header. If it rewrites `Host`, set `Web__AllowedOrigins` to the public URL
  (e.g. `https://files.example.com`).
- `8443` is no longer published, so nobody can bypass the proxy.

Afterwards review **Settings → Login protection**: per-user, per-address and
account-wide limits for web, client API and WebDAV logins, share-link passwords
and the SMB account lockout. The **Security** page in the navigation shows
failed logins and locked accounts, including SMB.

### SMB (445)

SMB is not designed for the open internet: it is a frequent target of automated
password guessing and of exploits. **Never publish `445` publicly.** Instead:

- **VPN (recommended):** WireGuard or Tailscale. Bind the port to the VPN
  address only, e.g. `"10.8.0.1:445:445"` in the `samba` service instead of
  `"445:445"`. The address must exist when the container starts, so bring the
  VPN up before Docker.
- **Firewall on fixed source addresses:** only if all clients have static
  public addresses. Note that Docker-published ports **bypass `ufw`/`firewalld`
  rules**; filter in the `DOCKER-USER` iptables chain or bind the port to a
  specific address as above.

In both cases additionally restrict SMB to the expected client networks under
**Settings → Access restrictions → SMB** (Samba `hosts allow`; loopback is
always allowed for the health checks). Samba must see the real client address
for this: with Docker's IPv4 port forwarding it does, behind Docker's userland
proxy (IPv6, hairpin) every client appears as the bridge gateway. If you lock
yourself out, set `KAIMO_ACCESS_RESTRICTIONS_DISABLED=true` on `web` **and**
`smb-bridge` (see
[`OPTIONAL_ENVIRONMENT_VARIABLES.md`](OPTIONAL_ENVIRONMENT_VARIABLES.md)).

The SMB account lockout (**Settings → Login protection**, default on: 10 failed
attempts, then 15 minutes locked) limits password guessing but also lets anyone
who can reach `445` lock out an account on purpose — one more reason to keep the
port off the internet. Administrators can lift a lock early under
**Users → user details → Unlock SMB**.

### Two-factor sign-in

Turn on **Settings → Two-factor sign-in** (TOTP codes from an authenticator app)
and require it at least for everyone with management permissions. It protects
the **web login only**: the client app, WebDAV and SMB keep signing in with the
password alone, so switch those services off for accounts that do not need them
(**Settings → File services**). Before you rely on it:

- The codes depend on the server clock. Keep NTP running on the Docker host.
- The secrets are encrypted with the Data Protection key ring. A database
  restore without that key ring makes every second factor unreadable; the
  affected users then need an administrator reset (**Users → user details →
  Reset two-factor sign-in**).
- If the last administrator is locked out, start `web` once with
  `KAIMO_MFA_DISABLED=true` (see
  [`OPTIONAL_ENVIRONMENT_VARIABLES.md`](OPTIONAL_ENVIRONMENT_VARIABLES.md)) and
  remove it right after the reset.

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
