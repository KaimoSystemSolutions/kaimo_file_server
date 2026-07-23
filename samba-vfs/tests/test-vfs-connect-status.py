#!/usr/bin/env python3
"""Verify a denied VFS connect becomes a prompt-compatible ACCESS_DENIED."""

from __future__ import annotations

import os
import shutil
import socket
import struct
import subprocess
import threading
import time
from pathlib import Path


ROOT = Path(f"/tmp/kaimo-vfs-connect-status-{os.getpid()}")
SOCKET_PATH = ROOT / "authz.sock"
SHARE_PATH = ROOT / "share"
CONFIG_PATH = ROOT / "smb.conf"
SMBD_LOG = ROOT / "smbd.log"
USER = "kaimostatus"
PASSWORD = "StatusPassw0rd!"
HEADER = struct.Struct("!4sBBBBI")
MAGIC = b"KAIM"
VERSION = 1
CONNECT = 1
REQUEST = 1
RESPONSE = 2
DENY = 3


def run(command: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, check=True, text=True, **kwargs)


def read_exact(connection: socket.socket, length: int) -> bytes:
    chunks: list[bytes] = []
    remaining = length
    while remaining:
        chunk = connection.recv(remaining)
        if not chunk:
            raise ConnectionError("client closed before sending the complete frame")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def serve_denials(ready: threading.Event, stop: threading.Event) -> None:
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
            with connection:
                header = read_exact(connection, HEADER.size)
                magic, version, operation, kind, status, payload_length = (
                    HEADER.unpack(header)
                )
                if (
                    magic != MAGIC
                    or version != VERSION
                    or operation != CONNECT
                    or kind != REQUEST
                    or status != 0
                ):
                    raise AssertionError("unexpected local authorization frame")
                read_exact(connection, payload_length)
                connection.sendall(
                    HEADER.pack(MAGIC, VERSION, CONNECT, RESPONSE, DENY, 0)
                )


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
                "[denied-share]",
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
        target=serve_denials, args=(ready, stop), daemon=True
    )
    server.start()
    if not ready.wait(timeout=2):
        raise RuntimeError("fake authorization sidecar did not start")

    environment = os.environ.copy()
    environment["KAIMO_AUTHD_SOCK"] = str(SOCKET_PATH)
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

            wrong_password = subprocess.run(
                [
                    "/opt/samba/bin/smbclient",
                    "//127.0.0.1/denied-share",
                    "-p",
                    "445",
                    "-U",
                    f"{USER}%wrong-password",
                    "-c",
                    "ls",
                ],
                check=False,
                text=True,
                capture_output=True,
            )
            started = time.monotonic()
            result = subprocess.run(
                [
                    "/opt/samba/bin/smbclient",
                    "//127.0.0.1/denied-share",
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
            duration = time.monotonic() - started
    finally:
        terminate(smbd)
        stop.set()
        server.join(timeout=2)

    diagnostics = (
        f"wrong-password stdout:\n{wrong_password.stdout}\n"
        f"wrong-password stderr:\n{wrong_password.stderr}\n"
        f"deny stdout:\n{result.stdout}\ndeny stderr:\n{result.stderr}\n"
        f"smbd:\n{SMBD_LOG.read_text(encoding='utf-8', errors='replace')}"
    )
    assert wrong_password.returncode != 0, diagnostics
    assert (
        "NT_STATUS_LOGON_FAILURE"
        in wrong_password.stdout + wrong_password.stderr
    ), diagnostics
    assert result.returncode != 0, diagnostics
    assert "NT_STATUS_ACCESS_DENIED" in result.stdout + result.stderr, diagnostics
    assert duration < 1.5, f"denial took {duration:.3f}s\n{diagnostics}"
    print(f"VFS connect denial returned NT_STATUS_ACCESS_DENIED in {duration:.3f}s")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    finally:
        if ROOT.exists():
            shutil.rmtree(ROOT, ignore_errors=True)
