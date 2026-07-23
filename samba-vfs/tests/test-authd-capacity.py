#!/usr/bin/env python3
"""Runtime regression test for the bounded kaimo_authd worker pool."""

from __future__ import annotations

import os
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path


RUNTIME_DIRECTORY = Path(f"/tmp/kaimo-authd-capacity-{os.getpid()}")
SOCKET_PATH = RUNTIME_DIRECTORY / "authz.sock"
WORKERS = 2
QUEUE_CAPACITY = 3
CLIENT_COUNT = 40
HEADER = struct.Struct("!4sBBBBI")


def proc_count(pid: int, name: str) -> int:
    return len(list((Path("/proc") / str(pid) / name).iterdir()))


def wait_for_socket(process: subprocess.Popen[str]) -> None:
    for _ in range(100):
        if SOCKET_PATH.exists():
            return
        if process.poll() is not None:
            stderr = process.stderr.read() if process.stderr else ""
            raise RuntimeError(f"kaimo_authd exited early: {stderr}")
        time.sleep(0.02)
    raise RuntimeError("kaimo_authd did not publish its Unix socket")


def main() -> int:
    RUNTIME_DIRECTORY.mkdir(mode=0o750)
    RUNTIME_DIRECTORY.chmod(0o750)
    os.chown(RUNTIME_DIRECTORY, 0, 0)

    environment = os.environ.copy()
    environment.update(
        {
            "KAIMO_AUTHD_SOCK": str(SOCKET_PATH),
            "KAIMO_AUTHD_WORKERS": str(WORKERS),
            "KAIMO_AUTHD_QUEUE_CAPACITY": str(QUEUE_CAPACITY),
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
    clients: list[socket.socket] = []

    try:
        wait_for_socket(process)
        baseline_threads = proc_count(process.pid, "task")
        baseline_fds = proc_count(process.pid, "fd")

        for _ in range(CLIENT_COUNT):
            client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            client.settimeout(1)
            client.connect(str(SOCKET_PATH))
            clients.append(client)

        time.sleep(0.25)
        loaded_threads = proc_count(process.pid, "task")
        loaded_fds = proc_count(process.pid, "fd")

        rejected = 0
        for client in clients:
            client.setblocking(False)
            try:
                response = bytearray()
                while len(response) != HEADER.size:
                    chunk = client.recv(HEADER.size - len(response))
                    if not chunk:
                        break
                    response.extend(chunk)
                if len(response) == HEADER.size:
                    magic, version, operation, kind, status, length = HEADER.unpack(
                        response
                    )
                    if (
                        magic == b"KAIM"
                        and version == 1
                        and operation == 0
                        and kind == 2
                        and status == 6
                        and length == 0
                    ):
                        rejected += 1
            except BlockingIOError:
                pass

        if loaded_threads != baseline_threads:
            raise AssertionError(
                f"thread count grew under load: {baseline_threads} -> {loaded_threads}"
            )

        # Two workers + three queued descriptors, plus at most one descriptor
        # between accept() and the queue-full decision.
        maximum_server_fd_growth = WORKERS + QUEUE_CAPACITY + 1
        if loaded_fds > baseline_fds + maximum_server_fd_growth:
            raise AssertionError(
                f"server FD growth exceeded bound: {baseline_fds} -> {loaded_fds}"
            )

        minimum_rejections = CLIENT_COUNT - maximum_server_fd_growth
        if rejected < minimum_rejections:
            raise AssertionError(
                f"only {rejected} overload clients received ERROR; "
                f"expected at least {minimum_rejections}"
            )

        # Silent clients initially occupying workers/queue must all be released
        # after bounded batches of the configured receive deadline.
        time.sleep(2)
        settled_fds = proc_count(process.pid, "fd")
        if settled_fds > baseline_fds:
            raise AssertionError(
                f"silent-client descriptors survived receive deadlines: "
                f"{baseline_fds} -> {settled_fds}"
            )

        print(
            "authd capacity test passed: "
            f"threads={loaded_threads}, server_fds={loaded_fds}, "
            f"settled_fds={settled_fds}, overload_rejections={rejected}"
        )
        return 0
    finally:
        for client in clients:
            client.close()
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
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"authd capacity test failed: {error}", file=sys.stderr)
        raise
