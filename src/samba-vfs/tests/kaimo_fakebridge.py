# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""Scriptable stand-in for the .NET SmbBridge gRPC server (mTLS).

Lets the real C++ sidecars (kaimo_authd, kaimo_authsync, kaimo_sharesync,
kaimo_configsync) run end to end without .NET:

* ``make_pki`` runs the production ``generate-control-plane-certs.sh`` and
  additionally issues a ``localhost`` server identity from the same CA.
* ``FakeBridge`` serves every RPC of ``protos/kaimo_smb_bridge.proto`` through
  generic handlers (no grpc_tools codegen needed, only ``protoc --python_out``).
  Handlers are plain callables ``(request, context) -> reply``; they may call
  ``context.abort`` or set trailing metadata. Every call is recorded.
"""

from __future__ import annotations

import dataclasses
import importlib
import os
import subprocess
import sys
import threading
from concurrent import futures
from pathlib import Path
from typing import Callable

import grpc

HERE = Path(__file__).resolve().parent
PROTO = Path(os.environ.get("KAIMO_BRIDGE_PROTO", HERE.parent / "protos" / "kaimo_smb_bridge.proto"))


def load_messages(out_dir: Path):
    """Compile the contract with the system protoc and import the _pb2 module."""
    out_dir.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        ["protoc", f"-I{PROTO.parent}", f"--python_out={out_dir}", str(PROTO)],
        check=True,
    )
    if str(out_dir) not in sys.path:
        sys.path.insert(0, str(out_dir))
    return importlib.import_module("kaimo_smb_bridge_pb2")


@dataclasses.dataclass
class Pki:
    root: Path
    ca: Path
    client_cert: Path
    client_key: Path
    server_cert: Path
    server_key: Path

    def client_env(self, address: str) -> dict[str, str]:
        env = {"KAIMO_BRIDGE_ADDR": address, "KAIMO_BRIDGE_CA_CERT": str(self.ca)}
        for prefix in ("RUNTIME", "AUTH_SYNC", "SHARE_SYNC", "CONFIG_SYNC"):
            env[f"KAIMO_BRIDGE_{prefix}_CERT"] = str(self.client_cert)
            env[f"KAIMO_BRIDGE_{prefix}_KEY"] = str(self.client_key)
        return env


def make_pki(root: Path, generator: Path) -> Pki:
    subprocess.run(["bash", str(generator), str(root)], check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    authority = root / "authority"
    server = root / "localhost"
    server.mkdir(mode=0o700, exist_ok=True)
    (server / "server.ext").write_text(
        "basicConstraints=critical,CA:FALSE\n"
        "keyUsage=critical,digitalSignature,keyEncipherment\n"
        "extendedKeyUsage=serverAuth\n"
        "subjectAltName=DNS:localhost,IP:127.0.0.1\n"
    )
    subprocess.run(
        ["openssl", "req", "-new", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=localhost",
         "-keyout", str(server / "server.key"), "-out", str(server / "server.csr")],
        check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    subprocess.run(
        ["openssl", "x509", "-req", "-sha256", "-days", "2",
         "-in", str(server / "server.csr"), "-CA", str(authority / "ca.crt"),
         "-CAkey", str(authority / "ca.key"), "-CAcreateserial",
         "-extfile", str(server / "server.ext"), "-out", str(server / "server.crt")],
        check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    return Pki(
        root=root,
        ca=root / "samba" / "ca.crt",
        client_cert=root / "samba" / "samba.crt",
        client_key=root / "samba" / "samba.key",
        server_cert=server / "server.crt",
        server_key=server / "server.key",
    )


Handler = Callable[[object, grpc.ServicerContext], object]


class FakeBridge:
    def __init__(self, messages, pki: Pki) -> None:
        self.pb = messages
        self.pki = pki
        self.calls: list[tuple[str, object]] = []
        self._handlers: dict[str, Handler] = {}
        self._lock = threading.Lock()
        self._server: grpc.Server | None = None
        self.port = 0

    # -- scripting ---------------------------------------------------------

    def on(self, method: str, handler: Handler | object) -> None:
        """``handler`` is a callable or a fixed reply message."""
        if not callable(handler):
            reply = handler
            handler = lambda _request, _context: reply  # noqa: E731
        with self._lock:
            self._handlers[method] = handler

    def on_sequence(self, method: str, handlers: list) -> None:
        pending = list(handlers)

        def dispatch(request, context):
            item = pending.pop(0) if len(pending) > 1 else pending[0]
            return item(request, context) if callable(item) else item

        self.on(method, dispatch)

    def reset(self) -> None:
        with self._lock:
            self._handlers.clear()
            self.calls.clear()

    def requests(self, method: str) -> list:
        with self._lock:
            return [request for name, request in self.calls if name == method]

    # -- lifecycle ---------------------------------------------------------

    @property
    def address(self) -> str:
        return f"localhost:{self.port}"

    def start(self) -> "FakeBridge":
        server = grpc.server(futures.ThreadPoolExecutor(max_workers=16))
        for service in self.pb.DESCRIPTOR.services_by_name.values():
            methods = {}
            for method in service.methods:
                request_type = getattr(self.pb, method.input_type.name)
                reply_type = getattr(self.pb, method.output_type.name)
                methods[method.name] = grpc.unary_unary_rpc_method_handler(
                    self._dispatcher(method.name, reply_type),
                    request_deserializer=request_type.FromString,
                    response_serializer=reply_type.SerializeToString,
                )
            server.add_generic_rpc_handlers(
                (grpc.method_handlers_generic_handler(service.full_name, methods),)
            )
        credentials = grpc.ssl_server_credentials(
            [(self.pki.server_key.read_bytes(), self.pki.server_cert.read_bytes())],
            root_certificates=(self.pki.root / "authority" / "ca.crt").read_bytes(),
            require_client_auth=True,
        )
        self.port = server.add_secure_port("127.0.0.1:0", credentials)
        server.start()
        self._server = server
        return self

    def stop(self) -> None:
        if self._server is not None:
            self._server.stop(grace=None)
            self._server = None

    def _dispatcher(self, name: str, reply_type):
        def handle(request, context):
            with self._lock:
                self.calls.append((name, request))
                handler = self._handlers.get(name)
            if handler is None:
                return self.default_reply(name, reply_type)
            return handler(request, context)

        return handle

    def default_reply(self, name: str, reply_type):
        pb = self.pb
        if name.startswith("Authorize"):
            return pb.AuthorizeReply(allow=True, granted_access_mask=0x00120089)
        if name.startswith("Notify"):
            return pb.NotifyReply(ok=True)
        if name == "GetProtocolSettings":
            return pb.ProtocolSettingsReply(
                min_protocol="SMB2_10", max_protocol="SMB3_11", enabled=True,
                log_level="Warning",
            )
        if name == "ReleaseVersionLease":
            return pb.ReleaseVersionLeaseReply(released=True)
        return reply_type()
