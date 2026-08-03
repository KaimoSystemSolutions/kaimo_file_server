# Quick deployment

This Compose file pulls the published Kaimo images and does not require the
source repository or a local image build.

1. Copy `.env.example` to `.env`.
2. Set random values for `POSTGRES_PASSWORD`, `JWT_SECRET`, and
   `NT_HASH_ENCRYPTION_KEY`. Use at least 32 random characters for both keys.
3. Start the stack with `docker compose up -d`.

The web UI is available on `http://localhost:8081` and
`https://localhost:8443`; SMB listens on port `445`. Persistent data is stored
below `./data` by default. All ports, paths, the image tag, and database names
can be changed in `.env`.
