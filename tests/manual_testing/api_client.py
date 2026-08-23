#!/usr/bin/env python3
"""
Kaimo File Server — client API test prototype.

A tiny tkinter GUI for exercising the /api/v1 client API by hand while developing
the server. It is intentionally dependency-free (Python standard library only) so
it runs anywhere with a normal CPython install.

What it covers
--------------
  * Auth:   login (registers/reuses a device), refresh (token rotation), logout.
  * Browse: list shares, navigate a share, download a file, upload a file.
  * Sync:   list devices, list/create/delete per-device sync profiles,
            delta enumeration, the long-poll change-wait, and — on top of those —
            an actual file synchronizer that reconciles a share subtree with a
            local folder (one-shot "Sync now" plus a long-poll auto-sync loop).

Every HTTP call is echoed to the Debug log at the bottom (method, URL, status,
elapsed time, and a truncated body) so you can see exactly what the server does.

Run
---
    python api_client.py

TLS: the server ships a self-signed certificate, so "Verify TLS" is OFF by
default. Turn it on once you put a trusted certificate in front of the server.

This is a developer tool for a server you control — not a hardened client.
"""

from __future__ import annotations

import datetime
import json
import os
import ssl
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from typing import Any, Callable, Optional

import tkinter as tk
from tkinter import filedialog, messagebox, ttk

DEFAULT_BASE_URL = "https://localhost:8443"
BODY_LOG_LIMIT = 2000  # characters of a response body echoed to the debug log


# ────────────────────────────── HTTP layer ──────────────────────────────


@dataclass
class ApiResult:
    """Outcome of one HTTP call."""

    ok: bool
    status: int
    elapsed_ms: float
    headers: dict[str, str] = field(default_factory=dict)
    body_bytes: bytes = b""
    json: Any = None
    error: Optional[str] = None

    @property
    def code(self) -> Optional[str]:
        """Machine error code from the API error envelope, if present."""
        if isinstance(self.json, dict):
            return self.json.get("code")
        return None


class ApiClient:
    """Thin wrapper over urllib for the Kaimo client API."""

    def __init__(self, base_url: str, verify_tls: bool, log: Callable[[str], None]):
        self.base_url = base_url.rstrip("/")
        self.verify_tls = verify_tls
        self.log = log
        self.access_token: Optional[str] = None

    def _context(self) -> Optional[ssl.SSLContext]:
        if self.base_url.lower().startswith("http://"):
            return None
        if self.verify_tls:
            return ssl.create_default_context()
        return ssl._create_unverified_context()  # self-signed dev certificate

    def request(
        self,
        method: str,
        path: str,
        *,
        query: Optional[dict[str, Any]] = None,
        json_body: Any = None,
        raw_body: Optional[bytes] = None,
        auth: bool = True,
        extra_headers: Optional[dict[str, str]] = None,
        timeout: float = 60.0,
    ) -> ApiResult:
        url = self.base_url + path
        if query:
            # Drop None values so optional params can be omitted cleanly.
            clean = {k: v for k, v in query.items() if v is not None}
            if clean:
                url += "?" + urllib.parse.urlencode(clean)

        headers: dict[str, str] = {"Accept": "application/json"}
        data: Optional[bytes] = None
        if json_body is not None:
            data = json.dumps(json_body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        elif raw_body is not None:
            data = raw_body
            headers["Content-Type"] = "application/octet-stream"
        if auth and self.access_token:
            headers["Authorization"] = "Bearer " + self.access_token
        if extra_headers:
            headers.update(extra_headers)

        self._log_request(method, url, headers, data)

        req = urllib.request.Request(url, data=data, method=method, headers=headers)
        started = time.monotonic()
        try:
            with urllib.request.urlopen(req, timeout=timeout, context=self._context()) as resp:
                body = resp.read()
                elapsed = (time.monotonic() - started) * 1000
                result = self._build_result(
                    True, resp.status, elapsed, dict(resp.headers), body)
        except urllib.error.HTTPError as exc:
            body = exc.read()
            elapsed = (time.monotonic() - started) * 1000
            result = self._build_result(
                False, exc.code, elapsed, dict(exc.headers or {}), body)
        except Exception as exc:  # noqa: BLE001 - surface any transport error in the UI
            elapsed = (time.monotonic() - started) * 1000
            result = ApiResult(False, 0, elapsed, error=str(exc))
            self.log(f"  ✗ transport error: {exc}\n")
            return result

        self._log_response(result)
        return result

    # -- logging helpers --

    def _log_request(self, method: str, url: str, headers: dict[str, str], data: Optional[bytes]) -> None:
        self.log(f"→ {method} {url}\n")
        if "Authorization" in headers:
            self.log("    Authorization: Bearer <token>\n")
        if data is not None:
            preview = data[:BODY_LOG_LIMIT].decode("utf-8", "replace")
            if "Content-Type" in headers and "octet-stream" in headers["Content-Type"]:
                self.log(f"    body: <{len(data)} bytes binary>\n")
            else:
                self.log(f"    body: {preview}\n")

    def _log_response(self, result: ApiResult) -> None:
        marker = "✓" if result.ok else "✗"
        self.log(f"  {marker} {result.status} ({result.elapsed_ms:.0f} ms)\n")
        if result.body_bytes:
            text = result.body_bytes[:BODY_LOG_LIMIT].decode("utf-8", "replace")
            self.log(f"    {text}\n")

    @staticmethod
    def _build_result(ok, status, elapsed, headers, body) -> ApiResult:
        parsed = None
        ctype = headers.get("Content-Type", "")
        if body and "application/json" in ctype:
            try:
                parsed = json.loads(body)
            except json.JSONDecodeError:
                parsed = None
        return ApiResult(ok, status, elapsed, headers, body, parsed)


# ────────────────────────────── Sync engine ──────────────────────────────


def _parse_utc(iso: str) -> float:
    """Parse an ISO-8601 UTC timestamp into a POSIX epoch (seconds)."""
    if not iso:
        return 0.0
    text = iso.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    try:
        dt = datetime.datetime.fromisoformat(text)
    except ValueError:
        # Fall back to whole-second precision if the fraction is unusual.
        dt = datetime.datetime.strptime(text[:19], "%Y-%m-%dT%H:%M:%S")
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=datetime.timezone.utc)
    return dt.timestamp()


@dataclass
class SyncStats:
    """Tally of everything one sync run did, for the summary line."""

    downloaded: int = 0
    uploaded: int = 0
    created_local_dirs: int = 0
    created_remote_dirs: int = 0
    deleted_local: int = 0
    deleted_remote: int = 0
    conflicts: int = 0
    skipped: int = 0
    failed: int = 0

    def summary(self) -> str:
        return (
            f"↓{self.downloaded} ↑{self.uploaded}  "
            f"mkdir(local {self.created_local_dirs}, remote {self.created_remote_dirs})  "
            f"del(local {self.deleted_local}, remote {self.deleted_remote})  "
            f"conflict {self.conflicts}  skip {self.skipped}  fail {self.failed}"
        )


def _state_dir() -> str:
    """Directory holding the per-profile sync baselines (created on demand)."""
    path = os.path.join(os.path.expanduser("~"), ".kaimo_sync_client")
    os.makedirs(path, exist_ok=True)
    return path


class SyncState:
    """
    Persistent record of the converged state after the last successful two-way
    sync of one connection, so the next run can tell a *newly created* file apart
    from one *deleted on the other side*.

    Without this memory a two-way sync sees a file present on only one side and
    can only guess "new here" — so it copies it back, silently resurrecting a file
    the user deleted on the other side. The baseline turns that guess into a fact:
    a path in the baseline that is now gone on one side was deleted (propagate the
    deletion); a path not in the baseline is genuinely new (copy it).

    Per file we remember each side's (size, mtime) separately — the "r"emote and
    "l"ocal observations can legitimately differ (e.g. the server stamps its own
    modification time on upload) — so "did this side change since the last sync?"
    is answered against that side's own last-seen value.

    The state lives outside the synced folders (keyed by profile id under
    ~/.kaimo_sync_client) so it is never itself swept up by the sync.
    """

    def __init__(self, profile_id: str):
        self.path = os.path.join(_state_dir(), f"{profile_id}.json")

    def load(self) -> tuple[dict[str, dict], set[str], bool]:
        """Return (files, dirs, existed). `files` maps rel → {"r":[size,mtime], "l":[size,mtime]}."""
        try:
            with open(self.path, encoding="utf-8") as fh:
                data = json.load(fh)
        except (OSError, json.JSONDecodeError):
            return {}, set(), False
        files = data.get("files", {}) if isinstance(data, dict) else {}
        dirs = set(data.get("dirs", [])) if isinstance(data, dict) else set()
        return files, dirs, True

    def save(self, files: dict[str, dict], dirs: set[str]) -> None:
        payload = {"files": files, "dirs": sorted(dirs)}
        tmp = self.path + ".tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as fh:
                json.dump(payload, fh)
            os.replace(tmp, self.path)
        except OSError:
            pass  # a lost baseline only costs one extra reconcile, never data

    def clear(self) -> None:
        try:
            os.remove(self.path)
        except OSError:
            pass


class SyncEngine:
    """
    One-shot, stateless folder synchronizer for the test client.

    It enumerates the remote subtree via the sync delta endpoint, walks the local
    folder, and reconciles the two according to the connection's direction:

      * Pull   — the server is authoritative: copy remote → local.
      * Push   — the device is authoritative: copy local → remote.
      * TwoWay — the newest-modified side wins, in either direction.

    Transfers reuse the same browse endpoints the Browse tab uses (content GET/PUT,
    directory POST, item DELETE), so this is a faithful exercise of the real API.

    Deletions are only performed when "mirror" is enabled. For the one-directional
    modes the authoritative side decides unambiguously (Pull deletes local extras,
    Push deletes remote extras). TwoWay has no authoritative side, so it leans on a
    persistent baseline (see SyncState): a path present on only one side is a
    deletion to propagate when it was in the baseline, or a new file to copy when it
    was not. A path modified on the surviving side after having been deleted on the
    other counts as a conflict and is kept (re-copied), never deleted — data loss is
    always resolved in favour of keeping the file. Without a baseline yet (the first
    TwoWay run) nothing is deleted; the run just unions both sides and records the
    baseline so subsequent runs can propagate deletions.

    Two files count as equal when their sizes match and their modification times
    agree within MTIME_TOLERANCE. On download the local mtime is set to the remote
    one so the two sides converge and the next run skips the file.
    """

    MTIME_TOLERANCE = 2.0  # seconds; absorbs filesystem/rounding differences

    def __init__(self, client: ApiClient, *, share_id: str, remote_root: str,
                 local_root: str, mode: str, mirror: bool, dry_run: bool,
                 log: Callable[[str], None], should_stop: Callable[[], bool],
                 state: Optional["SyncState"] = None):
        self.client = client
        self.share_id = share_id
        self.remote_root = remote_root.strip("/")
        self.local_root = local_root
        self.mode = mode              # "Pull" | "Push" | "TwoWay"
        self.mirror = mirror
        self.dry_run = dry_run
        self.log = log
        self.should_stop = should_stop
        self.state = state
        self.stats = SyncStats()
        # Baseline (loaded in run(), TwoWay only) and post-run existence tracking.
        self._base_files: dict[str, dict] = {}
        self._base_dirs: set[str] = set()
        self._baseline_available = False
        self._twoway_mirror = False   # mirror deletions armed for this run?
        self._remote_after: set[str] = set()
        self._local_after: set[str] = set()
        self._dir_deletes_remote: list[str] = []
        self._dir_deletes_local: list[str] = []

    # -- path mapping between the remote subtree and the local folder --

    def _remote_path(self, rel: str) -> str:
        """Share-relative path of a subtree-relative item."""
        return f"{self.remote_root}/{rel}" if self.remote_root else rel

    def _local_path(self, rel: str) -> str:
        """Absolute local path of a subtree-relative item."""
        return os.path.join(self.local_root, rel.replace("/", os.sep))

    def _rel_from_remote(self, path: str) -> Optional[str]:
        """Strip the subtree-root prefix; returns None for the root itself."""
        if not self.remote_root:
            return path or None
        if path == self.remote_root:
            return None
        prefix = self.remote_root + "/"
        return path[len(prefix):] if path.startswith(prefix) else None

    # -- enumeration of both sides --

    def _enumerate_remote(self) -> tuple[dict[str, None], dict[str, tuple[int, float]], Optional[str]]:
        res = self.client.request(
            "GET", f"/api/v1/sync/{self.share_id}/delta",
            query={"path": self.remote_root})
        if not res.ok or not isinstance(res.json, dict):
            raise RuntimeError(f"delta failed: HTTP {res.status}")
        dirs: dict[str, None] = {}
        files: dict[str, tuple[int, float]] = {}
        for entry in res.json.get("entries", []):
            rel = self._rel_from_remote(entry["path"])
            if rel is None:
                continue
            if entry["isDirectory"]:
                dirs[rel] = None
            else:
                files[rel] = (entry["size"], _parse_utc(entry["modifiedAtUtc"]))
        return dirs, files, res.json.get("token")

    def _scan_local(self) -> tuple[dict[str, None], dict[str, tuple[int, float]]]:
        dirs: dict[str, None] = {}
        files: dict[str, tuple[int, float]] = {}
        if not os.path.isdir(self.local_root):
            return dirs, files
        for base, subdirs, filenames in os.walk(self.local_root):
            rel_base = os.path.relpath(base, self.local_root)
            rel_base = "" if rel_base == "." else rel_base.replace(os.sep, "/")
            for name in subdirs:
                dirs[f"{rel_base}/{name}" if rel_base else name] = None
            for name in filenames:
                rel = f"{rel_base}/{name}" if rel_base else name
                try:
                    st = os.stat(os.path.join(base, name))
                except OSError:
                    continue
                files[rel] = (st.st_size, st.st_mtime)
        return dirs, files

    # -- the reconcile pass --

    def run(self) -> Optional[str]:
        """Reconcile the two sides once; returns the delta token seen at the start."""
        self.log(f"  enumerating remote '{self.remote_root or '/'}' …\n")
        remote_dirs, remote_files, token = self._enumerate_remote()
        local_dirs, local_files = self._scan_local()
        self.log(
            f"  remote: {len(remote_dirs)} dirs / {len(remote_files)} files    "
            f"local: {len(local_dirs)} dirs / {len(local_files)} files\n")

        pull = self.mode in ("Pull", "TwoWay")
        push = self.mode in ("Push", "TwoWay")

        # Load the baseline for two-way runs; arm deletion propagation only once we
        # actually have one (the first run just unions the sides and records it).
        if self.mode == "TwoWay" and self.state is not None:
            self._base_files, self._base_dirs, self._baseline_available = self.state.load()
            self._twoway_mirror = self.mirror and self._baseline_available
            if self.mirror and not self._baseline_available:
                self.log("  (first two-way run — building baseline; deletions apply next time)\n")

        # Track what will exist on each side after this run, for dir-emptiness
        # checks and for refreshing the baseline afterwards.
        self._remote_after = set(remote_files)
        self._local_after = set(local_files)
        self._dir_deletes_remote = []
        self._dir_deletes_local = []

        # 1) Directories first (shallow → deep) so file transfers have a parent.
        if self._twoway_mirror:
            self._reconcile_dirs_twoway(remote_dirs, local_dirs)
        else:
            if pull:
                for rel in sorted(remote_dirs, key=lambda r: r.count("/")):
                    if self.should_stop():
                        return self._stopped(token)
                    if rel not in local_dirs:
                        self._make_local_dir(rel)
            if push:
                for rel in sorted(local_dirs, key=lambda r: r.count("/")):
                    if self.should_stop():
                        return self._stopped(token)
                    if rel not in remote_dirs:
                        self._make_remote_dir(rel)

        # 2) Files.
        for rel in sorted(set(remote_files) | set(local_files)):
            if self.should_stop():
                return self._stopped(token)
            self._reconcile_file(rel, remote_files.get(rel), local_files.get(rel), pull, push)

        # 3) Mirror deletions of extras on the target, deepest path first.
        if self.mirror and self.mode == "Pull":
            self._mirror_delete_local(
                set(local_files) - set(remote_files), set(local_dirs) - set(remote_dirs))
        elif self.mirror and self.mode == "Push":
            self._mirror_delete_remote(
                set(remote_files) - set(local_files), set(remote_dirs) - set(local_dirs))
        elif self._twoway_mirror:
            # Two-way file deletions were propagated inline; now the emptied
            # directories, deepest first, guarding against non-empty ones.
            self._apply_twoway_dir_deletes()

        # 4) Record the converged state so the next two-way run can reason about
        #    deletions. Refresh when something changed, or to seed the first run.
        if self.mode == "TwoWay" and self.state is not None and not self.dry_run \
                and not self.should_stop() and (self._mutated() or not self._baseline_available):
            self._refresh_baseline()

        verb = "would sync" if self.dry_run else "done"
        self.log(f"  {verb}: {self.stats.summary()}\n")
        return token

    def _mutated(self) -> bool:
        """Did this run change either side (as opposed to only skipping/failing)?"""
        s = self.stats
        return bool(s.downloaded or s.uploaded or s.created_local_dirs
                    or s.created_remote_dirs or s.deleted_local or s.deleted_remote)

    def _reconcile_file(self, rel: str, remote: Optional[tuple[int, float]],
                        local: Optional[tuple[int, float]], pull: bool, push: bool) -> None:
        if remote is not None and local is not None:
            if self._same(remote, local):
                self.stats.skipped += 1
            elif self.mode == "Pull":
                self._download(rel, remote)
            elif self.mode == "Push":
                self._upload(rel)
            elif remote[1] >= local[1]:  # TwoWay: newer modification wins
                self._download(rel, remote)
            else:
                self._upload(rel)
        elif remote is not None:                # present only on the remote
            if self._twoway_mirror and self._in_baseline(rel):
                self._resolve_local_gone(rel, remote)
            elif pull:                          # new remote file → bring it local
                self._download(rel, remote)
        elif local is not None:                 # present only on the local side
            if self._twoway_mirror and self._in_baseline(rel):
                self._resolve_remote_gone(rel, local)
            elif push:                          # new local file → send it up
                self._upload(rel)
        # Any remaining "only one side" case (mirror off, or wrong direction) is
        # left untouched.

    def _resolve_local_gone(self, rel: str, remote: tuple[int, float]) -> None:
        """The file is in the baseline but gone locally: a local delete, unless the
        remote copy was edited since — then it is a conflict and we keep it."""
        if self._matches_baseline(rel, "r", remote):
            self.log(f"    − remote {rel} (deleted locally)\n")
            self._delete_remote_file(rel)
        else:
            self.log(f"    ⚠ conflict {rel}: deleted locally but changed on server — keeping\n")
            self.stats.conflicts += 1
            self._download(rel, remote)

    def _resolve_remote_gone(self, rel: str, local: tuple[int, float]) -> None:
        """The file is in the baseline but gone on the server: a remote delete,
        unless the local copy was edited since — then it is a conflict, keep it."""
        if self._matches_baseline(rel, "l", local):
            self.log(f"    − local {rel} (deleted on server)\n")
            self._delete_local_file(rel)
        else:
            self.log(f"    ⚠ conflict {rel}: deleted on server but changed locally — keeping\n")
            self.stats.conflicts += 1
            self._upload(rel)

    # -- baseline lookups --

    def _in_baseline(self, rel: str) -> bool:
        return rel in self._base_files

    def _matches_baseline(self, rel: str, side: str, current: tuple[int, float]) -> bool:
        """True when `current` (an "r" or "l" observation) equals what the baseline
        last recorded for that side — i.e. the surviving side was not edited since."""
        ref = self._base_files.get(rel, {}).get(side)
        if not ref:
            return False
        return ref[0] == current[0] and abs(ref[1] - current[1]) <= self.MTIME_TOLERANCE

    def _same(self, remote: tuple[int, float], local: tuple[int, float]) -> bool:
        return remote[0] == local[0] and abs(remote[1] - local[1]) <= self.MTIME_TOLERANCE

    # -- two-way directory reconciliation (baseline-driven) --

    def _reconcile_dirs_twoway(self, remote_dirs: dict, local_dirs: dict) -> None:
        """Create genuinely new directories on the opposite side; collect the ones
        that vanished from one side (and were known) as deletion candidates for
        after the file pass, when we can tell whether they are really empty."""
        for rel in sorted(remote_dirs, key=lambda r: r.count("/")):
            if self.should_stop():
                return
            if rel in local_dirs:
                continue
            if rel in self._base_dirs:
                self._dir_deletes_remote.append(rel)   # gone locally → maybe delete remote
            else:
                self._make_local_dir(rel)              # new on the server → create local
        for rel in sorted(local_dirs, key=lambda r: r.count("/")):
            if self.should_stop():
                return
            if rel in remote_dirs:
                continue
            if rel in self._base_dirs:
                self._dir_deletes_local.append(rel)    # gone on server → maybe delete local
            else:
                self._make_remote_dir(rel)             # new locally → create remote

    def _apply_twoway_dir_deletes(self) -> None:
        """Remove directories emptied by propagated deletions, deepest first, but
        only when no (possibly newly added) file still lives under them."""
        for rel in sorted(self._dir_deletes_remote, key=lambda r: -r.count("/")):
            if self.should_stop():
                return
            if self._has_children(self._remote_after, rel):
                continue                               # new content arrived → keep it
            self.log(f"    − remote dir {rel} (deleted locally)\n")
            self._delete_remote_path(rel)
            self._remote_after.discard(rel)
        for rel in sorted(self._dir_deletes_local, key=lambda r: -r.count("/")):
            if self.should_stop():
                return
            if self._has_children(self._local_after, rel):
                continue
            self.log(f"    − local dir {rel} (deleted on server)\n")
            self._delete_local_path(self._local_path(rel), is_dir=True)
            self._local_after.discard(rel)

    @staticmethod
    def _has_children(paths: set[str], rel: str) -> bool:
        prefix = rel + "/"
        return any(p.startswith(prefix) for p in paths)

    def _refresh_baseline(self) -> None:
        """Re-observe both sides after reconciling and store the converged state
        (paths present on both sides) as the baseline for the next two-way run."""
        try:
            remote_dirs, remote_files, _ = self._enumerate_remote()
            local_dirs, local_files = self._scan_local()
        except Exception as exc:  # noqa: BLE001 - keep the old baseline on any hiccup
            self.log(f"  ⚠ baseline not updated: {exc}\n")
            return
        files = {
            rel: {"r": list(remote_files[rel]), "l": list(local_files[rel])}
            for rel in set(remote_files) & set(local_files)
        }
        dirs = set(remote_dirs) & set(local_dirs)
        assert self.state is not None
        self.state.save(files, dirs)

    # -- individual operations (each honours dry-run and tallies its outcome) --

    def _download(self, rel: str, remote: tuple[int, float]) -> None:
        self.log(f"    ↓ {rel}\n")
        if self.dry_run:
            self.stats.downloaded += 1
            return
        res = self.client.request(
            "GET", f"/api/v1/browse/{self.share_id}/content",
            query={"path": self._remote_path(rel)})
        if not res.ok:
            self.stats.failed += 1
            return
        local_path = self._local_path(rel)
        os.makedirs(os.path.dirname(local_path) or ".", exist_ok=True)
        with open(local_path, "wb") as fh:
            fh.write(res.body_bytes)
        # Match the remote mtime so the next run sees the two sides as equal.
        os.utime(local_path, (time.time(), remote[1]))
        self._local_after.add(rel)
        self.stats.downloaded += 1

    def _upload(self, rel: str) -> None:
        self.log(f"    ↑ {rel}\n")
        if self.dry_run:
            self.stats.uploaded += 1
            return
        try:
            with open(self._local_path(rel), "rb") as fh:
                data = fh.read()
        except OSError:
            self.stats.failed += 1
            return
        res = self.client.request(
            "PUT", f"/api/v1/browse/{self.share_id}/content",
            query={"path": self._remote_path(rel)}, raw_body=data)
        if res.ok:
            self._remote_after.add(rel)
        self._tally(res.ok, "uploaded")

    def _make_local_dir(self, rel: str) -> None:
        self.log(f"    + local dir {rel}\n")
        if not self.dry_run:
            os.makedirs(self._local_path(rel), exist_ok=True)
        self.stats.created_local_dirs += 1

    def _make_remote_dir(self, rel: str) -> None:
        self.log(f"    + remote dir {rel}\n")
        if self.dry_run:
            self.stats.created_remote_dirs += 1
            return
        res = self.client.request(
            "POST", f"/api/v1/browse/{self.share_id}/directory",
            query={"path": self._remote_path(rel)})
        self._tally(res.ok, "created_remote_dirs")

    def _mirror_delete_local(self, extra_files: set[str], extra_dirs: set[str]) -> None:
        for rel in sorted(extra_files, key=lambda r: -r.count("/")):
            if self.should_stop():
                return
            self.log(f"    − local {rel}\n")
            self._delete_local_path(self._local_path(rel), is_dir=False)
        for rel in sorted(extra_dirs, key=lambda r: -r.count("/")):
            if self.should_stop():
                return
            self.log(f"    − local dir {rel}\n")
            self._delete_local_path(self._local_path(rel), is_dir=True)

    def _delete_local_file(self, rel: str) -> None:
        """Delete a single local file (two-way delete propagation) and forget it."""
        self._delete_local_path(self._local_path(rel), is_dir=False)
        self._local_after.discard(rel)

    def _delete_remote_file(self, rel: str) -> None:
        """Delete a single remote file (two-way delete propagation) and forget it."""
        self._delete_remote_path(rel)
        self._remote_after.discard(rel)

    def _delete_local_path(self, path: str, is_dir: bool) -> None:
        if self.dry_run:
            self.stats.deleted_local += 1
            return
        try:
            os.rmdir(path) if is_dir else os.remove(path)
            self.stats.deleted_local += 1
        except OSError:
            self.stats.failed += 1

    def _mirror_delete_remote(self, extra_files: set[str], extra_dirs: set[str]) -> None:
        # Files first, then directories deepest-first, so a directory is empty
        # by the time we remove it.
        for rel in sorted(extra_files, key=lambda r: -r.count("/")):
            if self.should_stop():
                return
            self.log(f"    − remote {rel}\n")
            self._delete_remote_path(rel)
        for rel in sorted(extra_dirs, key=lambda r: -r.count("/")):
            if self.should_stop():
                return
            self.log(f"    − remote dir {rel}\n")
            self._delete_remote_path(rel)

    def _delete_remote_path(self, rel: str) -> None:
        if self.dry_run:
            self.stats.deleted_remote += 1
            return
        res = self.client.request(
            "DELETE", f"/api/v1/browse/{self.share_id}/item",
            query={"path": self._remote_path(rel)})
        self._tally(res.ok, "deleted_remote")

    def _tally(self, ok: bool, field_name: str) -> None:
        if ok:
            setattr(self.stats, field_name, getattr(self.stats, field_name) + 1)
        else:
            self.stats.failed += 1

    def _stopped(self, token: Optional[str]) -> Optional[str]:
        self.log(f"  ⧗ stopped before completion — {self.stats.summary()}\n")
        return token


# ────────────────────────────── GUI ──────────────────────────────


class App:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        root.title("Kaimo File Server — API test client")
        root.geometry("1000x760")

        # session state
        self.client: Optional[ApiClient] = None
        self.device_id: Optional[str] = None
        self.refresh_token: Optional[str] = None
        self.shares: list[dict] = []          # [{Id, Name, IsRecycleEnabled}]
        self.current_share_id: Optional[str] = None
        self.current_path: str = ""
        self.profiles_by_id: dict[str, dict] = {}   # raw sync profiles, keyed by id
        self.sync_running: bool = False             # a one-shot or loop sync is active
        self.auto_thread: Optional[threading.Thread] = None
        self.auto_stop = threading.Event()          # signals the auto-sync loop to quit

        self._build_connection_frame()
        self._build_auth_frame()
        self._build_notebook()
        self._build_log()

    # -- construction --

    def _build_connection_frame(self) -> None:
        frame = ttk.LabelFrame(self.root, text="Connection")
        frame.pack(fill="x", padx=8, pady=(8, 4))

        ttk.Label(frame, text="Base URL:").grid(row=0, column=0, sticky="w", padx=4, pady=4)
        self.base_url_var = tk.StringVar(value=DEFAULT_BASE_URL)
        ttk.Entry(frame, textvariable=self.base_url_var, width=40).grid(
            row=0, column=1, sticky="w", padx=4)

        self.verify_tls_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(frame, text="Verify TLS", variable=self.verify_tls_var).grid(
            row=0, column=2, sticky="w", padx=8)

        self.status_var = tk.StringVar(value="not signed in")
        ttk.Label(frame, textvariable=self.status_var, foreground="#666").grid(
            row=0, column=3, sticky="e", padx=8)
        frame.columnconfigure(3, weight=1)

    def _build_auth_frame(self) -> None:
        frame = ttk.LabelFrame(self.root, text="Authentication")
        frame.pack(fill="x", padx=8, pady=4)

        ttk.Label(frame, text="Username:").grid(row=0, column=0, sticky="w", padx=4, pady=4)
        self.username_var = tk.StringVar(value="admin")
        ttk.Entry(frame, textvariable=self.username_var, width=20).grid(row=0, column=1, sticky="w")

        ttk.Label(frame, text="Password:").grid(row=0, column=2, sticky="w", padx=4)
        self.password_var = tk.StringVar()
        ttk.Entry(frame, textvariable=self.password_var, width=20, show="•").grid(
            row=0, column=3, sticky="w")

        ttk.Label(frame, text="Device name:").grid(row=1, column=0, sticky="w", padx=4, pady=4)
        self.device_name_var = tk.StringVar(value="Test client")
        ttk.Entry(frame, textvariable=self.device_name_var, width=20).grid(row=1, column=1, sticky="w")

        ttk.Label(frame, text="Platform:").grid(row=1, column=2, sticky="w", padx=4)
        self.platform_var = tk.StringVar(value="python-test")
        ttk.Entry(frame, textvariable=self.platform_var, width=20).grid(row=1, column=3, sticky="w")

        buttons = ttk.Frame(frame)
        buttons.grid(row=0, column=4, rowspan=2, padx=8)
        ttk.Button(buttons, text="Login", command=self.do_login).pack(fill="x", pady=1)
        ttk.Button(buttons, text="Refresh token", command=self.do_refresh).pack(fill="x", pady=1)
        ttk.Button(buttons, text="Logout", command=self.do_logout).pack(fill="x", pady=1)

        self.token_var = tk.StringVar(value="access token: —   device: —")
        ttk.Label(frame, textvariable=self.token_var, foreground="#666").grid(
            row=2, column=0, columnspan=5, sticky="w", padx=4, pady=(0, 4))

    def _build_notebook(self) -> None:
        nb = ttk.Notebook(self.root)
        nb.pack(fill="both", expand=True, padx=8, pady=4)
        self._build_browse_tab(nb)
        self._build_sync_tab(nb)

    def _build_browse_tab(self, nb: ttk.Notebook) -> None:
        tab = ttk.Frame(nb)
        nb.add(tab, text="Browse")

        top = ttk.Frame(tab)
        top.pack(fill="x", pady=4)
        ttk.Button(top, text="Load shares", command=self.load_shares).pack(side="left", padx=2)
        self.share_combo = ttk.Combobox(top, state="readonly", width=30)
        self.share_combo.pack(side="left", padx=2)
        self.share_combo.bind("<<ComboboxSelected>>", lambda _e: self.on_share_selected())
        ttk.Button(top, text="Up", command=self.browse_up).pack(side="left", padx=2)
        ttk.Button(top, text="Refresh list", command=self.list_current).pack(side="left", padx=2)

        path_row = ttk.Frame(tab)
        path_row.pack(fill="x", pady=2)
        ttk.Label(path_row, text="Path:").pack(side="left")
        self.path_var = tk.StringVar(value="")
        ttk.Entry(path_row, textvariable=self.path_var, width=60).pack(side="left", padx=4)
        ttk.Button(path_row, text="Go", command=self.list_current).pack(side="left")

        self.tree = ttk.Treeview(
            tab, columns=("type", "size", "modified"), show="tree headings", height=12)
        self.tree.heading("#0", text="Name")
        self.tree.heading("type", text="Type")
        self.tree.heading("size", text="Size")
        self.tree.heading("modified", text="Modified (UTC)")
        self.tree.column("type", width=80, anchor="center")
        self.tree.column("size", width=90, anchor="e")
        self.tree.column("modified", width=160, anchor="center")
        self.tree.pack(fill="both", expand=True, pady=4)
        self.tree.bind("<Double-1>", lambda _e: self.on_entry_double_click())

        actions = ttk.Frame(tab)
        actions.pack(fill="x", pady=2)
        ttk.Button(actions, text="Download selected", command=self.download_selected).pack(side="left", padx=2)
        ttk.Button(actions, text="Upload file here…", command=self.upload_file).pack(side="left", padx=2)
        ttk.Button(actions, text="New folder…", command=self.create_folder).pack(side="left", padx=2)
        ttk.Button(actions, text="Delete selected", command=self.delete_selected).pack(side="left", padx=2)

    def _build_sync_tab(self, nb: ttk.Notebook) -> None:
        tab = ttk.Frame(nb)
        nb.add(tab, text="Sync")

        top = ttk.Frame(tab)
        top.pack(fill="x", pady=4)
        ttk.Button(top, text="Load connections", command=self.load_profiles).pack(side="left", padx=2)
        self.this_device_var = tk.StringVar(value="this device: — (log in first)")
        ttk.Label(top, textvariable=self.this_device_var, foreground="#666").pack(side="left", padx=8)

        self.profile_tree = ttk.Treeview(
            tab, columns=("share", "remote", "local", "mode", "enabled"), show="headings", height=8)
        for col, text, width in (
            ("share", "Share", 160), ("remote", "Remote folder", 180),
            ("local", "Local folder", 200), ("mode", "Direction", 110),
            ("enabled", "Enabled", 70),
        ):
            self.profile_tree.heading(col, text=text)
            self.profile_tree.column(col, width=width, anchor="w")
        self.profile_tree.pack(fill="x", pady=4)

        form = ttk.LabelFrame(
            tab, text="Create sync connection (remote share folder ↔ local device folder)")
        form.pack(fill="x", pady=4)

        # Remote endpoint: a share + a share-relative folder.
        ttk.Label(form, text="Remote share:").grid(row=0, column=0, sticky="w", padx=4, pady=4)
        self.sync_share_combo = ttk.Combobox(form, state="readonly", width=26)
        self.sync_share_combo.grid(row=0, column=1, sticky="w")
        ttk.Label(form, text="Remote folder:").grid(row=0, column=2, sticky="w", padx=4)
        self.sync_path_var = tk.StringVar(value="")
        ttk.Entry(form, textvariable=self.sync_path_var, width=24).grid(row=0, column=3, sticky="w")
        ttk.Button(form, text="Pick from Browse tab", command=self.use_browse_path).grid(
            row=0, column=4, padx=4)

        # Local endpoint: a folder on this machine.
        ttk.Label(form, text="Local folder:").grid(row=1, column=0, sticky="w", padx=4, pady=4)
        self.sync_local_var = tk.StringVar(value="")
        ttk.Entry(form, textvariable=self.sync_local_var, width=40).grid(
            row=1, column=1, columnspan=2, sticky="we")
        ttk.Button(form, text="Browse…", command=self.pick_local_folder).grid(row=1, column=3, sticky="w")

        ttk.Label(form, text="Direction:").grid(row=2, column=0, sticky="w", padx=4, pady=4)
        self.sync_mode_var = tk.StringVar(value="TwoWay")
        ttk.Combobox(
            form, state="readonly", width=12, textvariable=self.sync_mode_var,
            values=["TwoWay", "Pull", "Push"]).grid(row=2, column=1, sticky="w")
        ttk.Button(form, text="Create", command=self.create_profile).grid(row=2, column=3, sticky="w", padx=4)
        ttk.Button(form, text="Delete selected", command=self.delete_profile).grid(
            row=2, column=4, sticky="w")

        delta = ttk.LabelFrame(tab, text="Delta & change notification")
        delta.pack(fill="x", pady=4)
        ttk.Button(delta, text="Get delta (share + path above)", command=self.get_delta).pack(
            side="left", padx=4, pady=4)
        ttk.Button(delta, text="Wait for change (long-poll)", command=self.wait_for_change).pack(
            side="left", padx=4)
        self.last_token_var = tk.StringVar(value="last token: —")
        ttk.Label(delta, textvariable=self.last_token_var, foreground="#666").pack(side="left", padx=8)

        transfer = ttk.LabelFrame(tab, text="Synchronize (actually transfer files ↔ local folder)")
        transfer.pack(fill="x", pady=4)
        row = ttk.Frame(transfer)
        row.pack(fill="x", padx=4, pady=4)
        ttk.Button(row, text="Sync selected connection now", command=self.sync_selected_now).pack(
            side="left", padx=2)
        self.auto_sync_btn = ttk.Button(
            row, text="Start auto-sync (long-poll)", command=self.toggle_auto_sync)
        self.auto_sync_btn.pack(side="left", padx=2)
        self.mirror_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(row, text="Mirror deletes", variable=self.mirror_var).pack(side="left", padx=(12, 2))
        self.dry_run_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(row, text="Dry run", variable=self.dry_run_var).pack(side="left", padx=2)
        ttk.Label(
            transfer, foreground="#666", wraplength=920, justify="left",
            text=(
                "Select a connection above, then Sync. Pull = server→local, "
                "Push = local→server, TwoWay = newest wins. \"Mirror deletes\" removes "
                "extras on the target: Pull/Push delete on the authoritative side; "
                "TwoWay propagates a deletion in either direction using a per-connection "
                "baseline (the first two-way run only builds that baseline). A file "
                "edited on one side after being deleted on the other is kept as a "
                "conflict, never lost. Auto-sync reconciles once, then keeps syncing: "
                "Pull long-polls for server changes; Push/TwoWay also poll the local "
                "folder so local edits get pushed up too.")
        ).pack(anchor="w", padx=6, pady=(0, 4))

    def _build_log(self) -> None:
        frame = ttk.LabelFrame(self.root, text="Debug log")
        frame.pack(fill="both", expand=True, padx=8, pady=(4, 8))
        self.log_text = tk.Text(frame, height=12, wrap="word", font=("Consolas", 9))
        self.log_text.pack(side="left", fill="both", expand=True)
        scrollbar = ttk.Scrollbar(frame, command=self.log_text.yview)
        scrollbar.pack(side="right", fill="y")
        self.log_text.configure(yscrollcommand=scrollbar.set)
        bottom = ttk.Frame(self.root)
        bottom.pack(fill="x", padx=8, pady=(0, 8))
        ttk.Button(bottom, text="Clear log", command=lambda: self.log_text.delete("1.0", "end")).pack(side="right")

    # -- logging / threading helpers --

    def log(self, text: str) -> None:
        """Thread-safe append to the debug log."""
        self.root.after(0, self._append_log, text)

    def _append_log(self, text: str) -> None:
        self.log_text.insert("end", text)
        self.log_text.see("end")

    def ensure_client(self) -> Optional[ApiClient]:
        base = self.base_url_var.get().strip()
        if not base:
            messagebox.showerror("Missing URL", "Enter the server base URL first.")
            return None
        if self.client is None or self.client.base_url != base.rstrip("/") \
                or self.client.verify_tls != self.verify_tls_var.get():
            token = self.client.access_token if self.client else None
            self.client = ApiClient(base, self.verify_tls_var.get(), self.log)
            self.client.access_token = token
        return self.client

    def run_async(self, fn: Callable[[], ApiResult], on_done: Callable[[ApiResult], None]) -> None:
        """Run a network call off the UI thread, then marshal the result back."""
        def worker() -> None:
            try:
                result = fn()
            except Exception as exc:  # noqa: BLE001
                result = ApiResult(False, 0, 0.0, error=str(exc))
            self.root.after(0, lambda: on_done(result))

        threading.Thread(target=worker, daemon=True).start()

    # ────────────── auth actions ──────────────

    def do_login(self) -> None:
        client = self.ensure_client()
        if client is None:
            return
        body = {
            "username": self.username_var.get(),
            "password": self.password_var.get(),
            "deviceId": self.device_id,
            "deviceName": self.device_name_var.get(),
            "platform": self.platform_var.get(),
        }
        self.log("\n=== LOGIN ===\n")
        self.run_async(
            lambda: client.request("POST", "/api/v1/auth/login", json_body=body, auth=False),
            self._on_login)

    def _on_login(self, result: ApiResult) -> None:
        if result.ok and isinstance(result.json, dict):
            self.client.access_token = result.json.get("accessToken")
            self.refresh_token = result.json.get("refreshToken")
            self.device_id = result.json.get("deviceId") or self.device_id
            self.status_var.set(f"signed in as {self.username_var.get()}")
            self._update_token_label(result.json.get("expiresInSeconds"))
            self.this_device_var.set(
                f"this device: {self.device_name_var.get()} ({self.device_id})")
        else:
            self.status_var.set("login failed")
            messagebox.showerror("Login failed", self._describe(result))

    def do_refresh(self) -> None:
        client = self.ensure_client()
        if client is None or not self.refresh_token:
            messagebox.showinfo("No token", "Log in first to obtain a refresh token.")
            return
        self.log("\n=== REFRESH ===\n")
        self.run_async(
            lambda: client.request(
                "POST", "/api/v1/auth/refresh",
                json_body={"refreshToken": self.refresh_token}, auth=False),
            self._on_refresh)

    def _on_refresh(self, result: ApiResult) -> None:
        if result.ok and isinstance(result.json, dict):
            self.client.access_token = result.json.get("accessToken")
            self.refresh_token = result.json.get("refreshToken")  # rotation → store the new one
            self.status_var.set("token refreshed")
            self._update_token_label(result.json.get("expiresInSeconds"))
        else:
            messagebox.showerror("Refresh failed", self._describe(result))

    def do_logout(self) -> None:
        client = self.ensure_client()
        if client is None or not self.refresh_token:
            return
        self.log("\n=== LOGOUT ===\n")
        token = self.refresh_token
        self.run_async(
            lambda: client.request(
                "POST", "/api/v1/auth/logout", json_body={"refreshToken": token}, auth=False),
            self._on_logout)

    def _on_logout(self, result: ApiResult) -> None:
        self.client.access_token = None
        self.refresh_token = None
        self.status_var.set("signed out")
        self.token_var.set("access token: —   device: —")
        self.this_device_var.set("this device: — (log in first)")

    def _update_token_label(self, expires: Optional[int]) -> None:
        token = self.client.access_token if self.client else None
        short = (token[:16] + "…") if token else "—"
        self.token_var.set(
            f"access token: {short}   expires in: {expires}s   device: {self.device_id or '—'}")

    # ────────────── browse actions ──────────────

    def load_shares(self) -> None:
        client = self.ensure_client()
        if client is None:
            return
        self.log("\n=== SHARES ===\n")
        self.run_async(lambda: client.request("GET", "/api/v1/browse/shares"), self._on_shares)

    def _on_shares(self, result: ApiResult) -> None:
        if not result.ok or not isinstance(result.json, list):
            messagebox.showerror("Failed", self._describe(result))
            return
        self.shares = result.json
        labels = [f"{s['name']}  ({s['id'][:8]}…)" for s in self.shares]
        self.share_combo["values"] = labels
        self.sync_share_combo["values"] = labels
        if labels:
            self.share_combo.current(0)
            self.sync_share_combo.current(0)
            self.on_share_selected()

    def _selected_share_id(self, combo: ttk.Combobox) -> Optional[str]:
        idx = combo.current()
        if idx < 0 or idx >= len(self.shares):
            return None
        return self.shares[idx]["id"]

    def on_share_selected(self) -> None:
        self.current_share_id = self._selected_share_id(self.share_combo)
        self.current_path = ""
        self.path_var.set("")
        self.list_current()

    def list_current(self) -> None:
        client = self.ensure_client()
        if client is None or not self.current_share_id:
            return
        self.current_path = self.path_var.get().strip()
        self.log(f"\n=== LIST {self.current_path or '/'} ===\n")
        self.run_async(
            lambda: client.request(
                "GET", f"/api/v1/browse/{self.current_share_id}/list",
                query={"path": self.current_path}),
            self._on_list)

    def _on_list(self, result: ApiResult) -> None:
        self.tree.delete(*self.tree.get_children())
        if result.status == 304:
            return
        if not result.ok or not isinstance(result.json, list):
            messagebox.showerror("List failed", self._describe(result))
            return
        # directories first, then files, both alphabetical
        for entry in sorted(result.json, key=lambda e: (not e["isDirectory"], e["name"].lower())):
            self.tree.insert(
                "", "end", iid=entry["path"], text=entry["name"],
                values=(
                    "dir" if entry["isDirectory"] else "file",
                    "" if entry["isDirectory"] else entry["size"],
                    entry["modifiedAtUtc"][:19].replace("T", " "),
                ))

    def _selected_entry(self) -> Optional[tuple[str, bool]]:
        sel = self.tree.selection()
        if not sel:
            return None
        path = sel[0]
        is_dir = self.tree.set(path, "type") == "dir"
        return path, is_dir

    def on_entry_double_click(self) -> None:
        entry = self._selected_entry()
        if entry and entry[1]:  # navigate into a directory
            self.path_var.set(entry[0])
            self.list_current()

    def browse_up(self) -> None:
        if "/" in self.current_path:
            self.path_var.set(self.current_path.rsplit("/", 1)[0])
        else:
            self.path_var.set("")
        self.list_current()

    def download_selected(self) -> None:
        client = self.ensure_client()
        entry = self._selected_entry()
        if client is None or entry is None or entry[1]:
            messagebox.showinfo("Pick a file", "Select a file (not a folder) to download.")
            return
        path = entry[0]
        target = filedialog.asksaveasfilename(initialfile=path.rsplit("/", 1)[-1])
        if not target:
            return
        self.log(f"\n=== DOWNLOAD {path} ===\n")

        def do() -> ApiResult:
            res = client.request(
                "GET", f"/api/v1/browse/{self.current_share_id}/content", query={"path": path})
            if res.ok:
                with open(target, "wb") as fh:
                    fh.write(res.body_bytes)
            return res

        self.run_async(do, lambda r: self._simple_done(r, f"Saved {len(r.body_bytes)} bytes"))

    def upload_file(self) -> None:
        client = self.ensure_client()
        if client is None or not self.current_share_id:
            return
        source = filedialog.askopenfilename()
        if not source:
            return
        name = source.replace("\\", "/").rsplit("/", 1)[-1]
        remote = f"{self.current_path}/{name}" if self.current_path else name
        with open(source, "rb") as fh:
            data = fh.read()
        self.log(f"\n=== UPLOAD {remote} ({len(data)} bytes) ===\n")
        self.run_async(
            lambda: client.request(
                "PUT", f"/api/v1/browse/{self.current_share_id}/content",
                query={"path": remote}, raw_body=data),
            lambda r: self._simple_done(r, "Uploaded", refresh_list=True))

    def create_folder(self) -> None:
        client = self.ensure_client()
        if client is None or not self.current_share_id:
            return
        name = _prompt(self.root, "New folder", "Folder name:")
        if not name:
            return
        remote = f"{self.current_path}/{name}" if self.current_path else name
        self.log(f"\n=== MKDIR {remote} ===\n")
        self.run_async(
            lambda: client.request(
                "POST", f"/api/v1/browse/{self.current_share_id}/directory", query={"path": remote}),
            lambda r: self._simple_done(r, "Created", refresh_list=True))

    def delete_selected(self) -> None:
        client = self.ensure_client()
        entry = self._selected_entry()
        if client is None or entry is None:
            return
        if not messagebox.askyesno("Delete", f"Delete '{entry[0]}'?"):
            return
        self.log(f"\n=== DELETE {entry[0]} ===\n")
        self.run_async(
            lambda: client.request(
                "DELETE", f"/api/v1/browse/{self.current_share_id}/item", query={"path": entry[0]}),
            lambda r: self._simple_done(r, "Deleted", refresh_list=True))

    # ────────────── sync actions ──────────────
    # This client *is* the device (registered at login), so all sync actions
    # operate on self.device_id — there is no device to choose.

    def load_profiles(self) -> None:
        client = self.ensure_client()
        if client is None or not self.device_id:
            messagebox.showinfo(
                "Not signed in", "Log in first — this client registers itself as the device.")
            return
        self.log("\n=== CONNECTIONS ===\n")
        self.run_async(
            lambda: client.request(
                "GET", "/api/v1/sync/profiles", query={"deviceId": self.device_id}),
            self._on_profiles)

    def _on_profiles(self, result: ApiResult) -> None:
        self.profile_tree.delete(*self.profile_tree.get_children())
        if not result.ok or not isinstance(result.json, list):
            messagebox.showerror("Failed", self._describe(result))
            return
        # Keep the raw profiles so the synchronizer can read shareId/paths/mode.
        self.profiles_by_id = {p["id"]: p for p in result.json}
        for p in result.json:
            self.profile_tree.insert(
                "", "end", iid=p["id"],
                values=(
                    self._share_name(p["shareId"]),
                    p["relativePath"] or "/",
                    p.get("localPath") or "—",
                    p["mode"],
                    "yes" if p["enabled"] else "no",
                ))

    def _share_name(self, share_id: str) -> str:
        for s in self.shares:
            if s["id"] == share_id:
                return s["name"]
        return share_id[:8] + "…"

    def pick_local_folder(self) -> None:
        """Choose the device-local endpoint folder for a new sync connection."""
        folder = filedialog.askdirectory(title="Select the local folder to sync")
        if folder:
            self.sync_local_var.set(folder)

    def use_browse_path(self) -> None:
        """Copy the currently browsed share + path into the create form as the remote endpoint."""
        if self.current_share_id:
            # Point the remote-share combo at the share open in the Browse tab.
            for idx, share in enumerate(self.shares):
                if share["id"] == self.current_share_id:
                    self.sync_share_combo.current(idx)
                    break
        self.sync_path_var.set(self.current_path)

    def create_profile(self) -> None:
        client = self.ensure_client()
        share_id = self._selected_share_id(self.sync_share_combo)
        if client is None or not self.device_id or not share_id:
            messagebox.showinfo("Missing", "Log in and pick a remote share first.")
            return
        body = {
            "deviceId": self.device_id,
            "shareId": share_id,
            "relativePath": self.sync_path_var.get().strip(),
            "localPath": self.sync_local_var.get().strip(),
            "mode": self.sync_mode_var.get(),
            "enabled": True,
        }
        self.log("\n=== CREATE CONNECTION ===\n")
        self.run_async(
            lambda: client.request("POST", "/api/v1/sync/profiles", json_body=body),
            lambda r: self._simple_done(r, "Created", reload_profiles=True))

    def delete_profile(self) -> None:
        client = self.ensure_client()
        sel = self.profile_tree.selection()
        if client is None or not sel:
            return
        profile_id = sel[0]
        self.log(f"\n=== DELETE PROFILE {profile_id} ===\n")
        # Drop the local sync baseline too, so a later profile with the same id
        # (or a fresh connection) does not inherit a stale converged state.
        SyncState(profile_id).clear()
        self.run_async(
            lambda: client.request("DELETE", f"/api/v1/sync/profiles/{profile_id}"),
            lambda r: self._simple_done(r, "Deleted", reload_profiles=True))

    def get_delta(self) -> None:
        client = self.ensure_client()
        share_id = self._selected_share_id(self.sync_share_combo)
        if client is None or not share_id:
            return
        path = self.sync_path_var.get().strip()
        self.log(f"\n=== DELTA {path or '/'} ===\n")
        self.run_async(
            lambda: client.request(
                "GET", f"/api/v1/sync/{share_id}/delta", query={"path": path}),
            self._on_delta)

    def _on_delta(self, result: ApiResult) -> None:
        if result.ok and isinstance(result.json, dict):
            entries = result.json.get("entries", [])
            token = result.json.get("token")
            self.last_token_var.set(f"last token: {token}")
            self.log(f"  → {len(entries)} entries, token {token}\n")
        else:
            messagebox.showerror("Delta failed", self._describe(result))

    def wait_for_change(self) -> None:
        client = self.ensure_client()
        share_id = self._selected_share_id(self.sync_share_combo)
        if client is None or not share_id:
            return
        path = self.sync_path_var.get().strip()
        since = self.last_token_var.get().replace("last token: ", "").strip()
        since = None if since in ("", "—") else since
        self.log(f"\n=== WAIT FOR CHANGE (since={since}) — long-poll, up to ~30s ===\n")
        self.run_async(
            lambda: client.request(
                "GET", "/api/v1/sync/changes/wait",
                query={"shareId": share_id, "path": path, "since": since}, timeout=45.0),
            self._on_wait)

    def _on_wait(self, result: ApiResult) -> None:
        if result.ok and isinstance(result.json, dict):
            token = result.json.get("token")
            changed = result.json.get("changed")
            self.last_token_var.set(f"last token: {token}")
            self.log(f"  → changed={changed}, token {token}\n")
        else:
            messagebox.showerror("Wait failed", self._describe(result))

    # ────────────── real synchronization ──────────────

    def _selected_profile(self) -> Optional[dict]:
        """The sync connection highlighted in the profile list, or None."""
        sel = self.profile_tree.selection()
        if not sel:
            messagebox.showinfo("Pick a connection", "Select a sync connection in the list first.")
            return None
        profile = self.profiles_by_id.get(sel[0])
        if profile is None:
            messagebox.showinfo("Reload", "Reload connections and try again.")
            return None
        if not profile.get("localPath"):
            messagebox.showerror("No local folder", "This connection has no local folder set.")
            return None
        return profile

    def _make_engine(self, client: ApiClient, profile: dict,
                     should_stop: Callable[[], bool]) -> SyncEngine:
        return SyncEngine(
            client,
            share_id=profile["shareId"],
            remote_root=profile.get("relativePath") or "",
            local_root=profile["localPath"],
            mode=profile["mode"],
            mirror=self.mirror_var.get(),
            dry_run=self.dry_run_var.get(),
            log=self.log,
            should_stop=should_stop,
            state=SyncState(profile["id"]),
        )

    def sync_selected_now(self) -> None:
        client = self.ensure_client()
        if client is None:
            return
        if self.sync_running:
            messagebox.showinfo("Busy", "A sync is already running.")
            return
        profile = self._selected_profile()
        if profile is None:
            return
        engine = self._make_engine(client, profile, lambda: False)
        self.sync_running = True
        self.log(
            f"\n=== SYNC {profile['mode']} '{profile.get('relativePath') or '/'}' "
            f"↔ {profile['localPath']}"
            f"{' (dry run)' if self.dry_run_var.get() else ''} ===\n")

        def work() -> None:
            try:
                engine.run()
            except Exception as exc:  # noqa: BLE001 - surface any failure in the log
                self.log(f"  ✗ sync error: {exc}\n")
            finally:
                self.sync_running = False

        threading.Thread(target=work, daemon=True).start()

    def toggle_auto_sync(self) -> None:
        # Second click: ask the running loop to stop and flip the button back.
        if self.auto_thread and self.auto_thread.is_alive():
            self.auto_stop.set()
            self.auto_sync_btn.config(text="Start auto-sync (long-poll)")
            self.log("\n=== AUTO-SYNC stopping… ===\n")
            return

        client = self.ensure_client()
        if client is None:
            return
        profile = self._selected_profile()
        if profile is None:
            return
        self.auto_stop = threading.Event()
        self.auto_sync_btn.config(text="Stop auto-sync")
        self.log(
            f"\n=== AUTO-SYNC {profile['mode']} '{profile.get('relativePath') or '/'}' "
            f"↔ {profile['localPath']} ===\n")
        self.auto_thread = threading.Thread(
            target=self._auto_sync_loop, args=(client, profile), daemon=True)
        self.auto_thread.start()

    # How often the auto-sync loop re-checks a folder it has to watch locally.
    AUTO_POLL_SECONDS = 3.0

    @staticmethod
    def _local_signature(root: str) -> tuple:
        """A cheap fingerprint of the local tree (rel path, size, mtime) for change
        detection. Two calls compare equal iff no file was added, removed, or
        touched — this is how the loop notices local edits the server can't."""
        if not os.path.isdir(root):
            return ()
        sig: list[tuple[str, int, int]] = []
        for base, _dirs, files in os.walk(root):
            for name in files:
                full = os.path.join(base, name)
                try:
                    st = os.stat(full)
                except OSError:
                    continue
                sig.append((os.path.relpath(full, root), st.st_size, int(st.st_mtime)))
        return tuple(sorted(sig))

    def _auto_sync_loop(self, client: ApiClient, profile: dict) -> None:
        """
        Reconcile once, then keep the two sides in sync until auto_stop.

        The server's change-wait long-poll only reports *server-side* changes, so
        it cannot on its own drive a local→server sync. Therefore:

          * Pull            — nothing local to watch, so we use the efficient
                              long-poll and re-sync whenever the server changes.
          * Push / TwoWay   — we also poll a local fingerprint every
                              AUTO_POLL_SECONDS and re-sync when either side moved,
                              so local edits get pushed up without a server event.
        """
        share_id = profile["shareId"]
        remote_root = profile.get("relativePath") or ""
        local_root = profile["localPath"]
        watch_local = profile["mode"] in ("Push", "TwoWay")
        stop = self.auto_stop

        def current_token() -> Optional[str]:
            # An empty "since" makes the wait endpoint return the current token at
            # once — a cheap "what's the server on now?" probe.
            res = client.request(
                "GET", "/api/v1/sync/changes/wait",
                query={"shareId": share_id, "path": remote_root, "since": None}, timeout=45.0)
            return res.json.get("token") if res.ok and isinstance(res.json, dict) else None

        def reconcile() -> None:
            self.log("\n--- auto-sync: reconciling ---\n")
            self.sync_running = True
            try:
                self._make_engine(client, profile, stop.is_set).run()
            except Exception as exc:  # noqa: BLE001
                self.log(f"  ✗ sync error: {exc}\n")
            finally:
                self.sync_running = False

        token: Optional[str] = None
        local_sig = self._local_signature(local_root) if watch_local else ()
        first = True
        while not stop.is_set():
            trigger = first
            first = False

            if watch_local:
                # Poll both sides: cheap token probe + local fingerprint.
                new_token = current_token()
                new_sig = self._local_signature(local_root)
                if new_token != token or new_sig != local_sig:
                    trigger = True
                token, local_sig = new_token, new_sig
            else:
                # Pull: block on the long-poll until the server actually changes.
                res = client.request(
                    "GET", "/api/v1/sync/changes/wait",
                    query={"shareId": share_id, "path": remote_root, "since": token or None},
                    timeout=45.0)
                if stop.is_set():
                    break
                if not res.ok or not isinstance(res.json, dict):
                    self.log("  auto-sync: change-wait failed; retrying in 5s\n")
                    stop.wait(5.0)
                    continue
                if res.json.get("changed"):
                    trigger = True
                token = res.json.get("token") or token

            self.last_token_var.set(f"last token: {token}")
            if trigger:
                reconcile()
                if watch_local:
                    # Re-baseline so our own writes don't re-trigger next tick.
                    token = current_token()
                    local_sig = self._local_signature(local_root)

            if watch_local:
                stop.wait(self.AUTO_POLL_SECONDS)
        self.log("=== AUTO-SYNC stopped ===\n")

    # -- shared result handling --

    def _simple_done(self, result: ApiResult, ok_message: str,
                     refresh_list: bool = False, reload_profiles: bool = False) -> None:
        if result.ok:
            self.log(f"  → {ok_message}\n")
            if refresh_list:
                self.list_current()
            if reload_profiles:
                self.load_profiles()
        else:
            messagebox.showerror("Failed", self._describe(result))

    @staticmethod
    def _describe(result: ApiResult) -> str:
        if result.error:
            return result.error
        if isinstance(result.json, dict) and "code" in result.json:
            return f"{result.status} {result.json.get('code')}: {result.json.get('message')}"
        return f"HTTP {result.status}"


def _prompt(root: tk.Tk, title: str, label: str) -> Optional[str]:
    """Minimal modal text prompt (avoids importing tkinter.simpledialog quirks)."""
    dialog = tk.Toplevel(root)
    dialog.title(title)
    dialog.transient(root)
    dialog.grab_set()
    ttk.Label(dialog, text=label).pack(padx=10, pady=(10, 4))
    var = tk.StringVar()
    entry = ttk.Entry(dialog, textvariable=var, width=40)
    entry.pack(padx=10, pady=4)
    entry.focus_set()
    result: dict[str, Optional[str]] = {"value": None}

    def ok() -> None:
        result["value"] = var.get()
        dialog.destroy()

    row = ttk.Frame(dialog)
    row.pack(pady=8)
    ttk.Button(row, text="OK", command=ok).pack(side="left", padx=4)
    ttk.Button(row, text="Cancel", command=dialog.destroy).pack(side="left", padx=4)
    entry.bind("<Return>", lambda _e: ok())
    root.wait_window(dialog)
    return result["value"]


def main() -> None:
    root = tk.Tk()
    App(root)
    root.mainloop()


if __name__ == "__main__":
    main()
