#!/usr/bin/env python3
"""Live smbd -> VFS -> authd peer-identity regression test."""

from __future__ import annotations

import os
import shutil
import socket
import subprocess
import tempfile
import time
from pathlib import Path


ROOT = Path(f"/tmp/kaimo-authd-smb-peer-{os.getpid()}")
RUNTIME_DIRECTORY = ROOT / "run"
SOCKET_PATH = RUNTIME_DIRECTORY / "authz.sock"
CREDENTIAL_DIRECTORY = ROOT / "credentials"
SHARE_PATH = ROOT / "share"
CONFIG_PATH = ROOT / "smb.conf"
AUTHD_LOG = ROOT / "authd.log"
SMBD_LOG = ROOT / "smbd.log"
GROUP = "kaimo-authd-smbtest"
USER = "kaimosmbpeer"
PASSWORD = "PeerPassw0rd!"


def run(command: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, check=True, text=True, **kwargs)


def wait_for_path(
    path: Path, process: subprocess.Popen[bytes], log_path: Path
) -> None:
    for _ in range(150):
        if path.exists():
            return
        if process.poll() is not None:
            output = log_path.read_text(encoding="utf-8", errors="replace")
            raise RuntimeError(
                f"{process.args[0]} exited with {process.returncode}:\n{output}"
            )
        time.sleep(0.02)
    raise RuntimeError(f"timed out waiting for {path}")


def wait_for_smb(process: subprocess.Popen[bytes], log_path: Path) -> None:
    for _ in range(150):
        if process.poll() is not None:
            output = log_path.read_text(encoding="utf-8", errors="replace")
            raise RuntimeError(f"smbd exited with {process.returncode}:\n{output}")
        try:
            with socket.create_connection(("127.0.0.1", 445), timeout=0.1):
                return
        except OSError:
            time.sleep(0.02)
    raise RuntimeError("smbd did not listen on port 445")


def terminate(process: subprocess.Popen[bytes] | None) -> None:
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def main() -> int:
    ROOT.mkdir(mode=0o755)
    SHARE_PATH.mkdir(mode=0o700)
    run(["groupadd", "--system", GROUP])
    run(["useradd", "-M", "-s", "/usr/sbin/nologin", USER])
    run(["usermod", "-aG", GROUP, USER])
    run(["chown", f"{USER}:{USER}", str(SHARE_PATH)])

    password_input = f"{PASSWORD}\n{PASSWORD}\n"
    run(
        ["/opt/samba/bin/smbpasswd", "-s", "-a", USER],
        input=password_input,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    group_gid = int(
        run(
            ["getent", "group", GROUP],
            capture_output=True,
        ).stdout.split(":")[2]
    )
    RUNTIME_DIRECTORY.mkdir(mode=0o750)
    os.chown(RUNTIME_DIRECTORY, 0, group_gid)
    RUNTIME_DIRECTORY.chmod(0o750)
    CREDENTIAL_DIRECTORY.mkdir(mode=0o700)
    # The bridge endpoint is deliberately absent in this peer-identity test.
    # Non-empty placeholders are enough to construct the unused TLS channel;
    # the regression is about the authenticated local smbd -> authd hop.
    for name in ("ca.crt", "authd.crt", "authd.key"):
        (CREDENTIAL_DIRECTORY / name).write_text(
            "unused peer-test credential\n", encoding="utf-8"
        )

    CONFIG_PATH.write_text(
        "\n".join(
            [
                "[global]",
                "server role = standalone server",
                "workgroup = WORKGROUP",
                "map to guest = never",
                "passdb backend = tdbsam",
                "interfaces = 127.0.0.1",
                "bind interfaces only = yes",
                "logging = stdout",
                "log level = 3 vfs:10",
                "",
                "[peer-test]",
                f"path = {SHARE_PATH}",
                "read only = no",
                "vfs objects = kaimo_bridge",
                "",
            ]
        ),
        encoding="utf-8",
    )

    common_environment = os.environ.copy()
    common_environment.update(
        {
            "KAIMO_AUTHD_SOCK": str(SOCKET_PATH),
            "KAIMO_AUTHD_GROUP": GROUP,
            "KAIMO_AUTHD_PEER_EXECUTABLE": "/opt/samba/sbin/smbd",
            "KAIMO_BRIDGE_ADDR": "127.0.0.1:1",
            "KAIMO_BRIDGE_CA_CERT": str(CREDENTIAL_DIRECTORY / "ca.crt"),
            "KAIMO_BRIDGE_RUNTIME_CERT": str(
                CREDENTIAL_DIRECTORY / "authd.crt"
            ),
            "KAIMO_BRIDGE_RUNTIME_KEY": str(
                CREDENTIAL_DIRECTORY / "authd.key"
            ),
            "KAIMO_AUTHD_WORKERS": "2",
            "KAIMO_AUTHD_QUEUE_CAPACITY": "8",
            "KAIMO_AUTHD_IO_TIMEOUT_MS": "1000",
            # The bridge is intentionally absent. Reaching its error path and
            # then listing successfully proves the local peer was accepted.
            "KAIMO_AUTHZ_FAILOPEN": "1",
        }
    )

    authd: subprocess.Popen[bytes] | None = None
    smbd: subprocess.Popen[bytes] | None = None
    try:
        with AUTHD_LOG.open("wb") as authd_log, SMBD_LOG.open("wb") as smbd_log:
            authd = subprocess.Popen(
                ["/usr/local/bin/kaimo_authd"],
                env=common_environment,
                stdout=subprocess.DEVNULL,
                stderr=authd_log,
            )
            wait_for_path(SOCKET_PATH, authd, AUTHD_LOG)

            smbd = subprocess.Popen(
                [
                    "/opt/samba/sbin/smbd",
                    "--foreground",
                    "--no-process-group",
                    "--debug-stdout",
                    "--configfile",
                    str(CONFIG_PATH),
                ],
                env=common_environment,
                stdout=smbd_log,
                stderr=subprocess.STDOUT,
            )
            wait_for_smb(smbd, SMBD_LOG)

            result = subprocess.run(
                [
                    "/opt/samba/bin/smbclient",
                    "//127.0.0.1/peer-test",
                    "-p",
                    "445",
                    "-U",
                    f"{USER}%{PASSWORD}",
                    "-c",
                    "ls",
                ],
                check=False,
                text=True,
                capture_output=True,
            )
            if result.returncode != 0:
                smbd_log.flush()
                authd_log.flush()
                raise RuntimeError(
                    "smbclient failed:\n"
                    f"stdout:\n{result.stdout}\n"
                    f"stderr:\n{result.stderr}\n"
                    f"authd:\n{AUTHD_LOG.read_text(encoding='utf-8', errors='replace')}\n"
                    f"smbd:\n{SMBD_LOG.read_text(encoding='utf-8', errors='replace')}"
                )
            assert "blocks of size" in result.stdout, result.stdout
    finally:
        terminate(smbd)
        terminate(authd)

    authd_output = AUTHD_LOG.read_text(encoding="utf-8", errors="replace")
    smbd_output = SMBD_LOG.read_text(encoding="utf-8", errors="replace")
    diagnostics = f"authd:\n{authd_output}\nsmbd:\n{smbd_output}"
    assert "AuthorizeConnect:" in authd_output, diagnostics
    assert "rejected unauthorized local peer" not in authd_output, diagnostics
    assert "cannot claim user" not in authd_output, diagnostics
    assert (
        "kaimo_bridge build [2026-07-23g read-only stacked snapshots]" in smbd_output
    ), diagnostics

    print("live smbd/authd peer-identity test passed")
    shutil.rmtree(ROOT)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    finally:
        if ROOT.exists():
            shutil.rmtree(ROOT, ignore_errors=True)
