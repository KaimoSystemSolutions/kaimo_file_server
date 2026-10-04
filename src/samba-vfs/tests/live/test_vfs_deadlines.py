# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""Bounded local deadlines: a stalled sidecar can never pin an smbd worker."""

from __future__ import annotations

import os
import time

from kaimo_testlib import P, Reply

AUTH_TIMEOUT_MS = 300
EVENT_TIMEOUT_MS = 100
SAMBA_ENV = {
    "KAIMO_VFS_AUTH_TIMEOUT_MS": str(AUTH_TIMEOUT_MS),
    "KAIMO_VFS_EVENT_TIMEOUT_MS": str(EVENT_TIMEOUT_MS),
    # Invalid values must fall back to the default instead of disabling it.
    "KAIMO_VFS_SNAPSHOT_TIMEOUT_MS": "5",
}


def test_configured_deadlines_are_logged(s):
    s.smb("ls")
    log = s.samba.log()
    assert (
        f"local deadlines auth={AUTH_TIMEOUT_MS} ms snapshot=32000 ms "
        f"event={EVENT_TIMEOUT_MS} ms" in log
    )
    assert "invalid KAIMO_VFS_SNAPSHOT_TIMEOUT_MS=[5], using 32000 ms" in log


def test_stalled_authorization_fails_closed_within_budget(s):
    s.authd.on(P.OP_CONNECT, Reply(P.STATUS_ALLOW, stall=True))
    started = time.monotonic()
    result = s.smb("ls", timeout=5)
    duration = time.monotonic() - started
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert duration >= AUTH_TIMEOUT_MS / 1000 * 0.66, f"{duration:.3f}s"
    assert duration < 1.5, f"deadline took {duration:.3f}s"
    assert "authd unreachable (connect), fail-closed" in s.new_log()


def test_stalled_event_does_not_block_mkdir(s):
    s.authd.on(P.OP_MKDIR, Reply(P.STATUS_OK, stall=True))
    started = time.monotonic()
    result = s.smb("mkdir slow-event", timeout=5)
    duration = time.monotonic() - started
    assert result.returncode == 0, s.details(result)
    assert (s.share / "slow-event").is_dir()
    assert duration < 1.5, f"{duration:.3f}s"
    assert "lifecycle event 6 was not durably accepted" in s.new_log()


def test_stalled_delete_authorization_keeps_file(s):
    target = s.file("keep.txt", "x")
    s.authd.on(P.OP_DELETE_AUTH, Reply(P.STATUS_ALLOW, stall=True))
    result = s.smb("del keep.txt", timeout=5)
    assert target.exists(), s.details(result)
