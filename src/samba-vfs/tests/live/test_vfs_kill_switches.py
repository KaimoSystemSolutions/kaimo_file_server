# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""KAIMO_LIST_FILTER=0 and KAIMO_SNAPSHOT_OPENAT=0 kill switches."""

from __future__ import annotations

import kaimo_testlib as kt
from kaimo_testlib import P

SAMBA_ENV = {"KAIMO_LIST_FILTER": "0", "KAIMO_SNAPSHOT_OPENAT": "0"}
GMT = "@GMT-2024.01.02-03.04.05"


def test_disabled_list_filter_shows_every_entry_without_lookups(s):
    for name in ("public.txt", "hidden.txt"):
        (s.share / name).write_text(name)
    s.authd.on(P.OP_OPEN, lambda r: kt.deny() if r["listing"] == 1 else kt.default_handler(r))
    result = s.smb("ls")
    assert result.returncode == 0, s.details(result)
    assert "public.txt" in result.stdout and "hidden.txt" in result.stdout
    assert [r for r in s.authd.seen(P.OP_OPEN) if r["listing"] == 1] == []


def test_disabled_snapshot_redirect_fails_closed(s):
    (s.share / "historical.txt").write_text("LIVE\n")
    local = s.root / "download.tmp"
    result = s.smb(f"get {GMT}/historical.txt {local}")
    assert not local.exists(), s.details(result)
    assert "OPENAT twrp denied because snapshot redirect is disabled" in s.new_log()
