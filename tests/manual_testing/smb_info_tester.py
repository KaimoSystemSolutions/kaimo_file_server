#!/usr/bin/env python3
"""
smb_info.py -- Zeigt die wichtigsten Infos eines SMB-Servers an
               und kann Freigaben optional testen + benchmarken.

Stufe 1 (immer):    roher SMB2-/SMB1-NEGOTIATE ohne Login
                    -> Dialekt/Version, Server-GUID, Signing, Capabilities,
                       Verschluesselung, Max-Groessen, Server-/Boot-Zeit.
Stufe 2 (optional): via smbprotocol -> Freigaben auflisten (SRVSVC),
                       Lese-/Schreib-/Loesch-Tests, Durchsatz-Benchmark.
                       Unterstuetzt SMB 2.0.2 bis 3.1.1 (inkl. SMB3-only-Server).

Aufruf per Kommandozeile ODER per Config:
    python3 smb_info.py 192.168.1.10 -u admin
    python3 smb_info.py --config config.json

CLI-Argumente ueberschreiben einzelne Werte aus der Config.
Stufe 2 braucht smbprotocol:  pip install smbprotocol
"""

import argparse
import getpass
import io
import json
import socket
import struct
import sys
import time
import uuid
from dataclasses import dataclass, field, asdict
from datetime import datetime, timedelta, timezone
from typing import Optional

# smbprotocol nur fuer Stufe 2 -> weich importieren.
try:
    from smbprotocol.connection import Connection
    from smbprotocol.session import Session
    from smbprotocol.tree import TreeConnect
    from smbprotocol.open import (
        Open, CreateDisposition, CreateOptions, ImpersonationLevel,
        ShareAccess, DirectoryAccessMask, FileAttributes, FilePipePrinterAccessMask,
    )
    from smbprotocol.file_info import FileInformationClass
    HAVE_SMB = True
except ImportError:
    HAVE_SMB = False


# ---------------------------------------------------------------------------
# Nachschlagetabellen
# ---------------------------------------------------------------------------

SMB2_DIALECTS_OFFERED = [0x0202, 0x0210, 0x0300, 0x0302, 0x0311]

# Capabilities, die dieser Client im NEGOTIATE-Request anbietet. LARGE_MTU (0x04) signalisiert
# dem Server, dass wir grosse Frames empfangen koennen. Ohne dieses Bit deckelt ein
# MS-SMB2-konformer Server (so wie diese Lib) Read/Write/Transact auf 64 KiB — der Test
# wuerde dann nicht die echte Obergrenze des Servers anzeigen, sondern nur den 64-KiB-Default.
CLIENT_CAPABILITIES = 0x04  # SMB2_GLOBAL_CAP_LARGE_MTU

DIALECT_NAMES = {
    0x0202: "SMB 2.0.2", 0x0210: "SMB 2.1", 0x0300: "SMB 3.0",
    0x0302: "SMB 3.0.2", 0x0311: "SMB 3.1.1",
    0x02FF: "SMB2 (Wildcard, >= 2.0.2)",
}
SMB2_CAPS = {
    0x01: "DFS", 0x02: "LEASING", 0x04: "LARGE_MTU", 0x08: "MULTI_CHANNEL",
    0x10: "PERSISTENT_HANDLES", 0x20: "DIRECTORY_LEASING", 0x40: "ENCRYPTION",
}
CIPHER_NAMES = {0x0001: "AES-128-CCM", 0x0002: "AES-128-GCM",
                0x0003: "AES-256-CCM", 0x0004: "AES-256-GCM"}
HASH_NAMES = {0x0001: "SHA-512"}
STYPE = {0: "Disk", 1: "Print-Queue", 2: "Device", 3: "IPC"}

# SRVSVC / NDR
SRVSVC_UUID = uuid.UUID("4b324fc8-1670-01d3-1278-5a47bf6ee188").bytes_le
NDR_UUID = uuid.UUID("8a885d04-1ceb-11c9-9fe8-08002b104860").bytes_le


# ---------------------------------------------------------------------------
# Datenmodelle
# ---------------------------------------------------------------------------

@dataclass
class RunOptions:
    timeout: float = 8.0
    test_share: str = ""
    test_all_shares: bool = False
    write_test: bool = False
    delete_test: bool = False
    benchmark: bool = False
    benchmark_size_kb: int = 8192
    verbose: bool = False


@dataclass
class SMBInfo:
    host: str = ""
    port: int = 0
    reachable: bool = False
    error: Optional[str] = None

    smb2_supported: bool = False
    smb1_supported: bool = False
    dialect_hex: Optional[str] = None
    dialect_name: Optional[str] = None
    server_guid: Optional[str] = None
    signing_enabled: Optional[bool] = None
    signing_required: Optional[bool] = None
    capabilities: list = field(default_factory=list)
    max_transact_size: Optional[int] = None
    max_read_size: Optional[int] = None
    max_write_size: Optional[int] = None
    server_time: Optional[str] = None
    server_boot_time: Optional[str] = None
    negotiated_cipher: Optional[str] = None
    negotiated_hash: Optional[str] = None

    shares: list = field(default_factory=list)
    shares_error: Optional[str] = None
    share_tests: list = field(default_factory=list)


# ---------------------------------------------------------------------------
# Hilfsfunktionen
# ---------------------------------------------------------------------------

def filetime_to_dt(ft: int) -> Optional[str]:
    if ft == 0:
        return None
    try:
        epoch = datetime(1601, 1, 1, tzinfo=timezone.utc)
        return (epoch + timedelta(microseconds=ft // 10)).isoformat()
    except (OverflowError, OSError):
        return None


def _align8(n: int) -> int:
    return (n + 7) & ~7


def _recv_exact(sock: socket.socket, n: int) -> bytes:
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("Verbindung vom Server geschlossen")
        buf += chunk
    return buf


def _recv_packet(sock: socket.socket) -> bytes:
    head = _recv_exact(sock, 4)
    length = struct.unpack(">I", head)[0] & 0x00FFFFFF
    return _recv_exact(sock, length)


# ---------------------------------------------------------------------------
# Stufe 1: SMB2-/SMB1-NEGOTIATE (ohne Login, ohne Fremd-Pakete)
# ---------------------------------------------------------------------------

def build_smb2_negotiate() -> bytes:
    header = struct.pack(
        "<4sHHIHHIIQIIQ16s",
        b"\xfeSMB", 64, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, b"\x00" * 16,
    )
    dialects = SMB2_DIALECTS_OFFERED
    client_guid = uuid.uuid4().bytes

    pos_after_dialects = 64 + 36 + 2 * len(dialects)
    pad = _align8(pos_after_dialects) - pos_after_dialects
    neg_ctx_offset = pos_after_dialects + pad

    salt = uuid.uuid4().bytes + uuid.uuid4().bytes
    pa = struct.pack("<HH", 1, 32) + struct.pack("<H", 0x0001) + salt
    ctx1 = struct.pack("<HHI", 0x0001, len(pa), 0) + pa
    ctx1 += b"\x00" * (_align8(len(ctx1)) - len(ctx1))
    en = struct.pack("<H", 2) + struct.pack("<HH", 0x0001, 0x0002)
    ctx2 = struct.pack("<HHI", 0x0002, len(en), 0) + en

    body = struct.pack(
        "<HHHHI16sIHH",
        36, len(dialects), 0x0001, 0, CLIENT_CAPABILITIES, client_guid, neg_ctx_offset, 2, 0,
    )
    body += struct.pack("<" + "H" * len(dialects), *dialects)
    body += b"\x00" * pad + ctx1 + ctx2

    msg = header + body
    return struct.pack(">I", len(msg)) + msg


def parse_smb2_negotiate(data: bytes, info: SMBInfo) -> None:
    if len(data) < 64 or data[:4] != b"\xfeSMB":
        raise ValueError("Keine gueltige SMB2-Antwort")
    status = struct.unpack_from("<I", data, 8)[0]
    if status != 0:
        raise ValueError(f"NEGOTIATE fehlgeschlagen, Status 0x{status:08x}")

    _, sec_mode, dialect, ctx_count = struct.unpack_from("<HHHH", data, 64)
    server_guid = data[72:88]
    caps, max_trans, max_read, max_write = struct.unpack_from("<IIII", data, 88)
    sys_time, boot_time = struct.unpack_from("<QQ", data, 104)
    neg_ctx_off = struct.unpack_from("<I", data, 124)[0]

    info.smb2_supported = True
    info.dialect_hex = f"0x{dialect:04x}"
    info.dialect_name = DIALECT_NAMES.get(dialect, f"unbekannt (0x{dialect:04x})")
    info.server_guid = str(uuid.UUID(bytes_le=server_guid))
    info.signing_enabled = bool(sec_mode & 0x0001)
    info.signing_required = bool(sec_mode & 0x0002)
    info.capabilities = [n for b, n in SMB2_CAPS.items() if caps & b]
    info.max_transact_size = max_trans
    info.max_read_size = max_read
    info.max_write_size = max_write
    info.server_time = filetime_to_dt(sys_time)
    info.server_boot_time = filetime_to_dt(boot_time)

    off = neg_ctx_off
    for _ in range(ctx_count):
        if off + 8 > len(data):
            break
        ctype, dlen = struct.unpack_from("<HH", data, off)
        cdata = data[off + 8: off + 8 + dlen]
        if ctype == 0x0002 and len(cdata) >= 4:
            cipher = struct.unpack_from("<H", cdata, 2)[0]
            info.negotiated_cipher = CIPHER_NAMES.get(cipher, f"0x{cipher:04x}")
        elif ctype == 0x0001 and len(cdata) >= 6:
            algo = struct.unpack_from("<H", cdata, 4)[0]
            info.negotiated_hash = HASH_NAMES.get(algo, f"0x{algo:04x}")
        off = _align8(off + 8 + dlen)


def build_smb1_negotiate() -> bytes:
    header = struct.pack(
        "<4sBIBHH8sHHHHH",
        b"\xffSMB", 0x72, 0, 0x18, 0xC853, 0, b"\x00" * 8, 0, 0, 0xFEFF, 0, 0,
    )
    dialects = [b"PC NETWORK PROGRAM 1.0", b"LANMAN1.0", b"NT LM 0.12"]
    buf = b"".join(b"\x02" + d + b"\x00" for d in dialects)
    body = struct.pack("<B", 0) + struct.pack("<H", len(buf)) + buf
    msg = header + body
    return struct.pack(">I", len(msg)) + msg


def probe_smb1(host: str, port: int, timeout: float) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout) as s:
            s.settimeout(timeout)
            s.sendall(build_smb1_negotiate())
            data = _recv_packet(s)
            if len(data) < 35 or data[:4] != b"\xffSMB":
                return False
            if data[32] == 0:
                return False
            return struct.unpack_from("<H", data, 33)[0] != 0xFFFF
    except (OSError, ConnectionError, struct.error):
        return False


def gather_negotiate(host: str, port: int, timeout: float) -> SMBInfo:
    info = SMBInfo(host=host, port=port)
    try:
        with socket.create_connection((host, port), timeout=timeout) as s:
            s.settimeout(timeout)
            s.sendall(build_smb2_negotiate())
            data = _recv_packet(s)
            info.reachable = True
            parse_smb2_negotiate(data, info)
    except (OSError, ConnectionError, ValueError, struct.error) as e:
        if not info.reachable:
            info.error = f"Nicht erreichbar / kein SMB2: {e}"
            return info
        info.error = f"SMB2-NEGOTIATE-Fehler: {e}"

    info.smb1_supported = probe_smb1(host, port, timeout)
    return info


# ---------------------------------------------------------------------------
# Stufe 2: SRVSVC NetrShareEnum (Freigaben auflisten) ueber IPC$
# ---------------------------------------------------------------------------

def _dcerpc_bind() -> bytes:
    ctx = (struct.pack("<HBx", 0, 1) + SRVSVC_UUID + struct.pack("<HH", 3, 0)
           + NDR_UUID + struct.pack("<HH", 2, 0))
    body = struct.pack("<HHI", 4280, 4280, 0) + struct.pack("<Bxxx", 1) + ctx
    hdr = struct.pack("<BBBB4sHHI", 5, 0, 11, 0x03, b"\x10\x00\x00\x00", 0, 0, 1)
    pdu = hdr + body
    return pdu[:8] + struct.pack("<H", len(pdu)) + pdu[10:]


def _ndr_unique_wstr(s: str, ref: int) -> bytes:
    data = (s + "\x00").encode("utf-16-le")
    n = len(s) + 1
    out = struct.pack("<IIII", ref, n, 0, n) + data
    out += b"\x00" * ((4 - (len(out) % 4)) % 4)
    return out


def _netr_share_enum_req(server: str) -> bytes:
    stub = _ndr_unique_wstr("\\\\" + server, 0x00020000)
    stub += struct.pack("<I", 1)            # Level 1
    stub += struct.pack("<I", 1)            # union switch
    stub += struct.pack("<I", 0x00020004)   # container referent
    stub += struct.pack("<I", 0)            # EntriesRead
    stub += struct.pack("<I", 0)            # Buffer = NULL
    stub += struct.pack("<I", 0xFFFFFFFF)   # PreferedMaximumLength
    stub += struct.pack("<I", 0x00020008)   # ResumeHandle referent
    stub += struct.pack("<I", 0)            # ResumeHandle value
    hdr = struct.pack("<BBBB4sHHI", 5, 0, 0, 0x03, b"\x10\x00\x00\x00", 0, 0, 1)
    body = struct.pack("<IHH", len(stub), 0, 15) + stub  # alloc_hint, ctx_id, opnum=15
    pdu = hdr + body
    return pdu[:8] + struct.pack("<H", len(pdu)) + pdu[10:]


def _parse_share_enum(data: bytes) -> list:
    off = 24  # DCERPC-Response-Header
    shares = []

    def u32():
        nonlocal off
        v = struct.unpack_from("<I", data, off)[0]
        off += 4
        return v

    _level = u32(); _switch = u32(); _cont_ref = u32()
    _entries = u32(); buf_ref = u32()
    if buf_ref == 0:
        return shares

    max_count = u32()
    fixed = []
    for _ in range(max_count):
        netname_ptr = u32(); stype = u32(); remark_ptr = u32()
        fixed.append((netname_ptr, stype, remark_ptr))

    def rd_str():
        nonlocal off
        mx, offs, ac = struct.unpack_from("<III", data, off)
        off += 12
        s = data[off:off + ac * 2].decode("utf-16-le", "replace").rstrip("\x00")
        off += ac * 2
        off += (4 - (off % 4)) % 4
        return s

    for netname_ptr, stype, remark_ptr in fixed:
        name = rd_str() if netname_ptr else ""
        remark = rd_str() if remark_ptr else ""
        shares.append({
            "name": name,
            "type": STYPE.get(stype & 0xFF, str(stype & 0xFF)),
            "hidden": bool(stype & 0x80000000) or name.endswith("$"),
            "comment": remark,
        })
    return shares


def enum_shares(sess, server: str, timeout: float) -> list:
    ipc = TreeConnect(sess, r"\\%s\IPC$" % server)
    ipc.connect()
    pipe = Open(ipc, "srvsvc")
    try:
        pipe.create(
            ImpersonationLevel.Impersonation,
            FilePipePrinterAccessMask.FILE_READ_DATA
            | FilePipePrinterAccessMask.FILE_WRITE_DATA
            | FilePipePrinterAccessMask.FILE_APPEND_DATA,
            0,
            ShareAccess.FILE_SHARE_READ | ShareAccess.FILE_SHARE_WRITE,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
        )
        pipe.write(_dcerpc_bind(), 0)
        pipe.read(0, 4096)  # bind_ack
        pipe.write(_netr_share_enum_req(server), 0)
        resp = pipe.read(0, 1 << 20)
        return _parse_share_enum(resp)
    finally:
        try:
            pipe.close()
        except Exception:
            pass
        try:
            ipc.disconnect()
        except Exception:
            pass


# ---------------------------------------------------------------------------
# Stufe 2: Datei-Operationen / Tests / Benchmark
# ---------------------------------------------------------------------------

def _write_all(handle, data: bytes, chunk: int = 1 << 20) -> None:
    off = 0
    while off < len(data):
        part = data[off:off + chunk]
        handle.write(part, off)
        off += len(part)


def _read_all(handle, length: int, chunk: int = 1 << 20) -> bytes:
    out = bytearray()
    off = 0
    while off < length:
        d = handle.read(off, min(chunk, length - off))
        if not d:
            break
        out += d
        off += len(d)
    return bytes(out)


def _open_dir(tree):
    d = Open(tree, "")
    d.create(
        ImpersonationLevel.Impersonation,
        DirectoryAccessMask.FILE_LIST_DIRECTORY | DirectoryAccessMask.FILE_READ_ATTRIBUTES,
        FileAttributes.FILE_ATTRIBUTE_DIRECTORY,
        ShareAccess.FILE_SHARE_READ | ShareAccess.FILE_SHARE_WRITE | ShareAccess.FILE_SHARE_DELETE,
        CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE,
    )
    return d


def _write_file(tree, name: str, data: bytes):
    f = Open(tree, name)
    f.create(
        ImpersonationLevel.Impersonation,
        FilePipePrinterAccessMask.FILE_WRITE_DATA | FilePipePrinterAccessMask.DELETE,
        FileAttributes.FILE_ATTRIBUTE_NORMAL,
        ShareAccess.FILE_SHARE_READ | ShareAccess.FILE_SHARE_WRITE | ShareAccess.FILE_SHARE_DELETE,
        CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE,
    )
    _write_all(f, data)
    f.close()


def _read_file(tree, name: str, length: int) -> bytes:
    f = Open(tree, name)
    f.create(
        ImpersonationLevel.Impersonation,
        FilePipePrinterAccessMask.FILE_READ_DATA,
        FileAttributes.FILE_ATTRIBUTE_NORMAL,
        ShareAccess.FILE_SHARE_READ | ShareAccess.FILE_SHARE_WRITE | ShareAccess.FILE_SHARE_DELETE,
        CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE,
    )
    data = _read_all(f, length)
    f.close()
    return data


def _delete_file(tree, name: str) -> None:
    f = Open(tree, name)
    f.create(
        ImpersonationLevel.Impersonation,
        FilePipePrinterAccessMask.DELETE,
        FileAttributes.FILE_ATTRIBUTE_NORMAL,
        ShareAccess.FILE_SHARE_READ | ShareAccess.FILE_SHARE_WRITE | ShareAccess.FILE_SHARE_DELETE,
        CreateDisposition.FILE_OPEN,
        CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_NON_DIRECTORY_FILE,
    )
    f.close()


def _test_one_share(sess, server: str, share: str, opts: RunOptions) -> dict:
    r = {
        "name": share, "readable": None, "items": None, "read_error": None,
        "writable": None, "write_error": None,
        "deletable": None, "delete_error": None,
        "upload_mbps": None, "download_mbps": None,
        "benchmark_bytes": None, "benchmark_error": None,
    }
    tree = TreeConnect(sess, r"\\%s\%s" % (server, share))
    try:
        tree.connect()
    except Exception as e:  # noqa: BLE001
        r["readable"] = False
        r["read_error"] = f"{type(e).__name__}: {e}"
        return r

    # --- Lese-Test ---
    try:
        d = _open_dir(tree)
        entries = d.query_directory("*", FileInformationClass.FILE_NAMES_INFORMATION)
        names = [e["file_name"].get_value().decode("utf-16-le") for e in entries]
        d.close()
        r["readable"] = True
        r["items"] = sum(1 for n in names if n not in (".", ".."))
    except Exception as e:  # noqa: BLE001
        r["readable"] = False
        r["read_error"] = f"{type(e).__name__}: {e}"

    # --- Schreib-/Loesch-Test ---
    if opts.write_test:
        name = "__smbinfo_w_%s.tmp" % uuid.uuid4().hex[:8]
        try:
            _write_file(tree, name, b"smbinfo write-test\n")
            r["writable"] = True
            if opts.delete_test:
                try:
                    _delete_file(tree, name)
                    r["deletable"] = True
                except Exception as e:  # noqa: BLE001
                    r["deletable"] = False
                    r["delete_error"] = f"{type(e).__name__}: {e}"
            else:
                try:
                    _delete_file(tree, name)  # nichts liegen lassen
                except Exception:
                    pass
        except Exception as e:  # noqa: BLE001
            r["writable"] = False
            r["write_error"] = f"{type(e).__name__}: {e}"

    # --- Benchmark ---
    if opts.benchmark:
        size = max(1, opts.benchmark_size_kb) * 1024
        r["benchmark_bytes"] = size
        payload = b"\xA5" * size
        name = "__smbinfo_bench_%s.bin" % uuid.uuid4().hex[:8]
        try:
            t0 = time.perf_counter()
            _write_file(tree, name, payload)
            up = time.perf_counter() - t0

            t0 = time.perf_counter()
            _read_file(tree, name, size)
            down = time.perf_counter() - t0

            try:
                _delete_file(tree, name)
            except Exception:
                pass

            mb = size / 1_000_000
            r["upload_mbps"] = mb / up if up > 0 else None
            r["download_mbps"] = mb / down if down > 0 else None
        except Exception as e:  # noqa: BLE001
            r["benchmark_error"] = f"{type(e).__name__}: {e}"

    try:
        tree.disconnect()
    except Exception:
        pass
    return r


def gather_shares(info: SMBInfo, username: str, password: str,
                  domain: str, opts: RunOptions) -> None:
    if not HAVE_SMB:
        info.shares_error = "smbprotocol nicht installiert (pip install smbprotocol)"
        return

    conn = Connection(uuid.uuid4(), info.host, info.port)
    sess = None
    try:
        conn.connect(timeout=opts.timeout)
        # Domaene wird (falls gesetzt) als DOMAIN\\user uebergeben
        user = ("%s\\%s" % (domain, username)) if domain and "\\" not in username else username
        sess = Session(conn, user, password)
        sess.connect()
    except Exception as e:  # noqa: BLE001
        info.shares_error = f"Verbindung/Login fehlgeschlagen: {type(e).__name__}: {e}"
        try:
            conn.disconnect()
        except Exception:
            pass
        return

    # Freigaben auflisten (best effort)
    try:
        info.shares = enum_shares(sess, info.host, opts.timeout)
    except Exception as e:  # noqa: BLE001
        info.shares_error = f"Shares nicht auflistbar (SRVSVC): {type(e).__name__}: {e}"

    # Welche Shares testen?
    targets = []
    if opts.test_share:
        targets = [opts.test_share]
    elif opts.test_all_shares:
        targets = [s["name"] for s in info.shares
                   if s["type"] == "Disk" and not s["name"].endswith("$")]

    for share in targets:
        info.share_tests.append(_test_one_share(sess, info.host, share, opts))

    try:
        sess.disconnect()
    except Exception:
        pass
    try:
        conn.disconnect()
    except Exception:
        pass


# ---------------------------------------------------------------------------
# Ausgabe
# ---------------------------------------------------------------------------

def _yn(v: Optional[bool]) -> str:
    return {True: "ja", False: "nein", None: "?"}[v]


def print_report(info: SMBInfo, opts: RunOptions) -> None:
    line = "=" * 60
    print(line)
    print(f"  SMB-Server: {info.host}:{info.port}")
    print(line)

    if not info.reachable:
        print(f"  [!] {info.error}")
        return

    print("\n  PROTOKOLL")
    print(f"    SMB2/3 unterstuetzt : {_yn(info.smb2_supported)}")
    print(f"    SMBv1 aktiv         : {_yn(info.smb1_supported)}"
          + ("   <-- veraltet/unsicher!" if info.smb1_supported else ""))
    if info.dialect_name:
        print(f"    Dialekt (max.)      : {info.dialect_name} ({info.dialect_hex})")

    if info.smb2_supported:
        print("\n  SICHERHEIT")
        print(f"    Signing aktiviert   : {_yn(info.signing_enabled)}")
        print(f"    Signing erzwungen   : {_yn(info.signing_required)}")
        if info.negotiated_cipher:
            print(f"    Verschluesselung    : {info.negotiated_cipher}")
        if info.negotiated_hash:
            print(f"    Preauth-Hash        : {info.negotiated_hash}")

        print("\n  SERVER")
        print(f"    Server-GUID         : {info.server_guid}")
        print(f"    Server-Uhrzeit      : {info.server_time or '?'}")
        print(f"    Boot-Zeit           : {info.server_boot_time or '?'}")
        if info.capabilities:
            print(f"    Capabilities        : {', '.join(info.capabilities)}")

        print("\n  LIMITS")
        print(f"    Max. Transact-Size  : {info.max_transact_size:,} Bytes")
        print(f"    Max. Read-Size      : {info.max_read_size:,} Bytes")
        print(f"    Max. Write-Size     : {info.max_write_size:,} Bytes")

    print("\n  FREIGABEN")
    if info.shares_error:
        print(f"    [-] {info.shares_error}")
    if info.shares:
        for sh in info.shares:
            tag = " [versteckt]" if sh["hidden"] else ""
            comment = f"  -- {sh['comment']}" if sh["comment"] else ""
            print(f"    {sh['name']:<20} {sh['type']:<12}{tag}{comment}")
    elif not info.shares_error:
        print("    (keine gefunden oder nicht abgefragt)")

    if info.share_tests:
        print("\n  SHARE-TESTS")
        for r in info.share_tests:
            print(f"    [{r['name']}]")
            extra = f"  ({r['items']} Eintraege)" if r["items"] is not None else ""
            rd = _yn(r["readable"]) + extra
            if r["read_error"] and opts.verbose:
                rd += f"  -> {r['read_error']}"
            print(f"       lesbar     : {rd}")
            if r["writable"] is not None:
                wl = _yn(r["writable"])
                if r["write_error"]:
                    wl += f"  ({r['write_error']})"
                print(f"       schreibbar : {wl}")
            if r["deletable"] is not None:
                dl = _yn(r["deletable"])
                if r["delete_error"]:
                    dl += f"  ({r['delete_error']})"
                print(f"       loeschbar  : {dl}")
            if r["upload_mbps"] is not None or r["download_mbps"] is not None:
                up = f"{r['upload_mbps']:.1f}" if r["upload_mbps"] else "?"
                dn = f"{r['download_mbps']:.1f}" if r["download_mbps"] else "?"
                sz = f" ({r['benchmark_bytes'] // 1024} KB)" if opts.verbose and r["benchmark_bytes"] else ""
                print(f"       Benchmark  : Upload {up} MB/s | Download {dn} MB/s{sz}")
            elif r["benchmark_error"]:
                print(f"       Benchmark  : Fehler ({r['benchmark_error']})")

    print("\n" + line)


# ---------------------------------------------------------------------------
# Config + Main
# ---------------------------------------------------------------------------

def load_config(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def main() -> int:
    p = argparse.ArgumentParser(
        description="Zeigt die wichtigsten Infos eines SMB-Servers an "
                    "und testet optional die Freigaben (SMB 2.0.2 - 3.1.1)."
    )
    p.add_argument("host", nargs="?", help="Host/IP (oder via --config 'ip')")
    p.add_argument("-c", "--config", help="Pfad zu einer JSON-Config")
    p.add_argument("-p", "--port", type=int, default=None)
    p.add_argument("-u", "--user", default=None, help="Benutzername (config: username)")
    p.add_argument("-d", "--domain", default=None)
    p.add_argument("-P", "--password", default=None,
                   help="Passwort (sonst Abfrage, wenn ein User gesetzt ist)")
    p.add_argument("-t", "--timeout", type=float, default=None)
    p.add_argument("--test-share", default=None, help="Nur diese eine Freigabe testen")
    p.add_argument("--test-all-shares", action="store_const", const=True, default=None,
                   help="Alle Disk-Freigaben testen")
    p.add_argument("--write-test", action="store_const", const=True, default=None)
    p.add_argument("--delete-test", action="store_const", const=True, default=None)
    p.add_argument("--benchmark", action="store_const", const=True, default=None)
    p.add_argument("--benchmark-size-kb", type=int, default=None)
    p.add_argument("--verbose", action="store_const", const=True, default=None)
    p.add_argument("--no-shares", action="store_true", help="Stufe 2 ueberspringen")
    p.add_argument("--anonymous", action="store_true",
                   help="Freigaben anonym/als Gast abfragen")
    p.add_argument("--json", action="store_true", help="Ausgabe als JSON")
    args = p.parse_args()

    cfg = {}
    if args.config:
        try:
            cfg = load_config(args.config)
        except (OSError, json.JSONDecodeError) as e:
            print(f"  [!] Config konnte nicht geladen werden: {e}")
            return 2

    def pick(cli_val, key, default):
        if cli_val is not None:
            return cli_val
        if key in cfg and cfg[key] is not None:
            return cfg[key]
        return default

    host = args.host or cfg.get("ip")
    if not host:
        print("  [!] Kein Host angegeben (Argument 'host' oder --config mit 'ip').")
        return 2

    port = pick(args.port, "port", 445)
    user = pick(args.user, "username", "")
    domain = pick(args.domain, "domain", "")
    timeout = float(pick(args.timeout, "timeout", 8.0))
    password = args.password if args.password is not None else cfg.get("password")

    opts = RunOptions(
        timeout=timeout,
        test_share=pick(args.test_share, "test_share", ""),
        test_all_shares=bool(pick(args.test_all_shares, "test_all_shares", False)),
        write_test=bool(pick(args.write_test, "write_test", False)),
        delete_test=bool(pick(args.delete_test, "delete_test", False)),
        benchmark=bool(pick(args.benchmark, "benchmark", False)),
        benchmark_size_kb=int(pick(args.benchmark_size_kb, "benchmark_size_kb", 8192)),
        verbose=bool(pick(args.verbose, "verbose", False)),
    )

    info = gather_negotiate(host, port, timeout)

    needs_conn = (bool(user) or args.anonymous or opts.test_all_shares
                  or bool(opts.test_share) or opts.write_test or opts.benchmark)
    if not args.no_shares and needs_conn and info.reachable:
        if user and password is None:
            password = getpass.getpass(f"Passwort fuer {user}: ")
        gather_shares(info, user, password or "", domain, opts)
    elif not args.no_shares and not needs_conn:
        info.shares_error = "uebersprungen (kein User/--anonymous/Test angegeben)"

    if args.json:
        print(json.dumps(asdict(info), indent=2, ensure_ascii=False))
    else:
        print_report(info, opts)

    return 0 if info.reachable else 1


if __name__ == "__main__":
    sys.exit(main())