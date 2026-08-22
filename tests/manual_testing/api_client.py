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
            delta enumeration, and the long-poll change-wait.

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

import json
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
        self.devices: list[dict] = []
        self.current_share_id: Optional[str] = None
        self.current_path: str = ""

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
        ttk.Button(top, text="Load devices", command=self.load_devices).pack(side="left", padx=2)
        self.device_combo = ttk.Combobox(top, state="readonly", width=40)
        self.device_combo.pack(side="left", padx=2)
        self.device_combo.bind("<<ComboboxSelected>>", lambda _e: self.load_profiles())

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

    def load_devices(self) -> None:
        client = self.ensure_client()
        if client is None:
            return
        self.log("\n=== DEVICES ===\n")
        self.run_async(lambda: client.request("GET", "/api/v1/sync/devices"), self._on_devices)

    def _on_devices(self, result: ApiResult) -> None:
        if not result.ok or not isinstance(result.json, list):
            messagebox.showerror("Failed", self._describe(result))
            return
        self.devices = result.json
        self.device_combo["values"] = [
            f"{d['displayName']}  ({d['platform']}, {d['id'][:8]}…)" for d in self.devices]
        if self.devices:
            self.device_combo.current(0)
            self.load_profiles()

    def _selected_device_id(self) -> Optional[str]:
        idx = self.device_combo.current()
        if idx < 0 or idx >= len(self.devices):
            return None
        return self.devices[idx]["id"]

    def load_profiles(self) -> None:
        client = self.ensure_client()
        device_id = self._selected_device_id()
        if client is None or not device_id:
            return
        self.log("\n=== PROFILES ===\n")
        self.run_async(
            lambda: client.request("GET", "/api/v1/sync/profiles", query={"deviceId": device_id}),
            self._on_profiles)

    def _on_profiles(self, result: ApiResult) -> None:
        self.profile_tree.delete(*self.profile_tree.get_children())
        if not result.ok or not isinstance(result.json, list):
            messagebox.showerror("Failed", self._describe(result))
            return
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
        device_id = self._selected_device_id()
        share_id = self._selected_share_id(self.sync_share_combo)
        if client is None or not device_id or not share_id:
            messagebox.showinfo("Missing", "Load devices and shares, then pick one of each.")
            return
        body = {
            "deviceId": device_id,
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
