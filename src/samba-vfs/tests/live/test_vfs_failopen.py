# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""KAIMO_AUTHZ_FAILOPEN=1: infrastructure errors allow, except where unsafe."""

from __future__ import annotations

import os

import kaimo_testlib as kt
from kaimo_testlib import P, Reply

SAMBA_ENV = {"KAIMO_AUTHZ_FAILOPEN": "1"}


def test_connect_infrastructure_error_fails_open(s):
    s.authd.on(P.OP_CONNECT, Reply(P.STATUS_ERROR))
    result = s.smb("ls")
    assert result.returncode == 0, s.details(result)
    assert "authd unreachable (connect), fail-open" in s.new_log()


def test_connect_without_sidecar_fails_open(s):
    parked = s.authd.socket_path.with_suffix(".parked")
    os.rename(s.authd.socket_path, parked)
    try:
        result = s.smb("ls")
    finally:
        os.rename(parked, s.authd.socket_path)
    assert result.returncode == 0, s.details(result)


def test_malformed_reply_still_fails_closed(s):
    s.authd.on(P.OP_CONNECT, Reply(P.STATUS_ALLOW, b"x"))
    result = s.smb("ls")
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)


def test_explicit_deny_is_never_overridden(s):
    s.authd.on(P.OP_CONNECT, kt.deny())
    result = s.smb("ls")
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)


def test_open_error_keeps_requested_access(s):
    (s.share / "file.txt").write_text("content\n")

    def handler(request):
        if request.fields.get("path") == "file.txt":
            return Reply(P.STATUS_OVERLOADED)
        return kt.default_handler(request)

    s.authd.on(P.OP_OPEN, handler)
    local = s.root / "download.tmp"
    result = s.smb(f"get file.txt {local}")
    assert result.returncode == 0, s.details(result)
    assert local.read_text() == "content\n"


def test_rename_error_fails_open(s):
    s.file("a.txt", "x")
    s.authd.on(P.OP_RENAME_AUTH, Reply(P.STATUS_ERROR))
    result = s.smb("rename a.txt b.txt")
    assert result.returncode == 0, s.details(result)
    assert (s.share / "b.txt").exists()
    assert "authd unreachable (rename), fail-open" in s.new_log()


def test_delete_error_fails_closed_despite_failopen(s):
    target = s.file("keep.txt", "x")
    s.authd.on(P.OP_DELETE_AUTH, Reply(P.STATUS_ERROR))
    result = s.smb("del keep.txt")
    assert target.exists(), s.details(result)
    assert "fail-closed because recycle disposition is unknown" in s.new_log()
