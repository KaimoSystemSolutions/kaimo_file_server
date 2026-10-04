# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""Runs the self-contained authd / VFS runtime scripts as part of the suite.

These scripts start real kaimo_authd and/or smbd processes themselves. They
need a readable (dummy) control-plane credential set and a private event spool;
the bridge stays unreachable on purpose (authd fails before any RPC).
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import pytest

TESTS = Path(__file__).resolve().parent.parent
SCRIPTS = [
    "test-authd-protocol.py",
    "test-authd-capacity.py",
    "test-authd-peer-security.py",
    "test-authd-smb-peer.py",
    "test-vfs-operation-compatibility.py",
]


@pytest.fixture(scope="module")
def runtime_env(tmp_path_factory):
    root = tmp_path_factory.mktemp("authd-runtime")
    os.chmod(root, 0o755)
    credentials = root / "credentials"
    credentials.mkdir(mode=0o700)
    for name in ("ca.crt", "samba.crt", "samba.key"):
        (credentials / name).write_text("unused dummy credential\n")
    spool = root / "spool"
    spool.mkdir(mode=0o700)
    env = os.environ.copy()
    env.update(
        {
            "KAIMO_BRIDGE_ADDR": "127.0.0.1:1",
            "KAIMO_BRIDGE_CA_CERT": str(credentials / "ca.crt"),
            "KAIMO_BRIDGE_RUNTIME_CERT": str(credentials / "samba.crt"),
            "KAIMO_BRIDGE_RUNTIME_KEY": str(credentials / "samba.key"),
            "KAIMO_EVENT_SPOOL_PATH": str(spool),
        }
    )
    return env


@pytest.mark.parametrize("script", SCRIPTS)
def test_runtime_script(script, runtime_env):
    result = subprocess.run(
        [os.path.realpath(sys.executable), str(TESTS / script)],
        env=runtime_env,
        text=True,
        capture_output=True,
        timeout=120,
    )
    assert result.returncode == 0, (
        f"{script} exited {result.returncode}\n"
        f"stdout:\n{result.stdout[-4000:]}\nstderr:\n{result.stderr[-8000:]}"
    )
