# 🐢 Kaimo File Server — Deployment

Quick start for running the Kaimo File Server with Docker Compose. Everything
else like secrets, data layout, backups, reverse proxy, troubleshooting you find here  
[`DETAILS.md`](DETAILS.md).

## 📦 Services

| Service | Description | Exposed on host |
| --- | --- | --- |
| `web` | Web UI and REST/sync API | `8081` (HTTP), `8443` (HTTPS) |
| `samba` | Native SMB file access | `445` |
| `host` | Background worker; owns the database schema | — |
| `smb-bridge` | Authorization and configuration control plane for Samba | — |
| `db` | PostgreSQL database | internal only |
| `elasticsearch` | Full-text file search | internal only |
| `pki-init` | One-shot generator for the internal mTLS control-plane PKI | — |

Only `web` and `samba` are reachable from the host. Every other service stays
on the internal Compose network.

## 🚀 Quick start

1. Copy the example environment file:
   ```bash
   cp .env.example .env
   ```
2. Fill in the empty secrets in `.env` (`POSTGRES_PASSWORD`, `JWT_SECRET`,
   `NT_HASH_ENCRYPTION_KEY`, `SEED_ADMIN_PASSWORD`) — a separate random value
   for each:
   ```bash
   openssl rand -hex 32
   ```
   Keep `NT_HASH_ENCRYPTION_KEY` safe and back it up with the database; see
   [Secrets](DETAILS.md#-secrets).
3. Create the data directories with the right owners:
   ```bash
   mkdir -p data/storage/pool01 data/kaimo-system data/logs data/backups data/elasticsearch
   sudo chown -R 1654:1654 data/storage data/kaimo-system data/logs data/backups
   sudo chown -R 1000:1000 data/elasticsearch
   ```
4. Start the stack:
   ```bash
   docker compose up -d
   ```
   If `elasticsearch` keeps restarting (`docker compose ps`), raise
   `vm.max_map_count` on the host; see
   [Search (Elasticsearch)](DETAILS.md#-search-elasticsearch).

Open `https://localhost:8443` and sign in as `admin` with your
`SEED_ADMIN_PASSWORD`; you are then asked to choose a new password. SMB listens
on port `445`.

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

## 📚 Next steps

See [`DETAILS.md`](DETAILS.md) for:

- [Storing the encryption certificate separately](DETAILS.md#storing-the-encryption-certificate-separately-recommended) (recommended)
- [Data & persistence](DETAILS.md#-data--persistence) and [storage pools](DETAILS.md#storage-pools)
- [Database backup & restore](DETAILS.md#️-database-backup--restore)
- [Search](DETAILS.md#-search-elasticsearch) and [mail notifications](DETAILS.md#️-mail-notifications)
- [Reverse proxy](DETAILS.md#-behind-a-reverse-proxy) and [plain HTTP port 8081](DETAILS.md#-plain-http-port-8081)
- [Troubleshooting](DETAILS.md#-troubleshooting)
