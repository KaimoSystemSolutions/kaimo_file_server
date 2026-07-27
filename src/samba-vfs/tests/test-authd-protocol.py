#!/usr/bin/env python3
"""Runtime regression tests for authd's framed local Unix protocol."""

from __future__ import annotations

import os
import socket
import struct
import subprocess
import time
from pathlib import Path


RUNTIME_DIRECTORY = Path(f"/tmp/kaimo-authd-protocol-{os.getpid()}")
SOCKET_PATH = RUNTIME_DIRECTORY / "authz.sock"
HEADER = struct.Struct("!4sBBBBI")


def wait_for_socket(process: subprocess.Popen[str]) -> None:
    for _ in range(100):
        if SOCKET_PATH.exists():
            return
        if process.poll() is not None:
            stderr = process.stderr.read() if process.stderr else ""
            raise RuntimeError(f"kaimo_authd exited early: {stderr}")
        time.sleep(0.02)
    raise RuntimeError("kaimo_authd did not publish its Unix socket")


def connect() -> socket.socket:
    client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    client.settimeout(2)
    client.connect(str(SOCKET_PATH))
    return client


def recv_exact(client: socket.socket, length: int) -> bytes:
    result = bytearray()
    while len(result) != length:
        chunk = client.recv(length - len(result))
        if not chunk:
            raise AssertionError(
                f"unexpected EOF after {len(result)} of {length} bytes"
            )
        result.extend(chunk)
    return bytes(result)


def encoded_string(value: bytes) -> bytes:
    return struct.pack("!I", len(value)) + value


def assert_protocol_error_for_fragmented_request() -> None:
    # This is structurally a CONNECT request, but the trailing byte makes the
    # operation payload invalid. Sending every byte separately proves authd
    # does not depend on one read() corresponding to one message.
    payload = encoded_string(b"user\tname") + encoded_string(b"share\nname") + b"x"
    frame = HEADER.pack(b"KAIM", 1, 1, 1, 0, len(payload)) + payload
    with connect() as client:
        for byte in frame:
            client.sendall(bytes((byte,)))
        response = HEADER.unpack(recv_exact(client, HEADER.size))
        assert response == (b"KAIM", 1, 1, 2, 5, 0), response


def assert_truncated_payload_is_rejected() -> None:
    with connect() as client:
        client.sendall(HEADER.pack(b"KAIM", 1, 1, 1, 0, 5) + b"ab")
        client.shutdown(socket.SHUT_WR)
        assert client.recv(1) == b""


def assert_oversized_frame_is_rejected_before_payload() -> None:
    with connect() as client:
        client.sendall(HEADER.pack(b"KAIM", 1, 1, 1, 0, 8193))
        assert client.recv(1) == b""


def assert_unknown_version_is_rejected() -> None:
    with connect() as client:
        client.sendall(HEADER.pack(b"KAIM", 2, 1, 1, 0, 0))
        assert client.recv(1) == b""


def main() -> int:
    RUNTIME_DIRECTORY.mkdir(mode=0o750)
    RUNTIME_DIRECTORY.chmod(0o750)
    os.chown(RUNTIME_DIRECTORY, 0, 0)

    environment = os.environ.copy()
    environment.update(
        {
            "KAIMO_AUTHD_SOCK": str(SOCKET_PATH),
            "KAIMO_AUTHD_WORKERS": "1",
            "KAIMO_AUTHD_QUEUE_CAPACITY": "4",
            "KAIMO_AUTHD_IO_TIMEOUT_MS": "500",
            "KAIMO_AUTHD_GROUP": "root",
            "KAIMO_AUTHD_PEER_EXECUTABLE": os.path.realpath("/proc/self/exe"),
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
        assert_protocol_error_for_fragmented_request()
        assert_truncated_payload_is_rejected()
        assert_oversized_frame_is_rejected_before_payload()
        assert_unknown_version_is_rejected()
        print("authd framed-protocol runtime tests passed")
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
        RUNTIME_DIRECTORY.rmdir()


if __name__ == "__main__":
    raise SystemExit(main())
