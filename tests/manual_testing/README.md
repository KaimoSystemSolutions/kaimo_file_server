# Manual API testing

A small, dependency-free **tkinter** prototype for exercising the client
API (`/api/v1`) by hand while developing the server. It is a developer tool, not
an automated test — use it to poke the running server and watch what happens.

See the API reference in [`docu/client-sync-api/README.md`](../../docu/client-sync-api/README.md).

## Prerequisites

- **Python 3.9+** with **tkinter** (bundled with the standard CPython installer on
  Windows and macOS; on Debian/Ubuntu install `python3-tk`). No `pip install` needed —
  the client uses only the standard library.
- A **running server**. The app targets `https://localhost:8443` by default. Start
  the stack with docker-compose from the repo root:
  ```bash
  docker compose up
  ```
- A user to sign in as. The bootstrap admin is seeded on first run (username
  `admin`; password from `Seed:AdminPassword`, or an auto-generated one printed in
  the Host logs).

## Run

```bash
python api_client.py
```

## What it does

- **Connection** — set the base URL and toggle TLS verification. The server ships a
  self-signed certificate, so "Verify TLS" is **off** by default. The device id is
  filled in automatically after the first login and reused on later logins/refreshes.
- **Authentication** — `login` (registers or reuses a device), `refresh` (shows
  token rotation), `logout`. The current access token, its lifetime, and the device
  id are shown.
- **Browse** — load the shares you can access, navigate a share (double-click a
  folder), download a file, upload a file into the current folder, create a folder,
  delete an item.
- **Sync** — this client *is* the device (registered at login), so the Sync tab
  works on **this** device: "Load connections" lists its sync connections. **Create**
  a connection the way a real client does: pick a **remote** endpoint (a share
  folder — use "Pick from Browse tab" to reuse whatever you have open there) and a
  **local** endpoint (a folder on this machine, via "Browse…"), then a direction
  (TwoWay / Pull = download-only / Push = upload-only). You can also run a **delta**
  enumeration and try the **long-poll** change wait (it blocks up to ~30 s and
  returns as soon as the watched subtree changes — try it, then upload a file from
  another window or the web UI and watch it return).

  Note: creating sync connections is a **client-only** capability by design; the web
  UI only displays connected devices. This tool acts as such a client.

## Debug log

Every HTTP call is echoed at the bottom: method, URL, status, elapsed time, and a
truncated request/response body. `Authorization` headers and binary bodies are shown
redacted/summarized. This is the quickest way to see the exact request the server
received and the error envelope (`{ "code": ..., "message": ... }`) it returned.

## Notes

- JSON is camelCase and enums are names (`"TwoWay"`), matching the server.
- The tool trusts the server you point it at and skips certificate checks by
  default — only use it against your own development server.
