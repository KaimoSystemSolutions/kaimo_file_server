# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""kaimo_authsync / kaimo_sharesync / kaimo_configsync against a fake bridge.

The shell reconciler tests stub these binaries; this suite proves the real
producers: mTLS, pagination validation, rate-limit retry and the JSON
documents the reconcilers consume.
"""

from __future__ import annotations

import json
import os
import subprocess
from dataclasses import dataclass

import grpc
import pytest

from conftest import SIDECARS

HASH_A = bytes(range(16))
HASH_B = bytes(range(16, 32))


@dataclass
class Run:
    returncode: int
    stdout: str
    stderr: str

    def json(self):
        return json.loads(self.stdout)


def run_sidecar(name: str, bridge, **env_overrides: str) -> Run:
    env = os.environ.copy()
    env.update(bridge.pki.client_env(bridge.address))
    env.update(env_overrides)
    result = subprocess.run(
        [str(SIDECARS / f"kaimo_{name}")], env=env, text=True,
        capture_output=True, timeout=60,
    )
    return Run(result.returncode, result.stdout, result.stderr)


def users_page(pb, users, next_offset, has_more=False, token="", rejected=0):
    return pb.ListUsersReply(
        users=[pb.UserEntry(username=name, nt_hash=digest) for name, digest in users],
        next_offset=next_offset, has_more=has_more, continuation_token=token,
        rejected_users=rejected,
    )


def abort(code: grpc.StatusCode, retry_after: str | None = None):
    def handler(_request, context):
        if retry_after is not None:
            context.set_trailing_metadata((("retry-after-ms", retry_after),))
        context.abort(code, "scripted failure")

    return handler


# ---------------------------------------------------------------- authsync


def test_authsync_exports_users_as_json(bridge):
    pb = bridge.pb
    bridge.on("ListUsers", users_page(pb, [("alice", HASH_A), ("bob", HASH_B)], 5, rejected=3))
    run = run_sidecar("authsync", bridge)
    assert run.returncode == 0, run.stderr
    assert run.json() == {
        "version": 1,
        "users": [
            {"username": "alice", "nt_hash": HASH_A.hex().upper()},
            {"username": "bob", "nt_hash": HASH_B.hex().upper()},
        ],
    }
    (request,) = bridge.requests("ListUsers")
    assert (request.offset, request.page_size, request.continuation_token) == (0, 1000, "")
    assert "3 invalid credential rows skipped" in run.stderr


def test_authsync_follows_continuation_pages(bridge):
    pb = bridge.pb
    bridge.on_sequence("ListUsers", [
        users_page(pb, [("alice", HASH_A)], 1, has_more=True, token="page-2"),
        users_page(pb, [("bob", HASH_B)], 2),
    ])
    run = run_sidecar("authsync", bridge)
    assert run.returncode == 0, run.stderr
    assert [u["username"] for u in run.json()["users"]] == ["alice", "bob"]
    requests = bridge.requests("ListUsers")
    assert [(r.offset, r.continuation_token) for r in requests] == [(0, ""), (1, "page-2")]


def test_authsync_empty_directory(bridge):
    run = run_sidecar("authsync", bridge)
    assert run.returncode == 0, run.stderr
    assert run.json() == {"version": 1, "users": []}


def _pagination_cases(pb):
    many = [(f"user{i}", HASH_A) for i in range(1001)]
    return {
        "page-too-large": [users_page(pb, many, 1001)],
        "offset-moves-backwards": [
            users_page(pb, [], 5, has_more=True, token="t", rejected=5),
            users_page(pb, [], 3),
        ],
        "no-progress": [users_page(pb, [], 0, has_more=True, token="t")],
        "offset-above-maximum": [users_page(pb, [], 100001)],
        "rejected-above-maximum": [users_page(pb, [], 100000, rejected=100001)],
        "rejected-sum-above-maximum": [
            users_page(pb, [], 60000, has_more=True, token="t", rejected=60000),
            users_page(pb, [], 100000, rejected=40001),
        ],
        "more-rows-than-offset-advance": [users_page(pb, [("a", HASH_A), ("b", HASH_B)], 1)],
        "more-without-token": [users_page(pb, [("a", HASH_A)], 1, has_more=True)],
        "token-on-last-page": [users_page(pb, [("a", HASH_A)], 1, token="dangling")],
    }


@pytest.mark.parametrize("case", [
    "page-too-large", "offset-moves-backwards", "no-progress", "offset-above-maximum",
    "rejected-above-maximum", "rejected-sum-above-maximum",
    "more-rows-than-offset-advance", "more-without-token", "token-on-last-page",
])
def test_authsync_rejects_invalid_pagination(bridge, case):
    bridge.on_sequence("ListUsers", _pagination_cases(bridge.pb)[case])
    run = run_sidecar("authsync", bridge)
    assert run.returncode == 1
    assert "invalid pagination metadata rejected" in run.stderr
    assert run.stdout == ""


@pytest.mark.parametrize(
    ("name", "digest"),
    [("bad name", HASH_A), ("", HASH_A), ("x" * 40, HASH_A),
     ("alice", HASH_A[:15]), ("alice", HASH_A + b"\0")],
)
def test_authsync_rejects_invalid_user_records(bridge, name, digest):
    bridge.on("ListUsers", users_page(bridge.pb, [(name, digest)], 1))
    run = run_sidecar("authsync", bridge)
    assert run.returncode == 1
    assert "invalid user record rejected" in run.stderr
    assert run.stdout == ""


def test_authsync_retries_once_after_rate_limit(bridge):
    bridge.on_sequence("ListUsers", [
        abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "50"),
        users_page(bridge.pb, [("alice", HASH_A)], 1),
    ])
    run = run_sidecar("authsync", bridge)
    assert run.returncode == 0, run.stderr
    assert "retrying after 300 ms" in run.stderr
    assert len(bridge.requests("ListUsers")) == 2


@pytest.mark.parametrize(
    "first",
    [
        pytest.param(abort(grpc.StatusCode.RESOURCE_EXHAUSTED), id="no-retry-hint"),
        pytest.param(abort(grpc.StatusCode.RESOURCE_EXHAUSTED, ""), id="empty-hint"),
        pytest.param(abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "soon"), id="garbage-hint"),
        pytest.param(abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "300001"), id="hint-too-long"),
        pytest.param(abort(grpc.StatusCode.UNAVAILABLE, "50"), id="other-status"),
    ],
)
def test_authsync_fails_without_valid_retry(bridge, first):
    bridge.on("ListUsers", first)
    run = run_sidecar("authsync", bridge)
    assert run.returncode == 1
    assert "ListUsers RPC failed" in run.stderr
    assert len(bridge.requests("ListUsers")) == 1


def test_authsync_retries_rate_limit_only_once(bridge):
    bridge.on("ListUsers", abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "10"))
    run = run_sidecar("authsync", bridge)
    assert run.returncode == 1
    assert len(bridge.requests("ListUsers")) == 2


def test_authsync_rate_limit_on_later_page_fails(bridge):
    bridge.on_sequence("ListUsers", [
        users_page(bridge.pb, [("alice", HASH_A)], 1, has_more=True, token="t"),
        abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "10"),
    ])
    run = run_sidecar("authsync", bridge)
    assert run.returncode == 1
    assert len(bridge.requests("ListUsers")) == 2


def test_missing_credentials_fail_before_any_rpc(bridge, tmp_path):
    run = run_sidecar("authsync", bridge,
                      KAIMO_BRIDGE_AUTH_SYNC_CERT=str(tmp_path / "missing.crt"))
    assert run.returncode == 1
    assert "cannot open control-plane credential" in run.stderr
    empty = tmp_path / "empty.key"
    empty.write_text("")
    run = run_sidecar("sharesync", bridge, KAIMO_BRIDGE_SHARE_SYNC_KEY=str(empty))
    assert run.returncode == 1
    assert "empty control-plane credential" in run.stderr
    run = run_sidecar("configsync", bridge, KAIMO_BRIDGE_CA_CERT=str(tmp_path / "none"))
    assert run.returncode == 1
    assert bridge.calls == []


def test_untrusted_bridge_identity_is_rejected(bridge):
    # The client CA does not issue a certificate for this name.
    run = run_sidecar("configsync", bridge, KAIMO_BRIDGE_ADDR=f"127.0.0.2:{bridge.port}")
    assert run.returncode == 1
    assert "GetProtocolSettings RPC failed" in run.stderr


# ---------------------------------------------------------------- sharesync


def test_sharesync_exports_shares(bridge):
    pb = bridge.pb
    bridge.on("ListShares", pb.ListSharesReply(shares=[
        pb.ShareEntry(name="data", path="/srv/data"),
        pb.ShareEntry(name="team", path='/srv/te"am\\x', is_hidden=True,
                      restricted=True, allowed_users=["alice", "bob"]),
        pb.ShareEntry(name="locked", path="/srv/locked", restricted=True),
        pb.ShareEntry(name="open", path="/srv/open", allowed_users=["ignored"]),
    ]))
    run = run_sidecar("sharesync", bridge)
    assert run.returncode == 0, run.stderr
    assert run.json() == {
        "version": 1,
        "shares": [
            {"name": "data", "path": "/srv/data", "hidden": False},
            {"name": "team", "path": '/srv/te"am\\x', "hidden": True,
             "allowed_users": ["alice", "bob"]},
            {"name": "locked", "path": "/srv/locked", "hidden": False, "allowed_users": []},
            {"name": "open", "path": "/srv/open", "hidden": False},
        ],
    }
    assert "4 shares received" in run.stderr


@pytest.mark.parametrize(
    ("share", "message"),
    [
        ({"name": "bad/name", "path": "/srv/x"}, "invalid share record rejected"),
        ({"name": "global", "path": "/srv/x"}, "invalid share record rejected"),
        ({"name": "IPC$", "path": "/srv/x"}, "invalid share record rejected"),
        ({"name": ".hidden", "path": "/srv/x"}, "invalid share record rejected"),
        ({"name": "x" * 65, "path": "/srv/x"}, "invalid share record rejected"),
        ({"name": "rel", "path": "srv/x"}, "invalid share record rejected"),
        ({"name": "empty", "path": ""}, "invalid share record rejected"),
        ({"name": "ok", "path": "/srv/x", "restricted": True,
          "allowed_users": ["alice", "bad user"]}, "invalid allowed user rejected"),
    ],
)
def test_sharesync_rejects_invalid_records(bridge, share, message):
    pb = bridge.pb
    bridge.on("ListShares", pb.ListSharesReply(shares=[pb.ShareEntry(**share)]))
    run = run_sidecar("sharesync", bridge)
    assert run.returncode == 1
    assert message in run.stderr
    assert run.stdout == ""


def test_sharesync_rpc_failure(bridge):
    bridge.on("ListShares", abort(grpc.StatusCode.PERMISSION_DENIED))
    run = run_sidecar("sharesync", bridge)
    assert run.returncode == 1
    assert "ListShares RPC failed" in run.stderr


# ---------------------------------------------------------------- configsync


def test_configsync_exports_settings(bridge):
    bridge.on("GetProtocolSettings", bridge.pb.ProtocolSettingsReply(
        min_protocol="SMB3_00", max_protocol="SMB3_11", require_signing=True,
        require_encryption=True, enabled=False, enable_ws_discovery=True,
        enable_audit_log=True, log_level="Debug",
    ))
    run = run_sidecar("configsync", bridge)
    assert run.returncode == 0, run.stderr
    assert run.json() == {"version": 1, "config": {
        "min_protocol": "SMB3_00", "max_protocol": "SMB3_11",
        "require_signing": True, "require_encryption": True, "enabled": False,
        "enable_ws_discovery": True, "enable_audit_log": True, "log_level": "Debug",
    }}


@pytest.mark.parametrize("dialects", [
    ("SMB3_11", "SMB2_02"), ("NT1", "SMB3_11"), ("SMB2_02", "SMB4"), ("", ""),
])
def test_configsync_rejects_invalid_protocol_range(bridge, dialects):
    bridge.on("GetProtocolSettings", bridge.pb.ProtocolSettingsReply(
        min_protocol=dialects[0], max_protocol=dialects[1]))
    run = run_sidecar("configsync", bridge)
    assert run.returncode == 1
    assert "invalid protocol range" in run.stderr


@pytest.mark.parametrize("dialect", ["SMB2_02", "SMB2_10", "SMB3_00", "SMB3_02", "SMB3_11"])
def test_configsync_accepts_every_dialect(bridge, dialect):
    bridge.on("GetProtocolSettings", bridge.pb.ProtocolSettingsReply(
        min_protocol=dialect, max_protocol=dialect))
    run = run_sidecar("configsync", bridge)
    assert run.returncode == 0, run.stderr
    assert run.json()["config"]["min_protocol"] == dialect


def test_configsync_rpc_failure(bridge):
    bridge.on("GetProtocolSettings", abort(grpc.StatusCode.INTERNAL))
    run = run_sidecar("configsync", bridge)
    assert run.returncode == 1
    assert "GetProtocolSettings RPC failed" in run.stderr
