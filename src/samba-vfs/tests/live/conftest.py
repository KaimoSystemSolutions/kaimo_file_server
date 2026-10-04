# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""pytest fixtures for the live VFS scenario matrix.

One smbd + fake authd stack per test module (smbd start costs ~0.3 s; the
module re-opens the authd socket on every call, so replies can be re-scripted
between tests without restarting anything). Modules that need a different
smbd environment (fail-open, kill switches, deadlines) declare
``SAMBA_ENV = {...}``. Every test starts with an empty share and a reset fake.
"""

from __future__ import annotations

import os
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import kaimo_testlib as kt  # noqa: E402

SHARE = "data"
AUDIT_PREFIX = "KT"


@dataclass
class Stack:
    root: Path
    share: Path
    cache: Path
    authd: kt.FakeAuthd
    samba: kt.Samba
    _log_mark: int = 0

    def smb(self, command: str, **kwargs: object) -> kt.SmbResult:
        return self.samba.smbclient(SHARE, command, **kwargs)

    def mark(self) -> None:
        self._log_mark = len(self.samba.log())

    def new_log(self) -> str:
        return self.samba.log()[self._log_mark :]

    def details(self, *results: kt.SmbResult) -> str:
        return self.samba.diagnostics(*results, authd=self.authd)

    def chown(self, path: Path) -> None:
        kt.chown_tree(path, self.samba.user)

    def _own(self, path: Path) -> None:
        """Hand path and its parents below the share to the SMB user, as if
        the user had created them (POSIX delete/rename checks pass)."""
        entry = __import__("pwd").getpwnam(self.samba.user)
        current = path
        while current != self.share and self.share in current.parents:
            os.chown(current, entry.pw_uid, entry.pw_gid, follow_symlinks=False)
            current = current.parent

    def directory(self, relative: str) -> Path:
        path = self.share / relative
        path.mkdir(parents=True, exist_ok=True)
        self._own(path)
        return path

    def file(self, relative: str, content: str = "data\n") -> Path:
        path = self.share / relative
        if path.parent != self.share:
            self.directory(str(path.parent.relative_to(self.share)))
        path.write_text(content)
        self._own(path)
        return path


def _clear(directory: Path) -> None:
    for entry in directory.iterdir():
        if entry.is_dir() and not entry.is_symlink():
            shutil.rmtree(entry)
        else:
            entry.unlink()


@pytest.fixture(scope="module")
def stack(request: pytest.FixtureRequest):
    module = request.module.__name__.rsplit(".", 1)[-1]
    root = Path(f"/tmp/kaimo-live-{module}-{os.getpid()}")
    if root.exists():
        shutil.rmtree(root)
    root.mkdir(mode=0o755)
    os.chmod(root, 0o755)
    share = root / "share"
    share.mkdir(mode=0o777)
    os.chmod(share, 0o777)
    cache = root / "snapshot-cache"
    cache.mkdir(mode=0o755)

    authd = kt.FakeAuthd(root / "authz.sock").start()
    env = {
        "KAIMO_AUTHD_SOCK": str(authd.socket_path),
        "KAIMO_SNAPSHOT_CACHE_ROOT": str(cache),
    }
    # SAMBA_ENV values of None remove the variable from smbd's environment.
    env.update(getattr(request.module, "SAMBA_ENV", {}))
    share_options = {
        "path": str(share),
        "read only": "no",
        "vfs objects": "kaimo_bridge full_audit",
        "full_audit:syslog": "no",
        "full_audit:priority": "NOTICE",
        "full_audit:prefix": AUDIT_PREFIX,
        "full_audit:success": "connect openat close renameat unlinkat mkdirat",
        "full_audit:failure": "connect openat renameat unlinkat mkdirat",
    }
    # EXTRA_SHARES: further share names on the same directory, e.g. to drive
    # names the module must reject.
    shares = {SHARE: share_options}
    for name in getattr(request.module, "EXTRA_SHARES", ()):
        shares[name] = dict(share_options)
    samba = kt.Samba(root / "samba", shares, env=env)
    try:
        samba.start()
        yield Stack(root, share, cache, authd, samba)
    finally:
        samba.stop()
        authd.stop()
        shutil.rmtree(root, ignore_errors=True)


@pytest.fixture
def s(stack: Stack) -> Stack:
    """Per-test view of the module stack: empty share, reset fake, log mark."""
    _clear(stack.share)
    _clear(stack.cache)
    stack.authd.reset()
    stack.mark()
    return stack


# ---------------------------------------------------------------- fake bridge

SIDECARS = Path(os.environ.get("KAIMO_SIDECAR_DIR", "/usr/local/bin"))


def _pki_generator() -> Path:
    tests = Path(__file__).resolve().parent.parent
    for candidate in (
        Path("/usr/local/bin/generate-control-plane-certs.sh"),  # kcov wrapper
        tests.parent / "scripts" / "generate-control-plane-certs.sh",  # test image
        tests.parent / "generate-control-plane-certs.sh",  # repository checkout
    ):
        if candidate.exists():
            return candidate
    raise FileNotFoundError("generate-control-plane-certs.sh")


@pytest.fixture(scope="session")
def bridge_session():
    import kaimo_fakebridge as fb

    root = Path(f"/tmp/kaimo-bridge-{os.getpid()}")
    if root.exists():
        shutil.rmtree(root)
    root.mkdir(mode=0o700)
    messages = fb.load_messages(root / "generated")
    pki = fb.make_pki(root / "pki", _pki_generator())
    bridge = fb.FakeBridge(messages, pki).start()
    try:
        yield bridge
    finally:
        bridge.stop()
        shutil.rmtree(root, ignore_errors=True)


@pytest.fixture
def bridge(bridge_session):
    bridge_session.reset()
    return bridge_session
