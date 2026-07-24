#!/usr/bin/env python3
"""Verify a stalled local sidecar cannot pin a real smbd worker."""

from __future__ import annotations

import os
import shutil
import socket
import subprocess
import threading
import time
from pathlib import Path


ROOT = Path(f"/tmp/kaimo-vfs-io-deadline-{os.getpid()}")
SOCKET_PATH = ROOT / "authz.sock"
SHARE_PATH = ROOT / "share"
CONFIG_PATH = ROOT / "smb.conf"
SMBD_LOG = ROOT / "smbd.log"
USER = "kaimodeadline"
PASSWORD = "DeadlinePassw0rd!"
TIMEOUT_MS = 300


def run(command: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, check=True, text=True, **kwargs)


def serve_stalls(ready: threading.Event, stop: threading.Event) -> None:
    clients: list[socket.socket] = []
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
            listener.bind(str(SOCKET_PATH))
            os.chmod(SOCKET_PATH, 0o777)
            listener.listen(8)
            listener.settimeout(0.1)
            ready.set()
            while not stop.is_set():
                try:
                    connection, _ = listener.accept()
                except TimeoutError:
                    continue
                clients.append(connection)
    finally:
        for client in clients:
            client.close()


def wait_for_smb(process: subprocess.Popen[bytes]) -> None:
    for _ in range(150):
        if process.poll() is not None:
            output = SMBD_LOG.read_text(encoding="utf-8", errors="replace")
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
    SHARE_PATH.mkdir(mode=0o755)
    run(["useradd", "-M", "-s", "/usr/sbin/nologin", USER])
    run(
        ["/opt/samba/bin/smbpasswd", "-s", "-a", USER],
        input=f"{PASSWORD}\n{PASSWORD}\n",
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
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
                "[deadline-share]",
                f"path = {SHARE_PATH}",
                "read only = no",
                "vfs objects = kaimo_bridge",
                "",
            ]
        ),
        encoding="utf-8",
    )

    ready = threading.Event()
    stop = threading.Event()
    server = threading.Thread(
        target=serve_stalls, args=(ready, stop), daemon=True
    )
    server.start()
    if not ready.wait(timeout=2):
        raise RuntimeError("fake stalled sidecar did not start")

    environment = os.environ.copy()
    environment["KAIMO_AUTHD_SOCK"] = str(SOCKET_PATH)
    environment["KAIMO_VFS_AUTH_TIMEOUT_MS"] = str(TIMEOUT_MS)
    smbd: subprocess.Popen[bytes] | None = None
    try:
        with SMBD_LOG.open("wb") as log:
            smbd = subprocess.Popen(
                [
                    "/opt/samba/sbin/smbd",
                    "--foreground",
                    "--no-process-group",
                    "--debug-stdout",
                    "--configfile",
                    str(CONFIG_PATH),
                ],
                env=environment,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            wait_for_smb(smbd)
            started = time.monotonic()
            result = subprocess.run(
                [
                    "/opt/samba/bin/smbclient",
                    "//127.0.0.1/deadline-share",
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
                timeout=3,
            )
            duration = time.monotonic() - started
    finally:
        terminate(smbd)
        stop.set()
        server.join(timeout=2)

    diagnostics = (
        f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}\n"
        f"smbd:\n{SMBD_LOG.read_text(encoding='utf-8', errors='replace')}"
    )
    assert result.returncode != 0, diagnostics
    assert "NT_STATUS_ACCESS_DENIED" in result.stdout + result.stderr, diagnostics
    assert duration >= 0.20, f"request did not reach the stall: {duration:.3f}s"
    assert duration < 1.5, f"deadline took {duration:.3f}s\n{diagnostics}"
    log_text = SMBD_LOG.read_text(encoding="utf-8", errors="replace")
    assert (
        f"local deadlines auth={TIMEOUT_MS} ms" in log_text
    ), f"configured deadline marker missing\n{diagnostics}"
    print(
        "Stalled VFS authorization failed closed in "
        f"{duration:.3f}s with a {TIMEOUT_MS} ms end-to-end budget"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    finally:
        if ROOT.exists():
            shutil.rmtree(ROOT, ignore_errors=True)
