# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""TREE_CONNECT authorization: allow, deny, and every fail-closed reply shape."""

from __future__ import annotations

import os
import time

import pytest

import kaimo_testlib as kt
from kaimo_testlib import P, Reply

MALFORMED = "malformed CONNECT authorization response, denied"
UNREACHABLE = "authd unreachable (connect), fail-closed"


def test_allow_records_user_and_share(s):
    result = s.smb("ls")
    assert result.returncode == 0, s.details(result)
    (connect,) = s.authd.seen(P.OP_CONNECT)
    assert connect.fields == {"user": s.samba.user, "share": "data"}
    assert "CONNECT ALLOW share=[data]" in s.new_log()


def test_deny_maps_to_access_denied_promptly(s):
    s.authd.on(P.OP_CONNECT, kt.deny())
    started = time.monotonic()
    result = s.smb("ls")
    duration = time.monotonic() - started
    assert result.returncode != 0
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert duration < 1.5, f"denial took {duration:.3f}s"
    log = s.new_log()
    assert "CONNECT DENIED share=[data]" in log, s.details(result)
    assert MALFORMED not in log and UNREACHABLE not in log
    # A denied tree connect must never reach a later VFS operation.
    assert s.authd.names() == ["CONNECT"]


def test_wrong_password_never_reaches_vfs(s):
    result = s.smb("ls", password="wrong-password")
    assert result.status() == "NT_STATUS_LOGON_FAILURE", s.details(result)
    assert s.authd.requests == []


@pytest.mark.parametrize(
    ("reply", "marker"),
    [
        pytest.param(Reply(P.STATUS_ERROR), UNREACHABLE, id="error"),
        pytest.param(Reply(P.STATUS_OVERLOADED), UNREACHABLE, id="overloaded"),
        pytest.param(
            Reply(P.STATUS_ERROR, operation=P.OP_NONE), UNREACHABLE,
            id="error-op-none",
        ),
        pytest.param(Reply(P.STATUS_ERROR, close=True), UNREACHABLE, id="closed"),
        pytest.param(Reply(P.STATUS_UNAUTHORIZED_PEER), MALFORMED, id="unauthorized-peer"),
        pytest.param(
            Reply(P.STATUS_UNAUTHORIZED_PEER, operation=P.OP_NONE), MALFORMED,
            id="unauthorized-peer-op-none",
        ),
        pytest.param(Reply(P.STATUS_DENY, b"x"), MALFORMED, id="deny-with-payload"),
        pytest.param(Reply(P.STATUS_ALLOW, b"x"), MALFORMED, id="allow-with-payload"),
        pytest.param(Reply(P.STATUS_ERROR, b"x"), MALFORMED, id="error-with-payload"),
        pytest.param(Reply(P.STATUS_OK), MALFORMED, id="unexpected-status"),
        pytest.param(Reply(P.STATUS_ALLOW, operation=P.OP_OPEN), MALFORMED, id="wrong-operation"),
        pytest.param(Reply(P.STATUS_ALLOW, operation=P.OP_NONE), MALFORMED, id="op-none-allow"),
        pytest.param(Reply(P.STATUS_ALLOW, kind=P.KIND_REQUEST), MALFORMED, id="wrong-kind"),
        pytest.param(Reply(P.STATUS_ALLOW, magic=b"XXXX"), MALFORMED, id="bad-magic"),
        pytest.param(
            Reply(P.STATUS_ALLOW, version=P.PROTOCOL_VERSION + 1), MALFORMED,
            id="bad-version",
        ),
        pytest.param(Reply(P.STATUS_NONE), MALFORMED, id="status-none"),
        pytest.param(Reply(99), MALFORMED, id="unknown-status"),
        pytest.param(
            Reply(P.STATUS_ALLOW, length=P.MAX_RESPONSE_PAYLOAD + 1), MALFORMED,
            id="oversized-length",
        ),
        pytest.param(
            Reply(P.STATUS_ALLOW, payload=b"\0" * 8), MALFORMED,
            id="payload-exceeds-buffer",
        ),
        pytest.param(
            Reply(P.STATUS_ALLOW, length=4, payload=b"ab"), MALFORMED,
            id="truncated-payload",
        ),
        pytest.param(Reply(0, raw=b"KAIM"), MALFORMED, id="truncated-header"),
        pytest.param(Reply(P.STATUS_ALLOW, operation=200), MALFORMED, id="unknown-operation"),
    ],
)
def test_bad_replies_fail_closed(s, reply, marker):
    s.authd.on(P.OP_CONNECT, reply)
    result = s.smb("ls")
    assert result.returncode != 0
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert marker in s.new_log(), s.details(result)


def test_missing_socket_fails_closed(s):
    parked = s.authd.socket_path.with_suffix(".parked")
    os.rename(s.authd.socket_path, parked)
    try:
        result = s.smb("ls")
    finally:
        os.rename(parked, s.authd.socket_path)
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert UNREACHABLE in s.new_log()
    assert s.authd.requests == []


def test_non_socket_path_fails_closed(s):
    parked = s.authd.socket_path.with_suffix(".parked")
    os.rename(s.authd.socket_path, parked)
    s.authd.socket_path.write_text("not a socket")
    try:
        result = s.smb("ls")
    finally:
        s.authd.socket_path.unlink()
        os.rename(parked, s.authd.socket_path)
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert UNREACHABLE in s.new_log()


def test_ipc_share_needs_no_authorization(s):
    # Listing shares binds IPC$; the module must not authorize or store context.
    result = s.samba.smbclient("IPC$", "help")
    assert result.returncode == 0, s.details(result)
    assert s.authd.seen(P.OP_CONNECT) == []


def test_module_build_marker_and_deadlines_logged(s):
    s.smb("ls")
    log = s.samba.log()
    assert "kaimo_bridge build [" in log
    assert "local deadlines auth=6000 ms snapshot=32000 ms event=250 ms" in log
    assert "Failed to load module" not in log
