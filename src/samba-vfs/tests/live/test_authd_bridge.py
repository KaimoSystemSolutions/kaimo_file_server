# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""Real kaimo_authd between a local KAIM client and the fake gRPC bridge.

Covers the request parsers, the mapping of every local operation to its RPC
and back, the decision cache, the durable event spool (delivery, retry, dead
letter) and the snapshot RPCs. The test process plays smbd: it runs as root
and is configured as the trusted peer executable.
"""

from __future__ import annotations

import os
import re
import shutil
import socket
import struct
import subprocess
import time
import uuid
from pathlib import Path

import grpc
import pytest

import kaimo_testlib as kt
from conftest import SIDECARS
from kaimo_testlib import P

VALID_TOKEN = "@GMT-2024.01.02-03.04.05"
CAPTURE_ID = "0123456789abcdef0123456789abcdef"


class Authd:
    def __init__(self, root: Path, bridge, **env: str) -> None:
        self.root = root
        self.socket_path = root / "run" / "authz.sock"
        self.spool = root / "spool"
        self.log_path = root / "authd.log"
        self.bridge = bridge
        self.env = env
        self.process: subprocess.Popen | None = None

    def start(self) -> "Authd":
        run_directory = self.root / "run"
        if not run_directory.exists():
            run_directory.mkdir(mode=0o750, parents=True)
            os.chmod(run_directory, 0o750)
        self.spool.mkdir(mode=0o700, exist_ok=True)
        env = os.environ.copy()
        env.update(self.bridge.pki.client_env(self.bridge.address))
        env.update({
            "KAIMO_AUTHD_SOCK": str(self.socket_path),
            "KAIMO_AUTHD_GROUP": "root",
            "KAIMO_AUTHD_PEER_EXECUTABLE": os.path.realpath("/proc/self/exe"),
            "KAIMO_AUTHD_WORKERS": "4",
            "KAIMO_AUTHD_QUEUE_CAPACITY": "16",
            "KAIMO_AUTHD_IO_TIMEOUT_MS": "2000",
            "KAIMO_AUTHD_CACHE_TTL_MS": "300",
            "KAIMO_EVENT_SPOOL_PATH": str(self.spool),
            "KAIMO_EVENT_MAX_ATTEMPTS": "3",
            "KAIMO_EVENT_RETRY_BASE_MS": "100",
            "KAIMO_EVENT_RETRY_MAX_MS": "1000",
        })
        env.update(self.env)
        log = self.log_path.open("ab")
        self.process = subprocess.Popen(
            [str(SIDECARS / "kaimo_authd")], env=env,
            stdout=subprocess.DEVNULL, stderr=log,
        )
        log.close()
        deadline = time.monotonic() + 10
        while not self.socket_path.exists():
            if self.process.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError(f"authd did not start:\n{self.log()}")
            time.sleep(0.02)
        return self

    def stop(self) -> None:
        if self.process is not None and self.process.poll() is None:
            # SIGTERM lets the gcov exit handlers flush coverage data.
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        self.process = None

    def log(self) -> str:
        return self.log_path.read_text(errors="replace") if self.log_path.exists() else ""

    def call(self, operation: int, payload: bytes) -> tuple[int, int, bytes]:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(10)
            client.connect(str(self.socket_path))
            client.sendall(kt.frame(operation, P.STATUS_NONE, payload, kind=P.KIND_REQUEST))
            magic, version, op, kind, status, length = kt.HEADER.unpack(
                kt.recv_exact(client, kt.HEADER.size))
            assert (magic, version, kind) == (kt.MAGIC, P.PROTOCOL_VERSION, P.KIND_RESPONSE)
            body = kt.recv_exact(client, length) if length else b""
            return op, status, body


@pytest.fixture(scope="module")
def authd(bridge_session):
    root = Path(f"/tmp/kaimo-authd-bridge-{os.getpid()}")
    if root.exists():
        shutil.rmtree(root)
    root.mkdir(mode=0o755)
    daemon = Authd(root, bridge_session).start()
    try:
        yield daemon
    finally:
        daemon.stop()
        shutil.rmtree(root, ignore_errors=True)


@pytest.fixture
def a(authd, bridge):
    return authd


def ident(user: str = "alice", share: str = "data") -> bytes:
    return kt.encode_string(user) + kt.encode_string(share)


def unique_path() -> str:
    return f"dir/{uuid.uuid4().hex}.txt"


def wait_for(predicate, timeout: float = 5.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.05)
    raise AssertionError("condition not reached in time")


def abort(code=grpc.StatusCode.UNAVAILABLE):
    def handler(_request, context):
        context.abort(code, "scripted failure")

    return handler


# ---------------------------------------------------------------- authorization


@pytest.mark.parametrize(("allow", "expected"), [(True, P.STATUS_ALLOW), (False, P.STATUS_DENY)])
def test_connect_maps_bridge_decision(a, bridge, allow, expected):
    bridge.on("AuthorizeConnect", bridge.pb.AuthorizeReply(allow=allow))
    op, status, body = a.call(P.OP_CONNECT, ident("alice", "projects"))
    assert (op, status, body) == (P.OP_CONNECT, expected, b"")
    (request,) = bridge.requests("AuthorizeConnect")
    assert (request.username, request.share) == ("alice", "projects")


def test_rpc_failure_becomes_error_status(a, bridge):
    bridge.on("AuthorizeConnect", abort())
    assert a.call(P.OP_CONNECT, ident())[1] == P.STATUS_ERROR
    assert "AuthorizeConnect:" in a.log()


def test_open_forwards_every_field_and_returns_mask(a, bridge):
    bridge.on("AuthorizeOpen", bridge.pb.AuthorizeReply(allow=True, granted_access_mask=0x00120089))
    path = unique_path()
    payload = ident() + kt.u32(0x00130197) + kt.u8(1) + kt.u8(0) + kt.u8(1) + kt.encode_string(path)
    assert a.call(P.OP_OPEN, payload) == (P.OP_OPEN, P.STATUS_ALLOW, kt.u32(0x00120089))
    (request,) = bridge.requests("AuthorizeOpen")
    assert (request.path, request.access_mask) == (path, 0x00130197)
    assert (request.wants_create, request.create_directory, request.directory_listing) == (
        True, False, True)


def test_open_decisions_are_cached_until_ttl(a, bridge):
    bridge.on("AuthorizeOpen", bridge.pb.AuthorizeReply(allow=False))
    payload = ident() + kt.u32(1) + kt.u8(0) * 3 + kt.encode_string(unique_path())
    assert a.call(P.OP_OPEN, payload)[1] == P.STATUS_DENY
    assert a.call(P.OP_OPEN, payload)[1] == P.STATUS_DENY
    assert len(bridge.requests("AuthorizeOpen")) == 1
    time.sleep(0.45)
    assert a.call(P.OP_OPEN, payload)[1] == P.STATUS_DENY
    assert len(bridge.requests("AuthorizeOpen")) == 2


def test_open_failures_are_not_cached(a, bridge):
    bridge.on_sequence("AuthorizeOpen", [abort(), bridge.pb.AuthorizeReply(allow=True)])
    payload = ident() + kt.u32(1) + kt.u8(0) * 3 + kt.encode_string(unique_path())
    assert a.call(P.OP_OPEN, payload)[1] == P.STATUS_ERROR
    assert a.call(P.OP_OPEN, payload)[1] == P.STATUS_ALLOW
    assert len(bridge.requests("AuthorizeOpen")) == 2


@pytest.mark.parametrize(
    ("reply", "expected"),
    [
        ({"allow": True, "recycle_delete": True, "recycle_root_depth": 1}, (P.STATUS_ALLOW, b"\x01\x01")),
        ({"allow": True}, (P.STATUS_ALLOW, b"\x00\x00")),
        ({"allow": False, "recycle_delete": True}, (P.STATUS_DENY, b"")),
        ({"allow": True, "recycle_delete": True, "recycle_root_depth": 300}, (P.STATUS_ALLOW, b"\x01\xff")),
    ],
)
def test_delete_authorization_disposition(a, bridge, reply, expected):
    bridge.on("AuthorizeDelete", bridge.pb.AuthorizeReply(**reply))
    op, status, body = a.call(P.OP_DELETE_AUTH, ident() + kt.u8(1) + kt.encode_string("old"))
    assert (status, body) == expected
    (request,) = bridge.requests("AuthorizeDelete")
    assert (request.path, request.is_directory) == ("old", True)


def test_delete_authorization_failure(a, bridge):
    bridge.on("AuthorizeDelete", abort())
    assert a.call(P.OP_DELETE_AUTH, ident() + kt.u8(0) + kt.encode_string("x"))[1] == P.STATUS_ERROR


@pytest.mark.parametrize("allow", [True, False])
def test_rename_authorization(a, bridge, allow):
    bridge.on("AuthorizeRename", bridge.pb.AuthorizeReply(allow=allow))
    payload = (ident() + kt.u8(1) + kt.u8(1) + kt.u8(0) + kt.u8(1)
               + kt.encode_string("a") + kt.encode_string("b"))
    status = a.call(P.OP_RENAME_AUTH, payload)[1]
    assert status == (P.STATUS_ALLOW if allow else P.STATUS_DENY)
    (request,) = bridge.requests("AuthorizeRename")
    assert (request.source_path, request.destination_path) == ("a", "b")
    assert (request.source_is_directory, request.destination_exists,
            request.destination_is_directory, request.replace_intent) == (True, True, False, True)


def test_rename_authorization_failure(a, bridge):
    bridge.on("AuthorizeRename", abort())
    payload = ident() + kt.u8(0) * 4 + kt.encode_string("a") + kt.encode_string("b")
    assert a.call(P.OP_RENAME_AUTH, payload)[1] == P.STATUS_ERROR


@pytest.mark.parametrize(
    ("operation", "payload"),
    [
        pytest.param(P.OP_CONNECT, ident() + b"x", id="connect-trailing"),
        pytest.param(P.OP_OPEN, ident() + kt.u32(1), id="open-truncated"),
        pytest.param(P.OP_OPEN, ident() + kt.u32(1) + b"\x02\x00\x00" + kt.encode_string("p"),
                     id="open-bad-boolean"),
        pytest.param(P.OP_DELETE_AUTH, ident() + kt.u8(0), id="delete-truncated"),
        pytest.param(P.OP_RENAME_AUTH, ident() + kt.u8(0) * 4 + kt.encode_string("a"),
                     id="rename-truncated"),
        pytest.param(P.OP_CLOSE, ident() + kt.encode_string("p") + kt.encode_string("XYZ"),
                     id="close-bad-capture"),
        pytest.param(P.OP_CLOSE, ident() + kt.encode_string("p") + kt.encode_string(CAPTURE_ID.upper()),
                     id="close-uppercase-capture"),
        pytest.param(P.OP_MKDIR, ident() + kt.encode_string("p") + b"x", id="mkdir-trailing"),
        pytest.param(P.OP_DELETE, ident() + kt.u8(3) + kt.encode_string("p"), id="delete-event-bool"),
        pytest.param(P.OP_RENAME, ident() + kt.u8(0) + kt.encode_string("a"), id="rename-event-truncated"),
        pytest.param(P.OP_SNAPSHOT_ENUMERATE, ident(), id="snapenum-truncated"),
        pytest.param(P.OP_SNAPSHOT_RESOLVE, ident() + kt.encode_string(VALID_TOKEN), id="resolve-truncated"),
        pytest.param(P.OP_SNAPSHOT_RELEASE, ident() + kt.encode_string("l") + b"x", id="release-trailing"),
        pytest.param(P.OP_CONNECT, ident("bad user", "data"), id="invalid-username"),
        pytest.param(P.OP_CONNECT, ident("alice", "bad/share"), id="invalid-share"),
        pytest.param(P.OP_CONNECT, b"\x00\x00", id="no-identity"),
    ],
)
def test_malformed_requests_get_error_without_rpc(a, bridge, operation, payload):
    assert a.call(operation, payload)[1] == P.STATUS_ERROR
    assert bridge.calls == []


# ---------------------------------------------------------------- events


@pytest.mark.parametrize(
    ("operation", "payload", "method", "fields"),
    [
        (P.OP_MKDIR, ident() + kt.encode_string("new"), "NotifyMkdir",
         {"path": "new", "is_directory": True}),
        (P.OP_DELETE, ident() + kt.u8(1) + kt.encode_string("gone"), "NotifyDelete",
         {"path": "gone", "is_directory": True}),
        (P.OP_RENAME, ident() + kt.u8(0) + kt.encode_string("a") + kt.encode_string("b"),
         "NotifyRename", {"old_path": "a", "new_path": "b", "is_directory": False}),
        (P.OP_CLOSE, ident() + kt.encode_string("doc.txt") + kt.encode_string(CAPTURE_ID),
         "NotifyClose", {"path": "doc.txt", "capture_id": CAPTURE_ID}),
    ],
)
def test_events_are_spooled_and_delivered(a, bridge, operation, payload, method, fields):
    assert a.call(operation, payload) == (operation, P.STATUS_OK, b"")
    (request,) = wait_for(lambda: bridge.requests(method))
    assert (request.username, request.share) == ("alice", "data")
    assert re.fullmatch(r"[0-9a-f]{32}", request.event_id)
    for name, value in fields.items():
        assert getattr(request, name) == value


def test_failed_delivery_is_retried_with_same_event_id(a, bridge):
    bridge.on_sequence("NotifyMkdir", [
        bridge.pb.NotifyReply(ok=False), abort(), bridge.pb.NotifyReply(ok=True)])
    assert a.call(P.OP_MKDIR, ident() + kt.encode_string("retry"))[1] == P.STATUS_OK
    requests = wait_for(lambda: len(bridge.requests("NotifyMkdir")) >= 3 and bridge.requests("NotifyMkdir"))
    assert len({r.event_id for r in requests}) == 1


def test_undeliverable_event_is_dead_lettered(a, bridge):
    bridge.on("NotifyDelete", bridge.pb.NotifyReply(ok=False))
    assert a.call(P.OP_DELETE, ident() + kt.u8(0) + kt.encode_string("never"))[1] == P.STATUS_OK
    wait_for(lambda: "lifecycle event dead-lettered" in a.log(), timeout=10)
    assert len(bridge.requests("NotifyDelete")) == 3


# ---------------------------------------------------------------- snapshots


def test_snapshot_enumeration(a, bridge):
    tokens = [VALID_TOKEN, "@GMT-2023.12.31-23.59.59"]
    bridge.on("EnumerateSnapshots", bridge.pb.EnumerateSnapshotsReply(gmt_tokens=tokens))
    op, status, body = a.call(P.OP_SNAPSHOT_ENUMERATE, ident() + kt.encode_string("doc.txt"))
    assert status == P.STATUS_OK
    assert body == kt.u32(2) + b"".join(kt.encode_string(t) for t in tokens)
    assert bridge.requests("EnumerateSnapshots")[0].path == "doc.txt"


@pytest.mark.parametrize(
    "reply",
    [
        pytest.param(lambda pb: pb.EnumerateSnapshotsReply(gmt_tokens=["@GMT-bad"]), id="bad-token"),
        pytest.param(lambda pb: pb.EnumerateSnapshotsReply(gmt_tokens=[VALID_TOKEN] * 2049),
                     id="too-many"),
        pytest.param(lambda pb: abort(), id="rpc-failure"),
    ],
)
def test_snapshot_enumeration_failures(a, bridge, reply):
    bridge.on("EnumerateSnapshots", reply(bridge.pb))
    op, status, body = a.call(P.OP_SNAPSHOT_ENUMERATE, ident() + kt.encode_string("x"))
    assert (status, body) == (P.STATUS_ERROR, b"")


def test_snapshot_resolution(a, bridge):
    bridge.on("ResolveVersion", bridge.pb.ResolveVersionReply(
        found=True, cache_path="s/t/u/doc.txt", size=7, lease_id="lease-1"))
    op, status, body = a.call(P.OP_SNAPSHOT_RESOLVE,
                              ident() + kt.encode_string(VALID_TOKEN) + kt.encode_string("doc.txt"))
    assert status == P.STATUS_OK
    assert body == kt.encode_string("s/t/u/doc.txt") + kt.u64(7) + kt.encode_string("lease-1")
    (request,) = bridge.requests("ResolveVersion")
    assert (request.gmt_token, request.path) == (VALID_TOKEN, "doc.txt")


@pytest.mark.parametrize(
    ("reply", "expected"),
    [
        pytest.param(lambda pb: pb.ResolveVersionReply(found=False), P.STATUS_NOT_FOUND, id="missing"),
        pytest.param(lambda pb: pb.ResolveVersionReply(found=True, cache_path="x", size=-1,
                                                       lease_id="l"), P.STATUS_ERROR, id="negative-size"),
        pytest.param(lambda pb: pb.ResolveVersionReply(found=True, cache_path="x"),
                     P.STATUS_ERROR, id="no-lease"),
        pytest.param(lambda pb: pb.ResolveVersionReply(found=True, cache_path="x" * 9000,
                                                       lease_id="l"), P.STATUS_ERROR, id="path-too-long"),
        pytest.param(lambda pb: abort(), P.STATUS_ERROR, id="rpc-failure"),
    ],
)
def test_snapshot_resolution_failures(a, bridge, reply, expected):
    bridge.on("ResolveVersion", reply(bridge.pb))
    op, status, body = a.call(P.OP_SNAPSHOT_RESOLVE,
                              ident() + kt.encode_string(VALID_TOKEN) + kt.encode_string("x"))
    assert (status, body) == (expected, b"")


def test_snapshot_lease_release(a, bridge):
    assert a.call(P.OP_SNAPSHOT_RELEASE, ident() + kt.encode_string("lease-9"))[1] == P.STATUS_OK
    assert bridge.requests("ReleaseVersionLease")[0].lease_id == "lease-9"
    bridge.on("ReleaseVersionLease", abort())
    assert a.call(P.OP_SNAPSHOT_RELEASE, ident() + kt.encode_string("lease-9"))[1] == P.STATUS_ERROR


# ---------------------------------------------------------------- startup


@pytest.mark.parametrize(
    "env",
    [
        {"KAIMO_AUTHD_WORKERS": "0"},
        {"KAIMO_AUTHD_QUEUE_CAPACITY": "lots"},
        {"KAIMO_EVENT_RETRY_BASE_MS": "5000", "KAIMO_EVENT_RETRY_MAX_MS": "1000"},
        {"KAIMO_AUTHD_GROUP": "no-such-group-kaimo"},
        {"KAIMO_AUTHD_PEER_EXECUTABLE": "/nonexistent/smbd"},
        {"KAIMO_BRIDGE_RUNTIME_CERT": "/nonexistent.crt"},
    ],
)
def test_invalid_configuration_refuses_to_start(bridge, tmp_path, env):
    daemon = Authd(tmp_path, bridge, **env)
    with pytest.raises(RuntimeError):
        daemon.start()
    daemon.stop()
    assert daemon.process is None


@pytest.mark.parametrize("mode", [0o777, 0o755, 0o770])
def test_insecure_socket_parent_refuses_to_start(bridge, tmp_path, mode):
    daemon = Authd(tmp_path, bridge)
    (tmp_path / "run").mkdir()
    os.chmod(tmp_path / "run", mode)  # start() keeps an existing directory as is
    with pytest.raises(RuntimeError):
        daemon.start()
    daemon.stop()


def test_socket_parent_symlink_refuses_to_start(bridge, tmp_path):
    real = tmp_path / "real"
    real.mkdir(mode=0o750)
    os.chmod(real, 0o750)
    os.symlink(real, tmp_path / "run")
    daemon = Authd(tmp_path, bridge)
    try:
        daemon.start()
    except RuntimeError:
        pass  # a symlinked parent may be rejected outright ...
    else:
        # ... or canonicalized to the real directory, never followed blindly.
        assert (real / "authz.sock").exists()
    daemon.stop()


def test_stale_socket_is_replaced_but_foreign_file_is_refused(bridge, tmp_path):
    daemon = Authd(tmp_path, bridge).start()
    daemon.process.kill()  # leaves a stale socket owned by us
    daemon.process.wait()
    daemon.process = None
    daemon.start()  # replaces the stale socket
    daemon.stop()
    daemon.socket_path.unlink(missing_ok=True)
    daemon.socket_path.write_text("not a socket")
    daemon.start()  # returns at once: the path already exists
    assert daemon.process.wait(timeout=10) == 1
    daemon.process = None
    assert "refusing unsafe stale socket path" in daemon.log()
    assert daemon.socket_path.read_text() == "not a socket"
