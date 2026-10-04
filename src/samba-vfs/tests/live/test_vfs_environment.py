# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""Default authd socket and identity validation before any authorization."""

from __future__ import annotations

from pathlib import Path

from kaimo_testlib import P

# No KAIMO_AUTHD_SOCK: the module must fall back to its compiled-in default
# (/var/run/kaimo/authz.sock), which does not exist in the test container.
SAMBA_ENV = {"KAIMO_AUTHD_SOCK": None}
# '$' is not a valid Kaimo share-name character (hidden admin-share style).
EXTRA_SHARES = ["team$"]


def test_default_socket_path_is_used_and_fails_closed(s):
    assert not Path("/var/run/kaimo/authz.sock").exists()
    result = s.smb("ls")
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert "authd unreachable (connect), fail-closed" in s.new_log()
    assert s.authd.requests == []


def test_invalid_share_name_is_denied_before_authorization(s):
    result = s.samba.smbclient("team$", "ls")
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert "CONNECT invalid user/share context denied" in s.new_log()
    assert s.authd.seen(P.OP_CONNECT) == []
