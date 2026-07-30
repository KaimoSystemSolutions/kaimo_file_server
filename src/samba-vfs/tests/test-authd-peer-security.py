#!/usr/bin/env python3
"""Runtime regression tests for authd Unix peer and session identity checks."""

from __future__ import annotations

import os
import pwd
import shutil
import socket
import stat
import struct
import subprocess
import sys
import time
from pathlib import Path


RUNTIME_DIRECTORY = Path(f"/tmp/kaimo-authd-peer-{os.getpid()}")
SOCKET_PATH = RUNTIME_DIRECTORY / "authz.sock"
COPIED_PYTHON = RUNTIME_DIRECTORY / "untrusted-python"
HEADER = struct.Struct("!4sBBBBI")
PROTOCOL_VERSION = 4


def encoded_string(value: str) -> bytes:
    data = value.encode("utf-8")
    return struct.pack("!I", len(data)) + data


def request_for(username: str) -> bytes:
    # The trailing byte deliberately makes CONNECT invalid after the common
    # user/share prefix. An authorized peer therefore gets protocol ERROR (5)
    # without a gRPC call, while an identity mismatch gets UNAUTHORIZED_PEER (7).
    payload = encoded_string(username) + encoded_string("share") + b"x"
    return HEADER.pack(
        b"KAIM", PROTOCOL_VERSION, 1, 1, 0, len(payload)
    ) + payload


def receive_header(client: socket.socket) -> tuple[bytes, int, int, int, int, int]:
    data = bytearray()
    while len(data) != HEADER.size:
        chunk = client.recv(HEADER.size - len(data))
        if not chunk:
            raise AssertionError("authd closed without a framed response")
        data.extend(chunk)
    return HEADER.unpack(data)


def claim(username: str) -> tuple[bytes, int, int, int, int, int]:
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
        client.settimeout(2)
        client.connect(str(SOCKET_PATH))
        client.sendall(request_for(username))
        return receive_header(client)


def wait_for_socket(process: subprocess.Popen[str]) -> None:
    for _ in range(100):
        if SOCKET_PATH.exists():
            return
        if process.poll() is not None:
            stderr = process.stderr.read() if process.stderr else ""
            raise RuntimeError(f"kaimo_authd exited early: {stderr}")
        time.sleep(0.02)
    raise RuntimeError("kaimo_authd did not publish its Unix socket")


def run_claim_as(
    account_name: str, claimed_name: str, socket_group_access: bool
) -> str:
    account = pwd.getpwnam(account_name)
    read_fd, write_fd = os.pipe()
    pid = os.fork()
    if pid == 0:
        try:
            os.close(read_fd)
            os.setgroups([0] if socket_group_access else [])
            os.setgid(account.pw_gid)
            os.setuid(account.pw_uid)
            try:
                result = repr(claim(claimed_name))
            except Exception as error:  # sent to parent for explicit assertion
                result = f"{type(error).__name__}:{error}"
            os.write(write_fd, result.encode("utf-8"))
        finally:
            os._exit(0)

    os.close(write_fd)
    result = os.read(read_fd, 4096).decode("utf-8")
    os.close(read_fd)
    waited_pid, wait_status = os.waitpid(pid, 0)
    assert waited_pid == pid and os.waitstatus_to_exitcode(wait_status) == 0
    return result


UNTRUSTED_CLIENT = r"""
import socket, struct, sys
path, claimed = sys.argv[1], sys.argv[2]
header = struct.Struct("!4sBBBBI")
def field(value):
    data = value.encode("utf-8")
    return struct.pack("!I", len(data)) + data
payload = field(claimed) + field("share") + b"x"
with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
    client.settimeout(2)
    client.connect(path)
    client.sendall(
        header.pack(
            b"KAIM", PROTOCOL_VERSION, 1, 1, 0, len(payload)
        ) + payload
    )
    data = b""
    while len(data) != header.size:
        chunk = client.recv(header.size - len(data))
        if not chunk:
            raise RuntimeError("unexpected EOF")
        data += chunk
    print(repr(header.unpack(data)))
"""


def main() -> int:
    RUNTIME_DIRECTORY.mkdir(mode=0o750)
    os.chown(RUNTIME_DIRECTORY, 0, 0)
    RUNTIME_DIRECTORY.chmod(0o750)
    shutil.copy2(os.path.realpath("/proc/self/exe"), COPIED_PYTHON)
    COPIED_PYTHON.chmod(0o755)

    environment = os.environ.copy()
    environment.update(
        {
            "KAIMO_AUTHD_SOCK": str(SOCKET_PATH),
            "KAIMO_AUTHD_GROUP": "root",
            "KAIMO_AUTHD_PEER_EXECUTABLE": os.path.realpath("/proc/self/exe"),
            "KAIMO_AUTHD_WORKERS": "2",
            "KAIMO_AUTHD_QUEUE_CAPACITY": "4",
            "KAIMO_AUTHD_IO_TIMEOUT_MS": "500",
            "KAIMO_BRIDGE_ADDR": "127.0.0.1:1",
        }
    )
    process = subprocess.Popen(
        ["/usr/local/bin/kaimo_authd"],
        env=environment,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
    )

    try:
        wait_for_socket(process)
        directory_mode = stat.S_IMODE(RUNTIME_DIRECTORY.stat().st_mode)
        socket_details = SOCKET_PATH.stat()
        assert directory_mode == 0o750
        assert stat.S_IMODE(socket_details.st_mode) == 0o660
        assert socket_details.st_uid == 0 and socket_details.st_gid == 0

        # A verified privileged peer (the test's stand-in for root smbd) may
        # carry the authenticated session identity from Samba.
        assert claim("daemon") == (
            b"KAIM", PROTOCOL_VERSION, 1, 2, 5, 0
        )

        matching = run_claim_as("nobody", "nobody", True)
        assert f"'KAIM', {PROTOCOL_VERSION}, 1, 2, 5, 0" in matching, matching

        mismatched = run_claim_as("nobody", "daemon", True)
        assert f"'KAIM', {PROTOCOL_VERSION}, 1, 2, 7, 0" in mismatched, mismatched

        no_group = run_claim_as("nobody", "nobody", False)
        assert no_group.startswith("PermissionError:"), no_group

        wrong_executable = subprocess.run(
            [
                str(COPIED_PYTHON),
                "-c",
                UNTRUSTED_CLIENT,
                str(SOCKET_PATH),
                "daemon",
            ],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
        assert f"'KAIM', {PROTOCOL_VERSION}, 0, 2, 7, 0" in wrong_executable, wrong_executable

        print("authd peer-security runtime tests passed")
        return 0
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
        try:
            SOCKET_PATH.unlink()
        except FileNotFoundError:
            pass
        try:
            COPIED_PYTHON.unlink()
        except FileNotFoundError:
            pass
        RUNTIME_DIRECTORY.rmdir()


if __name__ == "__main__":
    raise SystemExit(main())
