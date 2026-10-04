# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""Shared harness for the live Samba VFS / authd tests.

* ``protocol`` mirrors ``module/local_protocol.h`` by parsing the header at
  import time, so a protocol bump can never again leave a test talking an
  outdated version (the historic VERSION = 1 drift).
* ``FakeAuthd`` is a scriptable stand-in for kaimo_authd on the VFS side of the
  Unix socket: per-operation replies (ALLOW with payload, DENY, NOT_FOUND,
  OVERLOADED, UNAUTHORIZED_PEER, ERROR), raw/malformed frames, stalls, closed
  connections and pre-reply callbacks (deterministic TOCTOU injection). Every
  request is recorded with its decoded fields.
* ``Samba`` runs a private smbd with ``vfs objects = kaimo_bridge`` against an
  isolated config, passdb and state tree, and wraps smbclient.
"""

from __future__ import annotations

import dataclasses
import os
import pwd
import re
import shutil
import signal
import socket
import struct
import subprocess
import threading
import time
from pathlib import Path
from types import SimpleNamespace
from typing import Callable, Iterable

HEADER = struct.Struct("!4sBBBBI")
MAGIC = b"KAIM"

SAMBA_PREFIX = Path(os.environ.get("KAIMO_SAMBA_PREFIX", "/opt/samba"))
SMBD = SAMBA_PREFIX / "sbin" / "smbd"
SMBCLIENT = SAMBA_PREFIX / "bin" / "smbclient"
SMBPASSWD = SAMBA_PREFIX / "bin" / "smbpasswd"
TESTPARM = SAMBA_PREFIX / "bin" / "testparm"


# --------------------------------------------------------------------------
# Protocol constants parsed from local_protocol.h
# --------------------------------------------------------------------------


def _locate_protocol_header() -> Path:
    configured = os.environ.get("KAIMO_LOCAL_PROTOCOL_H")
    candidates = [Path(configured)] if configured else []
    here = Path(__file__).resolve().parent
    candidates += [
        here / "local_protocol.h",
        here.parent / "module" / "local_protocol.h",
        Path("/usr/local/share/kaimo/local_protocol.h"),
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise FileNotFoundError(
        "local_protocol.h not found; set KAIMO_LOCAL_PROTOCOL_H"
    )


def _parse_protocol(path: Path) -> SimpleNamespace:
    text = path.read_text(encoding="utf-8")
    values: dict[str, int] = {}
    for name, value in re.findall(
        r"^#define\s+KAIMO_LOCAL_([A-Z0-9_]+)\s+(\d+)U?\s*$", text, re.M
    ):
        values[name] = int(value)
    for name, value in re.findall(
        r"^\s*KAIMO_LOCAL_((?:OP|KIND|STATUS)_[A-Z0-9_]+)\s*=\s*(\d+)\s*,?",
        text,
        re.M,
    ):
        values[name] = int(value)
    required = (
        "PROTOCOL_VERSION",
        "OP_CONNECT",
        "OP_SNAPSHOT_RELEASE",
        "KIND_REQUEST",
        "KIND_RESPONSE",
        "STATUS_UNAUTHORIZED_PEER",
    )
    missing = [name for name in required if name not in values]
    if missing:
        raise RuntimeError(f"{path}: cannot parse {missing}")
    return SimpleNamespace(**values)


protocol = _parse_protocol(_locate_protocol_header())
P = protocol

OPERATION_NAMES = {
    getattr(P, name): name[3:]
    for name in vars(P)
    if name.startswith("OP_") and name not in ("OP_MAX",)
}


# --------------------------------------------------------------------------
# Wire helpers
# --------------------------------------------------------------------------


def encode_string(value: str | bytes) -> bytes:
    raw = value.encode("utf-8") if isinstance(value, str) else value
    return struct.pack("!I", len(raw)) + raw


def u8(value: int) -> bytes:
    return struct.pack("!B", value)


def u32(value: int) -> bytes:
    return struct.pack("!I", value)


def u64(value: int) -> bytes:
    return struct.pack("!Q", value)


def frame(
    operation: int,
    status: int,
    payload: bytes = b"",
    *,
    kind: int | None = None,
    version: int | None = None,
    magic: bytes = MAGIC,
    length: int | None = None,
) -> bytes:
    return (
        HEADER.pack(
            magic,
            P.PROTOCOL_VERSION if version is None else version,
            operation,
            P.KIND_RESPONSE if kind is None else kind,
            status,
            len(payload) if length is None else length,
        )
        + payload
    )


def recv_exact(connection: socket.socket, length: int) -> bytes:
    data = bytearray()
    while len(data) < length:
        chunk = connection.recv(length - len(data))
        if not chunk:
            raise EOFError(f"EOF after {len(data)} of {length} bytes")
        data.extend(chunk)
    return bytes(data)


class _Reader:
    def __init__(self, data: bytes) -> None:
        self.data = data
        self.offset = 0

    def u8(self) -> int:
        value = self.data[self.offset]
        self.offset += 1
        return value

    def u32(self) -> int:
        (value,) = struct.unpack_from("!I", self.data, self.offset)
        self.offset += 4
        return value

    def string(self) -> str:
        length = self.u32()
        raw = self.data[self.offset : self.offset + length]
        if len(raw) != length:
            raise ValueError("truncated string")
        self.offset += length
        return raw.decode("utf-8")


# Field layout of every request the VFS module sends (vfs_kaimo_bridge.c).
_REQUEST_LAYOUT: dict[str, tuple[tuple[str, str], ...]] = {
    "CONNECT": (("user", "s"), ("share", "s")),
    "OPEN": (
        ("user", "s"),
        ("share", "s"),
        ("access", "u32"),
        ("create", "u8"),
        ("directory", "u8"),
        ("listing", "u8"),
        ("path", "s"),
    ),
    "DELETE_AUTH": (
        ("user", "s"),
        ("share", "s"),
        ("directory", "u8"),
        ("path", "s"),
    ),
    "RENAME_AUTH": (
        ("user", "s"),
        ("share", "s"),
        ("source_directory", "u8"),
        ("destination_exists", "u8"),
        ("destination_directory", "u8"),
        ("replace", "u8"),
        ("source", "s"),
        ("destination", "s"),
    ),
    "CLOSE": (("user", "s"), ("share", "s"), ("path", "s"), ("capture", "s")),
    "MKDIR": (("user", "s"), ("share", "s"), ("path", "s")),
    "DELETE": (("user", "s"), ("share", "s"), ("directory", "u8"), ("path", "s")),
    "RENAME": (
        ("user", "s"),
        ("share", "s"),
        ("directory", "u8"),
        ("source", "s"),
        ("destination", "s"),
    ),
    "SNAPSHOT_ENUMERATE": (("user", "s"), ("share", "s"), ("path", "s")),
    "SNAPSHOT_RESOLVE": (
        ("user", "s"),
        ("share", "s"),
        ("gmt", "s"),
        ("path", "s"),
    ),
    "SNAPSHOT_RELEASE": (("user", "s"), ("share", "s"), ("lease", "s")),
}


def decode_request(operation: int, payload: bytes) -> dict[str, object]:
    name = OPERATION_NAMES.get(operation)
    layout = _REQUEST_LAYOUT.get(name or "")
    if layout is None:
        return {}
    reader = _Reader(payload)
    fields: dict[str, object] = {}
    for field, kind in layout:
        fields[field] = getattr(reader, "string" if kind == "s" else kind)()
    if reader.offset != len(payload):
        raise ValueError(f"{name}: {len(payload) - reader.offset} trailing bytes")
    return fields


# --------------------------------------------------------------------------
# Fake authd
# --------------------------------------------------------------------------


@dataclasses.dataclass
class Request:
    operation: int
    payload: bytes
    fields: dict[str, object]

    @property
    def name(self) -> str:
        return OPERATION_NAMES.get(self.operation, str(self.operation))

    def __getitem__(self, key: str) -> object:
        return self.fields[key]


@dataclasses.dataclass
class Reply:
    """One scripted answer. ``raw`` replaces the whole frame verbatim."""

    status: int
    payload: bytes = b""
    operation: int | None = None
    kind: int | None = None
    version: int | None = None
    magic: bytes = MAGIC
    length: int | None = None
    raw: bytes | None = None
    stall: bool = False
    close: bool = False
    before: Callable[[Request], None] | None = None

    def encode(self, request: Request) -> bytes:
        if self.raw is not None:
            return self.raw
        return frame(
            request.operation if self.operation is None else self.operation,
            self.status,
            self.payload,
            kind=self.kind,
            version=self.version,
            magic=self.magic,
            length=self.length,
        )


def allow(payload: bytes = b"") -> Reply:
    return Reply(P.STATUS_ALLOW, payload)


def ok(payload: bytes = b"") -> Reply:
    return Reply(P.STATUS_OK, payload)


def deny() -> Reply:
    return Reply(P.STATUS_DENY)


# Samba 4.19.5 FILE_GENERIC_ALL after generic expansion (KAIMO_SAMBA_SPECIFIC_ACCESS).
FULL_ACCESS = 0x001F01FF


def allow_open(mask: int = FULL_ACCESS) -> Reply:
    return allow(u32(mask))


def allow_delete(recycle: bool = False, depth: int = 0) -> Reply:
    return allow(u8(1 if recycle else 0) + u8(depth))


def snapshot_enumeration(labels: Iterable[str]) -> Reply:
    labels = list(labels)
    return ok(u32(len(labels)) + b"".join(encode_string(x) for x in labels))


def snapshot_resolution(cache_path: str, lease: str = "lease", size: int = 0) -> Reply:
    return ok(encode_string(cache_path) + u64(size) + encode_string(lease))


Handler = Callable[[Request], Reply]


GENERIC_MAPPING = {
    0x80000000: 0x00120089,  # GENERIC_READ    -> FILE_GENERIC_READ
    0x40000000: 0x00120116,  # GENERIC_WRITE   -> FILE_GENERIC_WRITE
    0x20000000: 0x001200A0,  # GENERIC_EXECUTE -> FILE_GENERIC_EXECUTE
    0x10000000: FULL_ACCESS,  # GENERIC_ALL
    0x02000000: FULL_ACCESS,  # MAXIMUM_ALLOWED
}


def normalized_access(requested: int) -> int:
    """What a permissive bridge grants: the requested rights, generic bits
    expanded, restricted to Samba's specific access mask."""
    granted = requested
    for generic, specific in GENERIC_MAPPING.items():
        if requested & generic:
            granted |= specific
    return granted & FULL_ACCESS


def default_handler(request: Request) -> Reply:
    """Permissive control plane: every authorization allowed, events acked."""
    name = request.name
    if name == "OPEN":
        return allow_open(normalized_access(int(request.fields.get("access", 0))))
    if name == "DELETE_AUTH":
        return allow_delete(False)
    if name in ("CONNECT", "RENAME_AUTH"):
        return allow()
    if name == "SNAPSHOT_ENUMERATE":
        return snapshot_enumeration([])
    if name == "SNAPSHOT_RESOLVE":
        return Reply(P.STATUS_NOT_FOUND)
    return ok()


class FakeAuthd:
    """Thread-per-connection Unix socket server speaking the KAIM protocol."""

    def __init__(self, socket_path: Path) -> None:
        self.socket_path = Path(socket_path)
        self.requests: list[Request] = []
        self.protocol_errors: list[str] = []
        self._handlers: dict[int, Handler] = {}
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._ready = threading.Event()
        self._thread: threading.Thread | None = None
        self._stalled: list[socket.socket] = []

    # -- scripting ---------------------------------------------------------

    def reset(self) -> None:
        with self._lock:
            self._handlers.clear()
            self.requests.clear()
            self.protocol_errors.clear()

    def on(self, operation: int, reply: Reply | Handler) -> None:
        handler = reply if callable(reply) else (lambda _request, r=reply: r)
        with self._lock:
            self._handlers[operation] = handler

    def on_sequence(self, operation: int, replies: list[Reply], then: Reply | None = None) -> None:
        pending = list(replies)

        def handler(request: Request) -> Reply:
            if pending:
                return pending.pop(0)
            return then if then is not None else default_handler(request)

        self.on(operation, handler)

    def seen(self, operation: int) -> list[Request]:
        with self._lock:
            return [r for r in self.requests if r.operation == operation]

    def names(self) -> list[str]:
        with self._lock:
            return [r.name for r in self.requests]

    # -- lifecycle ---------------------------------------------------------

    def start(self) -> "FakeAuthd":
        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._thread.start()
        if not self._ready.wait(timeout=5):
            raise RuntimeError("fake authd did not start")
        return self

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=5)
        for connection in self._stalled:
            connection.close()
        self._stalled.clear()

    def __enter__(self) -> "FakeAuthd":
        return self.start()

    def __exit__(self, *_exc: object) -> None:
        self.stop()

    def _serve(self) -> None:
        if self.socket_path.exists():
            self.socket_path.unlink()
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
            listener.bind(str(self.socket_path))
            os.chmod(self.socket_path, 0o777)
            listener.listen(64)
            listener.settimeout(0.05)
            self._ready.set()
            while not self._stop.is_set():
                try:
                    connection, _ = listener.accept()
                except (TimeoutError, socket.timeout):
                    continue
                threading.Thread(
                    target=self._handle, args=(connection,), daemon=True
                ).start()

    def _handle(self, connection: socket.socket) -> None:
        keep_open = False
        try:
            connection.settimeout(5)
            magic, version, operation, kind, status, length = HEADER.unpack(
                recv_exact(connection, HEADER.size)
            )
            if (magic, version, kind, status) != (
                MAGIC,
                P.PROTOCOL_VERSION,
                P.KIND_REQUEST,
                P.STATUS_NONE,
            ):
                self.protocol_errors.append(
                    f"bad request header {magic!r} v{version} kind={kind} status={status}"
                )
                return
            payload = recv_exact(connection, length)
            try:
                fields = decode_request(operation, payload)
            except (ValueError, IndexError, struct.error, UnicodeDecodeError) as error:
                self.protocol_errors.append(f"op {operation}: {error}")
                fields = {}
            request = Request(operation, payload, fields)
            with self._lock:
                self.requests.append(request)
                handler = self._handlers.get(operation, default_handler)
            reply = handler(request)
            if reply.before is not None:
                reply.before(request)
            if reply.stall:
                keep_open = True
                self._stalled.append(connection)
                return
            if reply.close:
                return
            connection.sendall(reply.encode(request))
        except (EOFError, OSError) as error:
            self.protocol_errors.append(f"connection error: {error}")
        finally:
            if not keep_open:
                connection.close()


# --------------------------------------------------------------------------
# Samba
# --------------------------------------------------------------------------


@dataclasses.dataclass
class SmbResult:
    returncode: int
    stdout: str
    stderr: str

    @property
    def output(self) -> str:
        return self.stdout + self.stderr

    def status(self) -> str | None:
        match = re.search(r"NT_STATUS_[A-Z_]+", self.output)
        return match.group(0) if match else None


_user_counter = 0


def unique_user(prefix: str = "kt") -> str:
    global _user_counter
    _user_counter += 1
    return f"{prefix}{os.getpid() % 100000}{_user_counter}"


class Samba:
    """A private smbd instance on 127.0.0.1:445 with the kaimo_bridge module."""

    PASSWORD = "Ephemeral-Test-Only-7f3!"

    def __init__(
        self,
        root: Path,
        shares: dict[str, dict[str, str]],
        *,
        env: dict[str, str | None] | None = None,
        global_options: dict[str, str] | None = None,
        user: str | None = None,
    ) -> None:
        self.root = Path(root)
        self.shares = shares
        self.env = dict(env or {})
        self.global_options = dict(global_options or {})
        self.user = user or unique_user()
        self.config = self.root / "smb.conf"
        self.log_path = self.root / "smbd.log"
        self.auth_file = self.root / "smbclient-auth"
        self.process: subprocess.Popen[bytes] | None = None
        self._created_user = False

    # -- setup -------------------------------------------------------------

    def _write_config(self) -> None:
        for name in ("state", "cache", "lock", "pid"):
            (self.root / name).mkdir(mode=0o755, exist_ok=True)
        for name in ("private", "ncalrpc"):
            (self.root / name).mkdir(mode=0o700, exist_ok=True)
        lines = [
            "[global]",
            "server role = standalone server",
            "workgroup = WORKGROUP",
            "map to guest = never",
            "passdb backend = tdbsam",
            "interfaces = 127.0.0.1",
            "bind interfaces only = yes",
            "logging = stdout",
            "log level = 3 vfs:10",
            f"private dir = {self.root / 'private'}",
            f"state directory = {self.root / 'state'}",
            f"cache directory = {self.root / 'cache'}",
            f"lock directory = {self.root / 'lock'}",
            f"pid directory = {self.root / 'pid'}",
            f"ncalrpc dir = {self.root / 'ncalrpc'}",
        ]
        lines += [f"{k} = {v}" for k, v in self.global_options.items()]
        for name, options in self.shares.items():
            lines += ["", f"[{name}]"]
            lines += [f"{k} = {v}" for k, v in options.items()]
        self.config.write_text("\n".join(lines) + "\n", encoding="utf-8")

    def _create_user(self) -> None:
        try:
            pwd.getpwnam(self.user)
        except KeyError:
            subprocess.run(
                ["useradd", "-M", "-s", "/usr/sbin/nologin", self.user], check=True
            )
            self._created_user = True
        subprocess.run(
            [str(SMBPASSWD), "-c", str(self.config), "-s", "-a", self.user],
            input=f"{self.PASSWORD}\n{self.PASSWORD}\n",
            text=True,
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        self.auth_file.write_text(
            f"username = {self.user}\npassword = {self.PASSWORD}\ndomain = WORKGROUP\n",
            encoding="utf-8",
        )
        os.chmod(self.auth_file, 0o600)

    # -- lifecycle ---------------------------------------------------------

    def start(self) -> "Samba":
        self.root.mkdir(mode=0o755, parents=True, exist_ok=True)
        os.chmod(self.root, 0o755)
        self._write_config()
        self._create_user()
        subprocess.run(
            [str(TESTPARM), "--suppress-prompt", str(self.config)],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        environment = os.environ.copy()
        for key, value in self.env.items():
            if value is None:  # None removes an inherited variable
                environment.pop(key, None)
            else:
                environment[key] = value
        log = self.log_path.open("wb")
        self.process = subprocess.Popen(
            [
                str(SMBD),
                "--foreground",
                "--no-process-group",
                "--debug-stdout",
                "--configfile",
                str(self.config),
            ],
            env=environment,
            # Docker RUN steps have a closed stdin; smbd treats EOF as shutdown.
            stdin=subprocess.PIPE,
            stdout=log,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        log.close()
        self._wait_listening()
        return self

    def _wait_listening(self) -> None:
        assert self.process is not None
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError(
                    f"smbd exited with {self.process.returncode}:\n{self.log()}"
                )
            try:
                with socket.create_connection(("127.0.0.1", 445), timeout=0.1):
                    return
            except OSError:
                time.sleep(0.025)
        raise RuntimeError(f"smbd did not listen on port 445:\n{self.log()}")

    def stop(self) -> None:
        process = self.process
        self.process = None
        if process is not None and process.poll() is None:
            # SIGTERM (not SIGKILL) lets smbd and its children run exit
            # handlers, which is what flushes gcov coverage data.
            process.send_signal(signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        if self._created_user:
            subprocess.run(["userdel", self.user], check=False,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            self._created_user = False

    def __enter__(self) -> "Samba":
        return self.start()

    def __exit__(self, *_exc: object) -> None:
        self.stop()

    # -- client ------------------------------------------------------------

    def smbclient(
        self,
        share: str,
        command: str,
        *,
        user: str | None = None,
        password: str | None = None,
        timeout: float = 20,
        protocol_max: str = "SMB3",
    ) -> SmbResult:
        argv = [
            str(SMBCLIENT),
            f"//127.0.0.1/{share}",
            "-p",
            "445",
            "--configfile",
            str(self.config),
            "-m",
            protocol_max,
            "-c",
            command,
        ]
        if user is None and password is None:
            argv += ["--authentication-file", str(self.auth_file)]
        else:
            argv += ["-U", f"{user or self.user}%{password or self.PASSWORD}"]
        result = subprocess.run(
            argv, check=False, text=True, capture_output=True, timeout=timeout
        )
        return SmbResult(result.returncode, result.stdout, result.stderr)

    def log(self) -> str:
        try:
            return self.log_path.read_text(encoding="utf-8", errors="replace")
        except FileNotFoundError:
            return ""

    def diagnostics(self, *results: SmbResult, authd: FakeAuthd | None = None) -> str:
        parts = [f"stdout:\n{r.stdout}\nstderr:\n{r.stderr}" for r in results]
        if authd is not None:
            parts.append(f"authd requests: {authd.names()}")
            parts.append(f"authd protocol errors: {authd.protocol_errors}")
        parts.append(f"smbd log (tail):\n{self.log()[-6000:]}")
        return "\n".join(parts)


def chown_tree(path: Path, user: str) -> None:
    entry = pwd.getpwnam(user)
    for current, directories, files in os.walk(path):
        os.chown(current, entry.pw_uid, entry.pw_gid)
        for name in directories + files:
            os.chown(os.path.join(current, name), entry.pw_uid, entry.pw_gid,
                     follow_symlinks=False)


def reset_directory(path: Path, mode: int = 0o777) -> None:
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(mode=mode, parents=True)
    os.chmod(path, mode)
