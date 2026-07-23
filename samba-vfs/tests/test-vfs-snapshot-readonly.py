#!/usr/bin/env python3
"""Live P1-06 regression: timewarp content is read-only and stays in VFS."""

from __future__ import annotations

import os
import shutil
import socket
import struct
import subprocess
import threading
import time
from pathlib import Path


ROOT = Path(f"/tmp/kaimo-vfs-snapshot-readonly-{os.getpid()}")
SOCKET_PATH = ROOT / "authz.sock"
SHARE_PATH = ROOT / "share"
CACHE_ROOT = ROOT / "cache"
CACHE_RELATIVE = Path("projection/historical.txt")
SNAPSHOT_PATH = CACHE_ROOT / CACHE_RELATIVE
CONFIG_PATH = ROOT / "smb.conf"
SMBD_LOG = ROOT / "smbd.log"
DOWNLOAD_PATH = ROOT / "downloaded.txt"
UPLOAD_PATH = ROOT / "replacement.txt"
USER = "kaimosnapshot"
PASSWORD = "SnapshotPassw0rd!"
GMT = "@GMT-2024.01.02-03.04.05"
HEADER = struct.Struct("!4sBBBBI")


def run(command: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, check=True, text=True, **kwargs)


def recv_exact(connection: socket.socket, length: int) -> bytes:
    data = bytearray()
    while len(data) < length:
        chunk = connection.recv(length - len(data))
        if not chunk:
            raise EOFError("truncated local-protocol frame")
        data.extend(chunk)
    return bytes(data)


def encoded_string(value: str) -> bytes:
    raw = value.encode()
    return struct.pack("!I", len(raw)) + raw


def response(operation: int, status: int, payload: bytes = b"") -> bytes:
    return HEADER.pack(b"KAIM", 1, operation, 2, status, len(payload)) + payload


def serve_requests(ready: threading.Event, stop: threading.Event) -> None:
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
        listener.bind(str(SOCKET_PATH))
        os.chmod(SOCKET_PATH, 0o777)
        listener.listen(16)
        listener.settimeout(0.1)
        ready.set()
        while not stop.is_set():
            try:
                connection, _ = listener.accept()
            except TimeoutError:
                continue
            with connection:
                connection.settimeout(2)
                try:
                    header = HEADER.unpack(recv_exact(connection, HEADER.size))
                    magic, version, operation, kind, status, length = header
                    assert (magic, version, kind, status) == (
                        b"KAIM",
                        1,
                        1,
                        0,
                    )
                    recv_exact(connection, length)
                    if operation in (1, 3, 4):
                        connection.sendall(response(operation, 2))
                    elif operation == 2:
                        # Deliberately claim every specific right. The VFS must
                        # attenuate this to its snapshot read-only mask.
                        connection.sendall(
                            response(operation, 2, struct.pack("!I", 0x001F01FF))
                        )
                    elif operation == 10:
                        payload = (
                            encoded_string(CACHE_RELATIVE.as_posix())
                            + struct.pack("!Q", len(b"SNAPSHOT\n"))
                        )
                        connection.sendall(response(operation, 1, payload))
                    elif operation == 9:
                        connection.sendall(response(operation, 1, struct.pack("!I", 0)))
                except (AssertionError, EOFError, OSError, TimeoutError):
                    continue


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


def smb(command: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            "/opt/samba/bin/smbclient",
            "//127.0.0.1/snapshot-share",
            "-p",
            "445",
            "-U",
            f"{USER}%{PASSWORD}",
            "-c",
            command,
        ],
        check=False,
        text=True,
        capture_output=True,
        timeout=10,
    )


def diagnostics(*results: subprocess.CompletedProcess[str]) -> str:
    client_output = "\n".join(
        f"stdout:\n{item.stdout}\nstderr:\n{item.stderr}" for item in results
    )
    return (
        f"{client_output}\nsmbd:\n"
        f"{SMBD_LOG.read_text(encoding='utf-8', errors='replace')}"
    )


def main() -> int:
    ROOT.mkdir(mode=0o755)
    SHARE_PATH.mkdir(mode=0o755)
    SNAPSHOT_PATH.parent.mkdir(parents=True, mode=0o755)
    (SHARE_PATH / "historical.txt").write_text("LIVE\n", encoding="utf-8")
    SNAPSHOT_PATH.write_text("SNAPSHOT\n", encoding="utf-8")
    UPLOAD_PATH.write_text("REPLACEMENT\n", encoding="utf-8")

    run(["useradd", "-M", "-s", "/usr/sbin/nologin", USER])
    run(["chown", "-R", f"{USER}:{USER}", str(SHARE_PATH), str(CACHE_ROOT)])
    # Make the cached object writable at the POSIX layer: a successful denial
    # then proves the VFS policy, not filesystem permissions, protected it.
    SNAPSHOT_PATH.chmod(0o666)
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
                "[snapshot-share]",
                f"path = {SHARE_PATH}",
                "read only = no",
                "vfs objects = kaimo_bridge full_audit",
                "full_audit:syslog = no",
                "full_audit:priority = NOTICE",
                "full_audit:prefix = P1-06",
                "full_audit:success = openat",
                "full_audit:failure = openat",
                "",
            ]
        ),
        encoding="utf-8",
    )

    ready = threading.Event()
    stop = threading.Event()
    server = threading.Thread(
        target=serve_requests, args=(ready, stop), daemon=True
    )
    server.start()
    if not ready.wait(timeout=2):
        raise RuntimeError("fake local sidecar did not start")

    environment = os.environ.copy()
    environment["KAIMO_AUTHD_SOCK"] = str(SOCKET_PATH)
    environment["KAIMO_SNAPSHOT_CACHE_ROOT"] = str(CACHE_ROOT)
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
            read_result = smb(
                f"get {GMT}/historical.txt {DOWNLOAD_PATH}"
            )
            write_result = smb(
                f"put {UPLOAD_PATH} {GMT}/historical.txt"
            )
            delete_result = smb(f"del {GMT}/historical.txt")
            rename_result = smb(
                f"rename {GMT}/historical.txt moved.txt"
            )
            mkdir_result = smb(f"mkdir {GMT}/new-directory")
    finally:
        terminate(smbd)
        stop.set()
        server.join(timeout=2)

    details = diagnostics(
        read_result,
        write_result,
        delete_result,
        rename_result,
        mkdir_result,
    )
    assert read_result.returncode == 0, details
    assert DOWNLOAD_PATH.read_text(encoding="utf-8") == "SNAPSHOT\n", details
    for result in (
        write_result,
        delete_result,
        rename_result,
        mkdir_result,
    ):
        # smbclient returns zero for some failed interactive commands, so the
        # server's precise status is the stable assertion.
        assert "NT_STATUS_MEDIA_WRITE_PROTECTED" in (
            result.stdout + result.stderr
        ), details
    assert SNAPSHOT_PATH.read_text(encoding="utf-8") == "SNAPSHOT\n", details
    assert (SHARE_PATH / "historical.txt").read_text(encoding="utf-8") == "LIVE\n"
    assert not (SHARE_PATH / "moved.txt").exists(), details
    assert not (SHARE_PATH / "new-directory").exists(), details

    log_text = SMBD_LOG.read_text(encoding="utf-8", errors="replace")
    assert "read-only stacked snapshots" in log_text, details
    assert "CREATE twrp write intent denied" in log_text, details
    # full_audit is the VFS module after kaimo_bridge. Its successful record for
    # the same historical open proves the redirect reached the next VFS layer
    # (full_audit formats the record from fsp->fsp_name, which stays logical).
    audit_open = f"P1-06|openat|ok|r|{SHARE_PATH}/historical.txt"
    assert audit_open in log_text, details
    print("P1-06 live snapshot read-only/VFS-stack regression passed")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    finally:
        if ROOT.exists():
            shutil.rmtree(ROOT, ignore_errors=True)
