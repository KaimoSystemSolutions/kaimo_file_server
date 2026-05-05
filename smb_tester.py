#!/usr/bin/env python3
"""
╔══════════════════════════════════════════════════════════════╗
║                    SMB SERVER TESTER                        ║
║          Comprehensive SMB Share Testing Suite              ║
║                                                              ║
║  Abhängigkeit:  pip install pysmb                           ║
╚══════════════════════════════════════════════════════════════╝

Testet dynamisch alle Aspekte eines SMB-Servers:
  - Verbindung & Authentifizierung
  - Share-Enumeration (alle Shares auflisten)
  - Ordner-/Datei-Zugriff (Read / Write / Delete)
  - Berechtigungsprüfung pro Share
  - Sonderzeichen in Dateinamen
  - Rekursive Verzeichnis-Traversierung
  - Performance-Benchmark (Upload / Download Speed)
  - Simultaner Dateizugriff
  - Gastzugang-Check

Verwendung:
  python smb_tester.py                                # Interaktiv
  python smb_tester.py --ip 10.0.0.5 --user admin    # CLI
  python smb_tester.py --config config.json           # Config-Datei
"""

import argparse
import getpass
import io
import json
import os
import socket
import sys
import time
import traceback
import uuid
from dataclasses import asdict, dataclass, field
from datetime import datetime
from typing import Optional

# ─── Dependency Check ────────────────────────────────────────
try:
    from smb.SMBConnection import SMBConnection
    from smb.smb_structs import OperationFailure
except ImportError:
    print()
    print("  ╔═══════════════════════════════════════════════╗")
    print("  ║  Fehlende Abhängigkeit: pysmb                ║")
    print("  ║                                               ║")
    print("  ║  Installation:                                ║")
    print("  ║    pip install pysmb                          ║")
    print("  ╚═══════════════════════════════════════════════╝")
    print()
    sys.exit(1)


# ═══════════════════════════════════════════════════════════════
# Datenklassen
# ═══════════════════════════════════════════════════════════════

@dataclass
class TestResult:
    name: str
    category: str
    status: str = "PENDING"  # PASS, FAIL, WARN, SKIP
    message: str = ""
    duration_ms: float = 0.0
    details: dict = field(default_factory=dict)

    @property
    def icon(self):
        return {
            "PASS": "✅", "FAIL": "❌", "WARN": "⚠️",
            "SKIP": "⏭️", "PENDING": "⏳",
        }.get(self.status, "❓")


@dataclass
class SMBConfig:
    ip: str = ""
    port: int = 445
    username: str = ""
    password: str = ""
    domain: str = ""
    client_name: str = ""           # NetBIOS-Name des Clients
    server_name: str = ""           # NetBIOS-Name des Servers
    test_share: str = ""            # Spezifischer Share
    test_all_shares: bool = True
    write_test: bool = True
    delete_test: bool = True
    benchmark: bool = True
    benchmark_size_kb: int = 1024
    timeout: int = 30
    verbose: bool = False


# ═══════════════════════════════════════════════════════════════
# Haupt-Tester
# ═══════════════════════════════════════════════════════════════

class SMBTester:
    def __init__(self, config: SMBConfig):
        self.config = config
        self.results: list[TestResult] = []
        self.conn: Optional[SMBConnection] = None
        self.discovered_shares: list[str] = []
        self._prefix = f"_smbtest_{uuid.uuid4().hex[:8]}"

        # Client-Name generieren falls nicht gesetzt
        if not self.config.client_name:
            self.config.client_name = f"SMBTEST-{uuid.uuid4().hex[:6].upper()}"
        if not self.config.server_name:
            self.config.server_name = self.config.ip

    # ─── Hilfsfunktionen ──────────────────────────────────────

    def _log(self, msg: str, level: str = "INFO"):
        if self.config.verbose or level in ("ERROR", "WARN"):
            ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
            print(f"  [{ts}] [{level}] {msg}")

    def _run_test(self, name, category, func, *args, **kwargs) -> TestResult:
        result = TestResult(name=name, category=category)
        t0 = time.perf_counter()
        try:
            func(result, *args, **kwargs)
        except OperationFailure as e:
            result.status = "FAIL"
            result.message = f"SMB-Fehler: {e}"
            self._log(f"{name}: {e}", "ERROR")
        except PermissionError as e:
            result.status = "FAIL"
            result.message = f"Zugriff verweigert: {e}"
        except Exception as e:
            result.status = "FAIL"
            result.message = f"{type(e).__name__}: {e}"
            if self.config.verbose:
                traceback.print_exc()
        finally:
            result.duration_ms = (time.perf_counter() - t0) * 1000
            self.results.append(result)
            suffix = f" — {result.message}" if result.message else ""
            print(f"  {result.icon}  {name}: {result.status} "
                  f"({result.duration_ms:.0f}ms){suffix}")
        return result

    def _new_connection(self, username=None, password=None) -> SMBConnection:
        """Erstellt eine neue SMBConnection."""
        conn = SMBConnection(
            username or self.config.username,
            password or self.config.password,
            self.config.client_name,
            self.config.server_name,
            domain=self.config.domain,
            use_ntlm_v2=True,
            is_direct_tcp=True,
        )
        return conn

    # ─── 1. Netzwerk-Tests ────────────────────────────────────

    def test_dns_resolution(self, result: TestResult):
        """Hostname-Auflösung testen."""
        try:
            resolved = socket.gethostbyname(self.config.ip)
            result.status = "PASS"
            result.message = f"{self.config.ip} → {resolved}"
            result.details["resolved_ip"] = resolved
        except socket.gaierror:
            try:
                socket.inet_aton(self.config.ip)
                result.status = "PASS"
                result.message = f"Direkte IP: {self.config.ip}"
            except socket.error:
                result.status = "FAIL"
                result.message = f"'{self.config.ip}' nicht auflösbar"

    def test_tcp_reachability(self, result: TestResult):
        """TCP-Verbindung zum SMB-Port prüfen."""
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(self.config.timeout)
        try:
            sock.connect((self.config.ip, self.config.port))
            result.status = "PASS"
            result.message = f"Port {self.config.port} offen"
        except socket.timeout:
            result.status = "FAIL"
            result.message = f"Timeout nach {self.config.timeout}s"
        except ConnectionRefusedError:
            result.status = "FAIL"
            result.message = f"Verbindung abgelehnt (Port {self.config.port})"
        except OSError as e:
            result.status = "FAIL"
            result.message = f"Netzwerkfehler: {e}"
        finally:
            sock.close()

    # ─── 2. Authentifizierung ─────────────────────────────────

    def test_authentication(self, result: TestResult):
        """SMB-Verbindung aufbauen und authentifizieren."""
        self.conn = self._new_connection()
        try:
            success = self.conn.connect(
                self.config.ip,
                self.config.port,
                timeout=self.config.timeout,
            )
            if success:
                result.status = "PASS"
                result.message = f"Angemeldet als '{self.config.username}'"
                result.details["server_name"] = self.config.server_name
                result.details["port"] = self.config.port
            else:
                result.status = "FAIL"
                result.message = "Authentifizierung abgelehnt"
                self.conn = None
        except Exception as e:
            result.status = "FAIL"
            result.message = f"Verbindung fehlgeschlagen: {e}"
            self.conn = None

    def test_guest_access(self, result: TestResult):
        """Testen ob Gastzugang möglich ist (Sicherheitscheck)."""
        guest_conn = self._new_connection(username="guest", password="")
        try:
            success = guest_conn.connect(
                self.config.ip,
                self.config.port,
                timeout=self.config.timeout,
            )
            if success:
                try:
                    shares = guest_conn.listShares()
                    result.status = "WARN"
                    result.message = (
                        f"Gastzugang AKTIV — {len(shares)} Shares sichtbar "
                        f"(Sicherheitsrisiko!)"
                    )
                except Exception:
                    result.status = "WARN"
                    result.message = "Gast-Login akzeptiert, aber kein Share-Zugriff"
            else:
                result.status = "PASS"
                result.message = "Gastzugang deaktiviert"
        except Exception:
            result.status = "PASS"
            result.message = "Gastzugang deaktiviert"
        finally:
            try:
                guest_conn.close()
            except Exception:
                pass

    # ─── 3. Share-Enumeration ─────────────────────────────────

    def test_share_enumeration(self, result: TestResult):
        """Alle verfügbaren Shares auflisten."""
        if not self.conn:
            result.status = "SKIP"
            result.message = "Keine aktive Verbindung"
            return

        shares = self.conn.listShares()
        share_info = []
        for s in shares:
            stype = {
                0: "Disk",
                1: "Drucker",
                2: "Gerät",
                3: "IPC",
            }.get(s.type & 0x0FFFFFFF, f"Unbekannt({s.type})")

            share_info.append({
                "name": s.name,
                "type": stype,
                "comments": s.comments or "",
            })

        self.discovered_shares = [
            si["name"] for si in share_info if si["type"] == "Disk"
        ]

        result.status = "PASS"
        result.message = (
            f"{len(share_info)} Shares gefunden, "
            f"davon {len(self.discovered_shares)} Disk-Shares"
        )
        result.details["shares"] = share_info

        # Share-Tabelle ausgeben
        print()
        print(f"  {'Share':<25} {'Typ':<12} {'Kommentar'}")
        print(f"  {'─' * 25} {'─' * 12} {'─' * 30}")
        for si in share_info:
            print(f"  {si['name']:<25} {si['type']:<12} {si['comments']}")
        print()

    # ─── 4. Read-Tests ────────────────────────────────────────

    def test_read_access(self, result: TestResult, share: str):
        """Lesezugriff auf Root des Shares."""
        entries = self.conn.listPath(share, "/")
        real = [e for e in entries if e.filename not in (".", "..")]
        dirs = sum(1 for e in real if e.isDirectory)
        files = len(real) - dirs

        result.status = "PASS"
        result.message = f"{files} Dateien, {dirs} Ordner im Root"
        result.details["total"] = len(real)
        result.details["files"] = files
        result.details["dirs"] = dirs
        result.details["sample"] = [e.filename for e in real[:15]]

    def test_directory_listing_details(self, result: TestResult, share: str):
        """Detailliertes Listing mit Metadaten."""
        entries = self.conn.listPath(share, "/")
        items = []
        for e in entries:
            if e.filename in (".", ".."):
                continue
            items.append({
                "name": e.filename,
                "is_dir": e.isDirectory,
                "size": e.file_size,
                "created": datetime.fromtimestamp(e.create_time).isoformat()
                    if e.create_time else None,
                "modified": datetime.fromtimestamp(e.last_write_time).isoformat()
                    if e.last_write_time else None,
                "readonly": bool(e.file_attributes & 0x01),
                "hidden": bool(e.file_attributes & 0x02),
                "system": bool(e.file_attributes & 0x04),
            })

        result.status = "PASS"
        result.message = f"{len(items)} Einträge mit Metadaten gelesen"
        result.details["items"] = items[:30]

    def test_file_read_content(self, result: TestResult, share: str):
        """Erste lesbare Datei inhaltlich lesen."""
        entries = self.conn.listPath(share, "/")
        target = None
        for e in entries:
            if (not e.isDirectory
                    and e.file_size > 0
                    and e.file_size < 1_000_000
                    and e.filename not in (".", "..")):
                target = e
                break

        if not target:
            result.status = "SKIP"
            result.message = "Keine passende Datei zum Lesen gefunden"
            return

        buf = io.BytesIO()
        self.conn.retrieveFile(share, f"/{target.filename}", buf)
        data = buf.getvalue()

        result.status = "PASS"
        result.message = f"'{target.filename}' gelesen — {len(data)} Bytes"
        result.details["filename"] = target.filename
        result.details["size"] = len(data)

        try:
            text = data.decode("utf-8")
            result.details["preview"] = text[:200]
        except UnicodeDecodeError:
            result.details["preview"] = "(Binärdatei)"

    # ─── 5. Write-Tests ───────────────────────────────────────

    def test_write_file(self, result: TestResult, share: str):
        """Datei schreiben und zurücklesen (Verifikation)."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        filename = f"{self._prefix}_write.txt"
        content = f"SMB-Test Schreibprüfung — {datetime.now().isoformat()}"
        content_bytes = content.encode("utf-8")

        try:
            buf = io.BytesIO(content_bytes)
            self.conn.storeFile(share, f"/{filename}", buf)

            buf_read = io.BytesIO()
            self.conn.retrieveFile(share, f"/{filename}", buf_read)
            read_back = buf_read.getvalue().decode("utf-8")

            if read_back == content:
                result.status = "PASS"
                result.message = "Schreiben + Lesen verifiziert"
            else:
                result.status = "WARN"
                result.message = "Geschrieben, aber Inhalt weicht ab"

        except OperationFailure as e:
            result.status = "FAIL"
            result.message = f"Schreibzugriff verweigert: {e}"
        finally:
            self._safe_delete(share, filename)

    def test_create_directory(self, result: TestResult, share: str):
        """Ordner erstellen und wieder löschen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        dirname = f"{self._prefix}_testdir"
        try:
            self.conn.createDirectory(share, dirname)

            entries = self.conn.listPath(share, "/")
            found = any(
                e.filename == dirname and e.isDirectory for e in entries
            )

            if found:
                result.status = "PASS"
                result.message = f"Ordner '{dirname}' erstellt und verifiziert"
            else:
                result.status = "WARN"
                result.message = "Ordner erstellt, aber nicht im Listing"

        except OperationFailure as e:
            result.status = "FAIL"
            result.message = f"Ordnererstellung verweigert: {e}"
        finally:
            try:
                self.conn.deleteDirectory(share, dirname)
            except Exception:
                pass

    def test_nested_directories(self, result: TestResult, share: str):
        """Verschachtelte Ordnerstruktur erstellen (3 Ebenen)."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        base = self._prefix + "_nested"
        paths = [base, f"{base}/ebene1", f"{base}/ebene1/ebene2"]
        created = []

        try:
            for p in paths:
                self.conn.createDirectory(share, p)
                created.append(p)

            test_file = f"{base}/ebene1/ebene2/tief.txt"
            buf = io.BytesIO(b"Tief verschachtelt!")
            self.conn.storeFile(share, test_file, buf)

            buf_r = io.BytesIO()
            self.conn.retrieveFile(share, test_file, buf_r)

            if buf_r.getvalue() == b"Tief verschachtelt!":
                result.status = "PASS"
                result.message = "3-Ebenen-Struktur + Datei erstellt/gelesen"
            else:
                result.status = "WARN"
                result.message = "Struktur erstellt, aber Leseinhalt abweichend"

        except OperationFailure as e:
            result.status = "FAIL"
            result.message = f"Verschachtelte Ordner: {e}"
        finally:
            try:
                self.conn.deleteFiles(share, f"{base}/ebene1/ebene2/tief.txt")
            except Exception:
                pass
            for p in reversed(created):
                try:
                    self.conn.deleteDirectory(share, p)
                except Exception:
                    pass

    def test_overwrite_file(self, result: TestResult, share: str):
        """Datei überschreiben und prüfen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        filename = f"{self._prefix}_overwrite.txt"
        try:
            self.conn.storeFile(share, f"/{filename}",
                                io.BytesIO(b"Version 1"))
            self.conn.storeFile(share, f"/{filename}",
                                io.BytesIO(b"Version 2"))

            buf = io.BytesIO()
            self.conn.retrieveFile(share, f"/{filename}", buf)
            content = buf.getvalue().decode()

            if content == "Version 2":
                result.status = "PASS"
                result.message = "Datei erfolgreich überschrieben"
            else:
                result.status = "WARN"
                result.message = f"Inhalt nach Überschreiben: '{content}'"

        except OperationFailure as e:
            result.status = "FAIL"
            result.message = str(e)
        finally:
            self._safe_delete(share, filename)

    def test_delete_file(self, result: TestResult, share: str):
        """Datei erstellen und löschen."""
        if not self.config.delete_test:
            result.status = "SKIP"
            result.message = "Löschtest deaktiviert"
            return

        filename = f"{self._prefix}_deltest.txt"
        try:
            buf = io.BytesIO(b"loeschtest")
            self.conn.storeFile(share, f"/{filename}", buf)

            self.conn.deleteFiles(share, f"/{filename}")

            entries = self.conn.listPath(share, "/")
            still_there = any(e.filename == filename for e in entries)

            if not still_there:
                result.status = "PASS"
                result.message = "Datei erstellt und erfolgreich gelöscht"
            else:
                result.status = "WARN"
                result.message = "Löschbefehl OK, Datei aber noch sichtbar"

        except OperationFailure as e:
            result.status = "FAIL"
            result.message = f"Löschen verweigert: {e}"
            self._safe_delete(share, filename)

    # ─── 6. Spezial-Tests ─────────────────────────────────────

    def test_special_characters(self, result: TestResult, share: str):
        """Dateinamen mit Sonderzeichen testen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        test_names = {
            "Leerzeichen": f"{self._prefix} space test.txt",
            "Umlaute": f"{self._prefix}_ÄÖÜß.txt",
            "Klammern": f"{self._prefix}_(test).txt",
            "Bindestrich": f"{self._prefix}_test-datei.txt",
            "Punkte": f"{self._prefix}_v1.2.3.txt",
        }

        passed = []
        failed = []

        for label, name in test_names.items():
            try:
                buf = io.BytesIO(f"test {label}".encode())
                self.conn.storeFile(share, f"/{name}", buf)
                self.conn.deleteFiles(share, f"/{name}")
                passed.append(label)
            except Exception as e:
                failed.append(f"{label}: {e}")

        if not failed:
            result.status = "PASS"
            result.message = f"Alle {len(passed)} Sonderzeichen-Tests OK"
        elif passed:
            result.status = "WARN"
            result.message = f"{len(passed)} OK, {len(failed)} fehlgeschlagen"
            result.details["failed"] = failed
        else:
            result.status = "FAIL"
            result.message = "Alle Sonderzeichen-Tests fehlgeschlagen"
            result.details["failed"] = failed

    def test_concurrent_read(self, result: TestResult, share: str):
        """Mehrfaches Lesen derselben Datei."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        filename = f"{self._prefix}_concurrent.txt"
        content = b"Simultaner Lesetest Inhalt 12345"

        try:
            self.conn.storeFile(share, f"/{filename}", io.BytesIO(content))

            buf1 = io.BytesIO()
            buf2 = io.BytesIO()
            self.conn.retrieveFile(share, f"/{filename}", buf1)
            self.conn.retrieveFile(share, f"/{filename}", buf2)

            if buf1.getvalue() == buf2.getvalue() == content:
                result.status = "PASS"
                result.message = "Mehrfaches Lesen konsistent"
            else:
                result.status = "WARN"
                result.message = "Inkonsistente Leseergebnisse"

        except Exception as e:
            result.status = "FAIL"
            result.message = str(e)
        finally:
            self._safe_delete(share, filename)

    def test_deep_traversal(self, result: TestResult, share: str):
        """Rekursive Verzeichnisstruktur lesen (max 3 Ebenen)."""
        tree = {}
        total = [0]
        self._walk(share, "/", tree, 0, 3, total)
        result.status = "PASS"
        result.message = f"{total[0]} Einträge in bis zu 3 Ebenen"
        result.details["tree_sample"] = self._trim(tree, 25)

    def _walk(self, share, path, tree, depth, max_depth, total):
        if depth >= max_depth:
            return
        try:
            entries = self.conn.listPath(share, path)
            for e in entries:
                if e.filename in (".", ".."):
                    continue
                total[0] += 1
                if total[0] > 500:
                    return
                if e.isDirectory:
                    tree[e.filename + "/"] = {}
                    subpath = f"{path.rstrip('/')}/{e.filename}"
                    try:
                        self._walk(share, subpath,
                                   tree[e.filename + "/"],
                                   depth + 1, max_depth, total)
                    except Exception:
                        tree[e.filename + "/"] = {"_error": "Zugriff verweigert"}
                else:
                    tree[e.filename] = f"{e.file_size} B"
        except Exception:
            tree["_error"] = "Zugriff verweigert"

    def _trim(self, tree, max_items):
        out = {}
        for i, (k, v) in enumerate(tree.items()):
            if i >= max_items:
                out["..."] = f"+{len(tree) - max_items} weitere"
                break
            out[k] = self._trim(v, 10) if isinstance(v, dict) else v
        return out

    # ─── 7. Benchmark ─────────────────────────────────────────

    def test_benchmark(self, result: TestResult, share: str):
        """Upload/Download Performance-Benchmark."""
        if not self.config.benchmark or not self.config.write_test:
            result.status = "SKIP"
            result.message = "Benchmark deaktiviert"
            return

        size = self.config.benchmark_size_kb * 1024
        data = os.urandom(size)
        filename = f"{self._prefix}_bench.bin"

        try:
            # Upload
            t0 = time.perf_counter()
            self.conn.storeFile(share, f"/{filename}", io.BytesIO(data))
            upload_time = time.perf_counter() - t0
            up_speed = (size / 1024 / 1024) / upload_time if upload_time else 0

            # Download
            buf = io.BytesIO()
            t0 = time.perf_counter()
            self.conn.retrieveFile(share, f"/{filename}", buf)
            download_time = time.perf_counter() - t0
            dl_speed = (size / 1024 / 1024) / download_time if download_time else 0

            integrity = buf.getvalue() == data

            result.status = "PASS" if integrity else "WARN"
            result.message = (
                f"Upload: {up_speed:.1f} MB/s | "
                f"Download: {dl_speed:.1f} MB/s | "
                f"{self.config.benchmark_size_kb} KB | "
                f"Integrität: {'OK' if integrity else 'FEHLER'}"
            )
            result.details = {
                "upload_mbps": round(up_speed, 2),
                "download_mbps": round(dl_speed, 2),
                "upload_time_s": round(upload_time, 3),
                "download_time_s": round(download_time, 3),
                "size_kb": self.config.benchmark_size_kb,
                "integrity": integrity,
            }

        except OperationFailure as e:
            result.status = "SKIP"
            result.message = f"Kein Schreibzugriff für Benchmark: {e}"
        finally:
            self._safe_delete(share, filename)

    # ─── Aufräumen ────────────────────────────────────────────

    def _safe_delete(self, share, filename):
        try:
            self.conn.deleteFiles(share, f"/{filename}")
        except Exception:
            pass

    # ─── Alle Tests ausführen ─────────────────────────────────

    def run_all(self) -> dict:
        w = 62
        print()
        print("=" * w)
        print("  SMB SERVER TESTER")
        print("=" * w)
        print(f"  Ziel:     {self.config.ip}:{self.config.port}")
        print(f"  Benutzer: {self.config.username}"
              + (f"@{self.config.domain}" if self.config.domain else ""))
        print(f"  Datum:    {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        print("=" * w)

        # Phase 1: Netzwerk
        print("\n-- Phase 1: Netzwerk ---------------------")
        self._run_test("DNS/IP Auflösung", "Netzwerk", self.test_dns_resolution)
        net = self._run_test("TCP Erreichbarkeit", "Netzwerk",
                             self.test_tcp_reachability)
        if net.status != "PASS":
            print("\n  Server nicht erreichbar. Abbruch.")
            return self._summary()

        # Phase 2: Auth
        print("\n-- Phase 2: Authentifizierung ------------")
        auth = self._run_test("Benutzer-Login", "Auth",
                              self.test_authentication)
        self._run_test("Gastzugang-Check", "Auth",
                       self.test_guest_access)
        if auth.status != "PASS":
            print("\n  Authentifizierung fehlgeschlagen. Abbruch.")
            return self._summary()

        # Phase 3: Shares
        print("\n-- Phase 3: Share-Enumeration ------------")
        self._run_test("Share-Auflistung", "Shares",
                       self.test_share_enumeration)

        shares = []
        if self.config.test_share:
            shares = [self.config.test_share]
        elif self.config.test_all_shares:
            shares = [
                s for s in self.discovered_shares
                if not s.endswith("$")
            ]

        if not shares:
            print("\n  Keine testbaren Disk-Shares gefunden.")
            if self.discovered_shares:
                print(f"  Vorhandene Shares: {', '.join(self.discovered_shares)}")
                print(f"  Nutze --share NAME um einen direkt zu testen.")
            return self._summary()

        # Phase 4: Pro-Share Tests
        for share in shares:
            print(f"\n-- Phase 4: Tests auf '{share}' ----------")

            self._run_test(f"[{share}] Lesezugriff", "Read",
                           self.test_read_access, share)
            self._run_test(f"[{share}] Detail-Listing", "Read",
                           self.test_directory_listing_details, share)
            self._run_test(f"[{share}] Datei lesen", "Read",
                           self.test_file_read_content, share)
            self._run_test(f"[{share}] Verzeichnis-Scan", "Read",
                           self.test_deep_traversal, share)
            self._run_test(f"[{share}] Datei schreiben", "Write",
                           self.test_write_file, share)
            self._run_test(f"[{share}] Ordner erstellen", "Write",
                           self.test_create_directory, share)
            self._run_test(f"[{share}] Verschachtelte Ordner", "Write",
                           self.test_nested_directories, share)
            self._run_test(f"[{share}] Datei ueberschreiben", "Write",
                           self.test_overwrite_file, share)
            self._run_test(f"[{share}] Datei loeschen", "Write",
                           self.test_delete_file, share)
            self._run_test(f"[{share}] Sonderzeichen", "Write",
                           self.test_special_characters, share)
            self._run_test(f"[{share}] Mehrfach-Lesen", "Access",
                           self.test_concurrent_read, share)
            self._run_test(f"[{share}] Benchmark", "Bench",
                           self.test_benchmark, share)

        # Verbindung schliessen
        if self.conn:
            try:
                self.conn.close()
            except Exception:
                pass

        return self._summary()

    # ─── Zusammenfassung ──────────────────────────────────────

    def _summary(self) -> dict:
        counts = {"PASS": 0, "FAIL": 0, "WARN": 0, "SKIP": 0}
        for r in self.results:
            counts[r.status] = counts.get(r.status, 0) + 1

        w = 62
        print()
        print("=" * w)
        print("  ERGEBNIS")
        print("=" * w)
        print(f"  Gesamt:      {len(self.results)} Tests")
        print(f"  Bestanden:   {counts['PASS']}")
        print(f"  Fehler:      {counts['FAIL']}")
        print(f"  Warnungen:   {counts['WARN']}")
        print(f"  Uebersprung: {counts['SKIP']}")
        print("-" * w)

        if counts["FAIL"] == 0 and counts["WARN"] == 0:
            print("  Alle Tests bestanden!")
        elif counts["FAIL"] == 0:
            print("  Keine Fehler, aber Warnungen beachten.")
        else:
            print("  Fehler gefunden — Details oben pruefen.")

        failed = [r for r in self.results if r.status == "FAIL"]
        if failed:
            print()
            print("  Fehlgeschlagene Tests:")
            for r in failed:
                print(f"    - {r.name}: {r.message}")

        print("=" * w)
        print()

        return {
            "target": f"{self.config.ip}:{self.config.port}",
            "username": self.config.username,
            "timestamp": datetime.now().isoformat(),
            "counts": counts,
            "results": [asdict(r) for r in self.results],
            "discovered_shares": self.discovered_shares,
        }


# ═══════════════════════════════════════════════════════════════
# Interaktiver Modus
# ═══════════════════════════════════════════════════════════════

def interactive_mode() -> SMBConfig:
    print()
    print("=" * 48)
    print("  SMB TESTER — Konfiguration")
    print("=" * 48)
    print()

    config = SMBConfig()

    config.ip = input("  Server IP / Hostname: ").strip()
    if not config.ip:
        print("  IP/Hostname ist Pflicht.")
        sys.exit(1)

    port = input("  Port [445]: ").strip()
    config.port = int(port) if port else 445

    config.username = input("  Benutzername: ").strip()
    config.password = getpass.getpass("  Passwort: ")
    config.domain = input("  Domaene (optional, Enter = keine): ").strip()

    server_name = input(f"  Server NetBIOS-Name [{config.ip}]: ").strip()
    config.server_name = server_name or config.ip

    share = input("  Bestimmter Share (leer = alle testen): ").strip()
    if share:
        config.test_share = share
        config.test_all_shares = False

    write = input("  Schreibtests? [J/n]: ").strip().lower()
    config.write_test = write != "n"
    config.delete_test = config.write_test

    bench = input("  Benchmark? [J/n]: ").strip().lower()
    config.benchmark = bench != "n"
    if config.benchmark:
        size = input("  Benchmark-Groesse in KB [1024]: ").strip()
        config.benchmark_size_kb = int(size) if size else 1024

    verbose = input("  Verbose? [j/N]: ").strip().lower()
    config.verbose = verbose == "j"

    return config


# ═══════════════════════════════════════════════════════════════
# CLI
# ═══════════════════════════════════════════════════════════════

def parse_args() -> Optional[SMBConfig]:
    parser = argparse.ArgumentParser(
        description="SMB Server Tester — Umfassende SMB-Tests",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Beispiele:
  %(prog)s --ip 192.168.1.10 --user admin --pass secret
  %(prog)s --ip server --user DOMAIN\\\\user --share SharedDocs
  %(prog)s --config my_config.json
  %(prog)s   (interaktiver Modus)
        """,
    )
    parser.add_argument("--ip", help="Server IP oder Hostname")
    parser.add_argument("--port", type=int, default=445)
    parser.add_argument("--user", dest="username", help="Benutzername")
    parser.add_argument("--pass", dest="password", help="Passwort")
    parser.add_argument("--domain", default="")
    parser.add_argument("--server-name", default="",
                        help="NetBIOS Server-Name (Standard: IP)")
    parser.add_argument("--share", default="",
                        help="Bestimmten Share testen")
    parser.add_argument("--no-write", action="store_true")
    parser.add_argument("--no-bench", action="store_true")
    parser.add_argument("--bench-size", type=int, default=1024,
                        help="Benchmark KB (Standard: 1024)")
    parser.add_argument("--timeout", type=int, default=30)
    parser.add_argument("-v", "--verbose", action="store_true")
    parser.add_argument("--config", help="JSON-Konfigurationsdatei")
    parser.add_argument("-o", "--output", help="Ergebnis als JSON speichern")

    args = parser.parse_args()

    if args.config:
        with open(args.config) as f:
            data = json.load(f)
        return SMBConfig(**data)

    if not args.ip:
        return None

    config = SMBConfig(
        ip=args.ip,
        port=args.port,
        username=args.username or "",
        password=args.password or "",
        domain=args.domain,
        server_name=args.server_name or args.ip,
        test_share=args.share,
        test_all_shares=not bool(args.share),
        write_test=not args.no_write,
        delete_test=not args.no_write,
        benchmark=not args.no_bench,
        benchmark_size_kb=args.bench_size,
        timeout=args.timeout,
        verbose=args.verbose,
    )

    if not config.username:
        config.username = input("  Benutzername: ").strip()
    if not config.password:
        config.password = getpass.getpass("  Passwort: ")

    return config


# ═══════════════════════════════════════════════════════════════
# Main
# ═══════════════════════════════════════════════════════════════

def main():
    config = parse_args()
    if config is None:
        config = interactive_mode()

    tester = SMBTester(config)
    summary = tester.run_all()

    # JSON-Export
    if "--output" in sys.argv or "-o" in sys.argv:
        for i, arg in enumerate(sys.argv):
            if arg in ("--output", "-o") and i + 1 < len(sys.argv):
                outfile = sys.argv[i + 1]
                with open(outfile, "w", encoding="utf-8") as f:
                    json.dump(summary, f, indent=2, ensure_ascii=False)
                print(f"  JSON-Report gespeichert: {outfile}\n")
                break


if __name__ == "__main__":
    main()
