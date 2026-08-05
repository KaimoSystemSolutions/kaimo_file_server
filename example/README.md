# Quick deployment

This Compose file pulls the published Kaimo images and does not require the
source repository or a local image build.

1. Copy `.env.example` to `.env`.
2. Set random values for `POSTGRES_PASSWORD`, `JWT_SECRET`, and
   `NT_HASH_ENCRYPTION_KEY`. Use at least 32 random characters for both keys.
3. Start the stack with `docker compose up -d`.

## Secrets

- `JWT_SECRET` signs login tokens. It must be at least 32 characters long.
  Changing it invalidates all existing login sessions.
- `NT_HASH_ENCRYPTION_KEY` encrypts the SMB password hashes stored in the
  database. Keep it permanently: changing or losing it makes existing hashes
  unreadable, so the affected SMB passwords must be set again.

Do not enter ordinary passwords or example values. Generate a separate random
value for each key, for example by running `openssl rand -hex 32` twice, and
store the values only in `.env` (never commit that file).

The web UI is available on `http://localhost:8081` and
`https://localhost:8443`; SMB listens on port `445`. Persistent data is stored
below `./data`. All database settings and values shared by multiple services,
such as the time zone and image tag, are configured in `.env`. One-off settings
such as published ports are kept directly in `docker-compose.yml`.

Additional service-specific overrides are documented in
[`OPTIONAL_ENVIRONMENT_VARIABLES.md`](OPTIONAL_ENVIRONMENT_VARIABLES.md).
