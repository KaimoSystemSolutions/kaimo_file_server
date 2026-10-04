# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""An authd socket path beyond sun_path is rejected, never truncated."""

from __future__ import annotations

# 120 bytes exceed the 108-byte sockaddr_un.sun_path; a truncated path could
# silently address a different socket.
SAMBA_ENV = {"KAIMO_AUTHD_SOCK": "/tmp/" + "s" * 120 + "/authz.sock"}


def test_overlong_socket_path_fails_closed(s):
    result = s.smb("ls")
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert "authd unreachable (connect), fail-closed" in s.new_log()
    assert s.authd.requests == []
