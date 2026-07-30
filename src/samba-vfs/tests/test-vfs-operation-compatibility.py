#!/usr/bin/env python3
"""Load the real VFS stack and exercise every configured full_audit operation."""

from __future__ import annotations

import os
import pwd
import shutil
import socket
import struct
import subprocess
import threading
import time
from pathlib import Path


ROOT = Path(f"/tmp/kaimo-vfs-compatibility-{os.getpid()}")
SOCKET_PATH = ROOT / "authz.sock"
SHARE_PATH = ROOT / "share"
CONFIG_PATH = ROOT / "smb.conf"
AUTH_FILE = ROOT / "smbclient-auth"
LOCAL_SOURCE = ROOT / "source.txt"
LOCAL_DOWNLOAD = ROOT / "download.txt"
SMBD_LOG = ROOT / "smbd.log"
USER = f"kaimoci{os.getpid()}"
PASSWORD = "Ephemeral-CI-Only-7f3!"
HEADER = struct.Struct("!4sBBBBI")
MAGIC = b"KAIM"
VERSION = 3
REQUEST = 1
RESPONSE = 2
STATUS_OK = 1
STATUS_ALLOW = 2
STATUS_NOT_FOUND = 4
OP_CONNECT = 1
OP_OPEN = 2
OP_DELETE_AUTH = 3
OP_RENAME_AUTH = 4
OP_SNAPSHOT_ENUMERATE = 9
OP_SNAPSHOT_RESOLVE = 10
OP_SNAPSHOT_RELEASE = 11
EXPECTED_AUDIT_OPERATIONS = {
    "connect",
    "disconnect",
    "openat",
    "close",
    "renameat",
    "unlinkat",
    "mkdirat",
}


def run(command: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, check=True, text=True, **kwargs)


def read_exact(connection: socket.socket, length: int) -> bytes:
    chunks: list[bytes] = []
    while length:
        chunk = connection.recv(length)
        if not chunk:
            raise ConnectionError("client closed before sending a complete frame")
        chunks.append(chunk)
        length -= len(chunk)
    return b"".join(chunks)


def serve_authorization(
    ready: threading.Event, stop: threading.Event, observed: list[int]
) -> None:
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
                header = read_exact(connection, HEADER.size)
                magic, version, operation, kind, status, payload_length = (
                    HEADER.unpack(header)
                )
                if (
                    magic != MAGIC
                    or version != VERSION
                    or kind != REQUEST
                    or status != 0
                ):
                    raise AssertionError("unexpected local protocol header")
                payload = read_exact(connection, payload_length)
                observed.append(operation)
                response_payload = b""
                if operation == OP_OPEN:
                    response_status = STATUS_ALLOW
                    # Return Samba 4.19.5 FILE_GENERIC_ALL after generic
                    # expansion. The VFS accepts only specific access bits.
                    response_payload = struct.pack("!I", 0x001F01FF)
                elif operation in {
                    OP_CONNECT,
                    OP_DELETE_AUTH,
                    OP_RENAME_AUTH,
                }:
                    response_status = STATUS_ALLOW
                elif operation == OP_SNAPSHOT_ENUMERATE:
                    response_status = STATUS_OK
                    response_payload = struct.pack("!I", 0)
                elif operation == OP_SNAPSHOT_RESOLVE:
                    response_status = STATUS_NOT_FOUND
                elif operation == OP_SNAPSHOT_RELEASE:
                    response_status = STATUS_OK
                else:
                    response_status = STATUS_OK
                connection.sendall(
                    HEADER.pack(
                        MAGIC,
                        VERSION,
                        operation,
                        RESPONSE,
                        response_status,
                        len(response_payload),
                    )
                    + response_payload
                )


def wait_for_smb(process: subprocess.Popen[bytes]) -> None:
    for _ in range(200):
        if process.poll() is not None:
            raise RuntimeError(
                f"smbd exited with {process.returncode}:\n"
                f"{SMBD_LOG.read_text(encoding='utf-8', errors='replace')}"
            )
        try:
            with socket.create_connection(("127.0.0.1", 445), timeout=0.1):
                return
        except OSError:
            time.sleep(0.025)
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


def write_configuration() -> None:
    for name in ("state", "cache", "lock", "pid"):
        (ROOT / name).mkdir(mode=0o755)
    for name in ("private", "ncalrpc"):
        (ROOT / name).mkdir(mode=0o700)
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
                f"private dir = {ROOT / 'private'}",
                f"state directory = {ROOT / 'state'}",
                f"cache directory = {ROOT / 'cache'}",
                f"lock directory = {ROOT / 'lock'}",
                f"pid directory = {ROOT / 'pid'}",
                f"ncalrpc dir = {ROOT / 'ncalrpc'}",
                "",
                "[compatibility]",
                f"path = {SHARE_PATH}",
                "read only = no",
                "vfs objects = kaimo_bridge full_audit",
                "full_audit:syslog = no",
                "full_audit:priority = NOTICE",
                "full_audit:prefix = P2-15",
                "full_audit:success = "
                "connect disconnect openat close renameat unlinkat mkdirat",
                "full_audit:failure = "
                "connect openat renameat unlinkat mkdirat",
                "",
            ]
        ),
        encoding="utf-8",
    )


def main() -> int:
    # The authenticated Unix identity must be able to traverse to the share;
    # credentials and Samba-private state remain protected below this root.
    ROOT.mkdir(mode=0o755)
    SHARE_PATH.mkdir(mode=0o777)
    os.chmod(SHARE_PATH, 0o777)
    write_configuration()
    LOCAL_SOURCE.write_text("P2-15 operation compatibility\n", encoding="utf-8")
    AUTH_FILE.write_text(
        f"username = {USER}\npassword = {PASSWORD}\ndomain = WORKGROUP\n",
        encoding="utf-8",
    )
    os.chmod(AUTH_FILE, 0o600)

    run(["useradd", "-M", "-s", "/usr/sbin/nologin", USER])
    run(
        [
            "/opt/samba/bin/smbpasswd",
            "-c",
            str(CONFIG_PATH),
            "-s",
            "-a",
            USER,
        ],
        input=f"{PASSWORD}\n{PASSWORD}\n",
    )

    ready = threading.Event()
    stop = threading.Event()
    observed: list[int] = []
    server = threading.Thread(
        target=serve_authorization,
        args=(ready, stop, observed),
        daemon=True,
    )
    server.start()
    if not ready.wait(timeout=2):
        raise RuntimeError("fake authorization sidecar did not start")

    environment = os.environ.copy()
    environment["KAIMO_AUTHD_SOCK"] = str(SOCKET_PATH)
    smbd: subprocess.Popen[bytes] | None = None
    try:
        run(
            [
                "/opt/samba/bin/testparm",
                "--suppress-prompt",
                str(CONFIG_PATH),
            ],
            stdout=subprocess.DEVNULL,
        )
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
                # Docker build RUN steps have an already-closed stdin. smbd
                # treats EOF as a shutdown request even in foreground mode,
                # so retain an open private pipe for the test lifetime.
                stdin=subprocess.PIPE,
                stdout=log,
                stderr=subprocess.STDOUT,
                # smbd forwards shutdown within its process group. Keep the
                # build shell and this test runner outside that group.
                start_new_session=True,
            )
            wait_for_smb(smbd)
            command = (
                f"mkdir matrix; put {LOCAL_SOURCE} matrix/original.txt; "
                f"get matrix/original.txt {LOCAL_DOWNLOAD}; "
                "rename matrix/original.txt matrix/renamed.txt; "
                "del matrix/renamed.txt; rmdir matrix; ls"
            )
            result = subprocess.run(
                [
                    "/opt/samba/bin/smbclient",
                    "//127.0.0.1/compatibility",
                    "-p",
                    "445",
                    "--authentication-file",
                    str(AUTH_FILE),
                    "--configfile",
                    str(CONFIG_PATH),
                    "-m",
                    "SMB3",
                    "-c",
                    command,
                ],
                check=False,
                text=True,
                capture_output=True,
                timeout=20,
            )
            time.sleep(0.2)
    finally:
        terminate(smbd)
        stop.set()
        server.join(timeout=2)
        try:
            pwd.getpwnam(USER)
        except KeyError:
            pass
        else:
            subprocess.run(["userdel", USER], check=False)

    log_text = SMBD_LOG.read_text(encoding="utf-8", errors="replace")
    diagnostics = (
        f"smbclient stdout:\n{result.stdout}\n"
        f"smbclient stderr:\n{result.stderr}\n"
        f"observed local operations: {observed}\n"
        f"smbd:\n{log_text}"
    )
    assert result.returncode == 0, diagnostics
    assert LOCAL_DOWNLOAD.read_text(encoding="utf-8") == (
        "P2-15 operation compatibility\n"
    ), diagnostics
    assert not (SHARE_PATH / "matrix").exists(), diagnostics
    assert {OP_CONNECT, OP_OPEN, OP_DELETE_AUTH, OP_RENAME_AUTH}.issubset(
        observed
    ), diagnostics
    assert "kaimo_bridge build [" in log_text, diagnostics
    assert "Failed to load module" not in log_text, diagnostics
    assert "could not find opname" not in log_text.lower(), diagnostics
    missing_audit = {
        operation
        for operation in EXPECTED_AUDIT_OPERATIONS
        if f"P2-15|{operation}|ok|" not in log_text
    }
    assert not missing_audit, (
        f"missing successful full_audit records: {sorted(missing_audit)}\n"
        f"{diagnostics}"
    )
    print(
        "P2-15 live VFS/full_audit matrix passed: "
        + ", ".join(sorted(EXPECTED_AUDIT_OPERATIONS))
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    finally:
        if ROOT.exists():
            shutil.rmtree(ROOT, ignore_errors=True)
