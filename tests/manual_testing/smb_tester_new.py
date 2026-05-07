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
  - Lösch-Tests (Dateien, Ordner, nicht-leere Ordner, gesperrte Dateien)
  - Umbenennen / Verschieben von Dateien und Ordnern
  - Reconnect / Disconnect-Handling
  - Grenzwerte (leere Dateien, sehr lange Pfade, maximale Dateinamen)
  - Gleichzeitige Verbindungen (Multi-Session)
  - Zero-Byte & sehr große Dateien
  - Timestamp-Prüfung (Erstellzeit, Änderungszeit)
  - Versteckte / System-Attribute
  - Admin-Shares ($-Shares)
  - Fehlerhafte Anmeldedaten

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
# Berechtigungs-Erkennung
# ═══════════════════════════════════════════════════════════════

# NT STATUS Codes die auf Berechtigungsprobleme hinweisen
_ACCESS_DENIED_INDICATORS = (
    "access_denied", "access is denied", "STATUS_ACCESS_DENIED",
    "STATUS_LOGON_FAILURE", "STATUS_BAD_NETWORK_NAME",
    "STATUS_NETWORK_NAME_DELETED", "STATUS_SHARING_VIOLATION",
    "STATUS_PRIVILEGE_NOT_HELD", "STATUS_INVALID_HANDLE",
    "permission", "zugriff verweigert", "nicht berechtigt",
    "0xc0000022",  # NT STATUS_ACCESS_DENIED
    "0xc000006d",  # NT STATUS_LOGON_FAILURE
)


def is_access_denied(exc: Exception) -> bool:
    """Prüft ob eine Exception auf fehlende Berechtigungen hinweist."""
    msg = str(exc).lower()
    return any(ind.lower() in msg for ind in _ACCESS_DENIED_INDICATORS)


def permission_hint(exc: Exception) -> str:
    """Gibt einen benutzerfreundlichen Hinweis zur fehlenden Berechtigung."""
    msg = str(exc).lower()
    if "bad_network_name" in msg or "network_name_deleted" in msg:
        return "Share existiert nicht oder ist nicht erreichbar"
    if "logon_failure" in msg:
        return "Anmeldung am Share fehlgeschlagen"
    if "sharing_violation" in msg:
        return "Datei ist durch anderen Prozess gesperrt"
    if "privilege_not_held" in msg:
        return "Höhere Berechtigung erforderlich (z.B. Admin)"
    if "access_denied" in msg or "access is denied" in msg or "0xc0000022" in msg:
        return "Zugriff verweigert — fehlende Berechtigung"
    if isinstance(exc, PermissionError):
        return "Zugriff verweigert (OS-Ebene)"
    return f"Fehlgeschlagen: {type(exc).__name__}"


# ═══════════════════════════════════════════════════════════════
# Datenklassen
# ═══════════════════════════════════════════════════════════════

@dataclass
class TestResult:
    name: str
    category: str
    status: str = "PENDING"  # PASS, FAIL, WARN, SKIP, DENIED
    message: str = ""
    duration_ms: float = 0.0
    details: dict = field(default_factory=dict)

    @property
    def icon(self):
        return {
            "PASS": "✅", "FAIL": "❌", "WARN": "⚠️ ",
            "SKIP": "⏭️", "PENDING": "⏳", "DENIED": "🔒",
        }.get(self.status, "❓")


@dataclass
class SharePermissions:
    """Erkannte Berechtigungen für einen einzelnen Share."""
    name: str
    accessible: bool = False      # Share überhaupt erreichbar
    can_read: bool = False        # Lesen (listPath, retrieveFile)
    can_write: bool = False       # Schreiben (storeFile)
    can_delete: bool = False      # Löschen (deleteFiles)
    can_create_dir: bool = False  # Ordner erstellen
    can_rename: bool = False      # Umbenennen
    error_message: str = ""       # Fehler beim Zugriff

    def summary_line(self) -> str:
        if not self.accessible:
            return f"🔒 {self.name}: KEIN ZUGRIFF — {self.error_message}"
        flags = []
        flags.append("✅ Read" if self.can_read else "🔒 Read")
        flags.append("✅ Write" if self.can_write else "🔒 Write")
        flags.append("✅ Delete" if self.can_delete else "🔒 Delete")
        flags.append("✅ MkDir" if self.can_create_dir else "🔒 MkDir")
        flags.append("✅ Rename" if self.can_rename else "🔒 Rename")
        return f"  {self.name}: {' | '.join(flags)}"


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
    test_admin_shares: bool = False     # $-Shares testen
    test_bad_credentials: bool = True   # Falsche Anmeldedaten testen
    reconnect_test: bool = True         # Disconnect/Reconnect
    max_filename_length: int = 255      # Max Dateinamenlänge zum Testen


# ═══════════════════════════════════════════════════════════════
# Haupt-Tester
# ═══════════════════════════════════════════════════════════════

class SMBTester:
    def __init__(self, config: SMBConfig):
        self.config = config
        self.results: list[TestResult] = []
        self.conn: Optional[SMBConnection] = None
        self.discovered_shares: list[str] = []
        self.share_permissions: dict[str, SharePermissions] = {}
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
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = f"🔒 {permission_hint(e)}"
            else:
                result.status = "FAIL"
                result.message = f"SMB-Fehler: {e}"
            self._log(f"{name}: {e}", "ERROR")
        except PermissionError as e:
            result.status = "DENIED"
            result.message = f"🔒 {permission_hint(e)}"
        except ConnectionError as e:
            result.status = "FAIL"
            result.message = f"Verbindungsfehler: {e}"
            self._log(f"{name}: Verbindung verloren: {e}", "ERROR")
        except Exception as e:
            # Auch hier auf Access-Denied prüfen
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = f"🔒 {permission_hint(e)}"
            else:
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

    def _skip_test(self, name: str, category: str, reason: str) -> TestResult:
        """Erstellt einen übersprungenen Test mit Begründung (ohne Ausführung)."""
        result = TestResult(
            name=name, category=category,
            status="SKIP", message=reason, duration_ms=0,
        )
        self.results.append(result)
        print(f"  {result.icon}  {name}: {result.status} — {reason}")
        return result

    def _denied_test(self, name: str, category: str, reason: str) -> TestResult:
        """Erstellt einen DENIED-Test (Berechtigung fehlt)."""
        result = TestResult(
            name=name, category=category,
            status="DENIED", message=f"🔒 {reason}", duration_ms=0,
        )
        self.results.append(result)
        print(f"  {result.icon}  {name}: {result.status} — 🔒 {reason}")
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

    def _ensure_connection(self) -> bool:
        """Stellt sicher, dass self.conn aktiv ist. Reconnect bei Bedarf."""
        if self.conn is None:
            return False
        try:
            # Probe: listShares ist leichtgewichtig
            self.conn.listShares()
            return True
        except Exception:
            self._log("Verbindung verloren, versuche Reconnect...", "WARN")
            try:
                self.conn.close()
            except Exception:
                pass
            try:
                self.conn = self._new_connection()
                return self.conn.connect(
                    self.config.ip, self.config.port,
                    timeout=self.config.timeout,
                )
            except Exception:
                self.conn = None
                return False

    # ─── Berechtigungsprüfung pro Share ───────────────────────

    def probe_share_permissions(self, share: str) -> SharePermissions:
        """
        Testet systematisch welche Rechte auf einem Share vorhanden sind.
        Gibt ein SharePermissions-Objekt zurück.
        Wird VOR den eigentlichen Tests aufgerufen.
        """
        perms = SharePermissions(name=share)

        if not self.conn:
            perms.error_message = "Keine aktive Verbindung"
            return perms

        # 1) Share erreichbar? (listPath auf Root)
        try:
            self.conn.listPath(share, "/")
            perms.accessible = True
            perms.can_read = True
        except Exception as e:
            perms.accessible = False
            perms.error_message = permission_hint(e)
            self.share_permissions[share] = perms
            return perms

        # 2) Schreibrecht? (kleine Testdatei erstellen)
        test_file = f"{self._prefix}_permcheck.tmp"
        try:
            self.conn.storeFile(
                share, f"/{test_file}",
                io.BytesIO(b"permission_check"),
            )
            perms.can_write = True
        except Exception:
            perms.can_write = False

        # 3) Löschrecht? (nur wenn Schreiben ging)
        if perms.can_write:
            try:
                self.conn.deleteFiles(share, f"/{test_file}")
                perms.can_delete = True
            except Exception:
                perms.can_delete = False
                # Datei bleibt liegen — wird am Ende aufgeräumt

        # 4) Ordner-Erstellung?
        test_dir = f"{self._prefix}_permcheck_dir"
        try:
            self.conn.createDirectory(share, test_dir)
            perms.can_create_dir = True
            try:
                self.conn.deleteDirectory(share, test_dir)
            except Exception:
                pass
        except Exception:
            perms.can_create_dir = False

        # 5) Umbenennen? (nur wenn Schreiben ging)
        if perms.can_write:
            old = f"{self._prefix}_permren_a.tmp"
            new = f"{self._prefix}_permren_b.tmp"
            try:
                self.conn.storeFile(
                    share, f"/{old}", io.BytesIO(b"rename_check"),
                )
                try:
                    self.conn.rename(share, f"/{old}", f"/{new}")
                    perms.can_rename = True
                    self._safe_delete(share, new)
                except AttributeError:
                    # pysmb-Version ohne rename()
                    perms.can_rename = False
                except Exception:
                    perms.can_rename = False
                    self._safe_delete(share, old)
            except Exception:
                perms.can_rename = False

        # Aufräumen der Permcheck-Dateien
        for fn in (test_file, f"{self._prefix}_permren_a.tmp",
                   f"{self._prefix}_permren_b.tmp"):
            self._safe_delete(share, fn)

        self.share_permissions[share] = perms
        return perms

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

    def test_port_139(self, result: TestResult):
        """Prüfe ob auch NetBIOS-Port 139 erreichbar ist."""
        if self.config.port == 139:
            result.status = "SKIP"
            result.message = "Bereits auf Port 139 konfiguriert"
            return
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(5)
        try:
            sock.connect((self.config.ip, 139))
            result.status = "PASS"
            result.message = "Port 139 (NetBIOS) ebenfalls offen"
        except Exception:
            result.status = "PASS"
            result.message = "Port 139 geschlossen (nur Direct TCP aktiv — OK)"
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

    def test_bad_credentials(self, result: TestResult):
        """Testen ob falsche Anmeldedaten korrekt abgelehnt werden."""
        if not self.config.test_bad_credentials:
            result.status = "SKIP"
            result.message = "Test deaktiviert"
            return

        bad_conn = self._new_connection(
            username=f"invalid_{uuid.uuid4().hex[:6]}",
            password=f"wrong_{uuid.uuid4().hex[:8]}",
        )
        try:
            success = bad_conn.connect(
                self.config.ip,
                self.config.port,
                timeout=self.config.timeout,
            )
            if success:
                try:
                    shares = bad_conn.listShares()
                    result.status = "WARN"
                    result.message = (
                        f"Ungültige Credentials akzeptiert — "
                        f"{len(shares)} Shares sichtbar (Gast-Fallback?)"
                    )
                except Exception:
                    result.status = "WARN"
                    result.message = "Login mit falschen Daten akzeptiert, aber kein Zugriff"
            else:
                result.status = "PASS"
                result.message = "Falsche Anmeldedaten korrekt abgelehnt"
        except Exception:
            result.status = "PASS"
            result.message = "Falsche Anmeldedaten korrekt abgelehnt"
        finally:
            try:
                bad_conn.close()
            except Exception:
                pass

    def test_empty_password(self, result: TestResult):
        """Testen ob leeres Passwort akzeptiert wird."""
        if not self.config.test_bad_credentials:
            result.status = "SKIP"
            result.message = "Test deaktiviert"
            return

        empty_conn = self._new_connection(
            username=self.config.username,
            password="",
        )
        try:
            success = empty_conn.connect(
                self.config.ip,
                self.config.port,
                timeout=self.config.timeout,
            )
            if success:
                result.status = "WARN"
                result.message = (
                    f"Benutzer '{self.config.username}' mit leerem Passwort "
                    f"akzeptiert (Sicherheitsrisiko!)"
                )
            else:
                result.status = "PASS"
                result.message = "Leeres Passwort korrekt abgelehnt"
        except Exception:
            result.status = "PASS"
            result.message = "Leeres Passwort korrekt abgelehnt"
        finally:
            try:
                empty_conn.close()
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
                "is_admin": s.name.endswith("$"),
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
        print(f"  {'Share':<25} {'Typ':<12} {'Admin':<7} {'Kommentar'}")
        print(f"  {'─' * 25} {'─' * 12} {'─' * 7} {'─' * 30}")
        for si in share_info:
            admin_flag = "  $" if si["is_admin"] else ""
            print(f"  {si['name']:<25} {si['type']:<12} {admin_flag:<7} {si['comments']}")
        print()

    def test_admin_shares(self, result: TestResult):
        """Prüfe Zugriff auf administrative $-Shares."""
        if not self.config.test_admin_shares:
            result.status = "SKIP"
            result.message = "Admin-Share-Test deaktiviert (--test-admin-shares)"
            return
        if not self.conn:
            result.status = "SKIP"
            result.message = "Keine aktive Verbindung"
            return

        admin_shares = [s for s in self.discovered_shares if s.endswith("$")]
        if not admin_shares:
            result.status = "PASS"
            result.message = "Keine Admin-Shares ($) gefunden"
            return

        accessible = []
        denied = []
        for share in admin_shares:
            try:
                self.conn.listPath(share, "/")
                accessible.append(share)
            except Exception:
                denied.append(share)

        if accessible:
            result.status = "WARN"
            result.message = (
                f"Zugriff auf Admin-Shares möglich: "
                f"{', '.join(accessible)} (Sicherheitsrisiko!)"
            )
        else:
            result.status = "PASS"
            result.message = f"Alle {len(denied)} Admin-Shares korrekt gesperrt"

        result.details["accessible"] = accessible
        result.details["denied"] = denied

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

    def test_read_nonexistent_file(self, result: TestResult, share: str):
        """Lesen einer nicht existierenden Datei (soll Fehler geben)."""
        fake_name = f"{self._prefix}_nonexistent_{uuid.uuid4().hex[:8]}.txt"
        try:
            buf = io.BytesIO()
            self.conn.retrieveFile(share, f"/{fake_name}", buf)
            result.status = "WARN"
            result.message = (
                "Lesen einer nicht-existierenden Datei gab keinen Fehler!"
            )
        except OperationFailure:
            result.status = "PASS"
            result.message = "Nicht-existierende Datei korrekt abgelehnt"
        except Exception as e:
            result.status = "PASS"
            result.message = f"Korrekt abgelehnt: {type(e).__name__}"

    def test_read_nonexistent_directory(self, result: TestResult, share: str):
        """Listing eines nicht existierenden Ordners."""
        fake_dir = f"/{self._prefix}_nodir_{uuid.uuid4().hex[:8]}"
        try:
            self.conn.listPath(share, fake_dir)
            result.status = "WARN"
            result.message = "Nicht-existierender Ordner gab kein Fehler!"
        except OperationFailure:
            result.status = "PASS"
            result.message = "Nicht-existierender Ordner korrekt abgelehnt"
        except Exception as e:
            result.status = "PASS"
            result.message = f"Korrekt abgelehnt: {type(e).__name__}"

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
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = f"🔒 Schreibzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Schreibzugriff verweigert: {e}"
        finally:
            self._safe_delete(share, filename)

    def test_write_zero_byte_file(self, result: TestResult, share: str):
        """Leere Datei (0 Bytes) schreiben und prüfen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        filename = f"{self._prefix}_zero.txt"
        try:
            buf = io.BytesIO(b"")
            self.conn.storeFile(share, f"/{filename}", buf)

            entries = self.conn.listPath(share, "/")
            found = [e for e in entries if e.filename == filename]

            if found and found[0].file_size == 0:
                result.status = "PASS"
                result.message = "0-Byte-Datei erstellt und verifiziert"
            elif found:
                result.status = "WARN"
                result.message = f"Datei existiert, aber Größe = {found[0].file_size}"
            else:
                result.status = "WARN"
                result.message = "Datei nicht im Listing gefunden"

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = f"0-Byte-Datei schreiben fehlgeschlagen: {e}"
        finally:
            self._safe_delete(share, filename)

    def test_write_large_content(self, result: TestResult, share: str):
        """Große Datei schreiben (5 MB) mit Integritätsprüfung."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        filename = f"{self._prefix}_large.bin"
        size = 5 * 1024 * 1024  # 5 MB
        data = os.urandom(size)

        try:
            self.conn.storeFile(share, f"/{filename}", io.BytesIO(data))

            buf = io.BytesIO()
            self.conn.retrieveFile(share, f"/{filename}", buf)

            if buf.getvalue() == data:
                result.status = "PASS"
                result.message = f"5 MB Datei mit Integritätsprüfung OK"
            else:
                result.status = "WARN"
                result.message = (
                    f"5 MB geschrieben, aber Inhalt weicht ab "
                    f"(gelesen: {len(buf.getvalue())} Bytes)"
                )

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Große Datei fehlgeschlagen: {e}"
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
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Ordnererstellung verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Ordnererstellung verweigert: {e}"
        finally:
            try:
                self.conn.deleteDirectory(share, dirname)
            except Exception:
                pass

    def test_create_duplicate_directory(self, result: TestResult, share: str):
        """Prüfen ob doppeltes Erstellen eines Ordners korrekt Fehler gibt."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        dirname = f"{self._prefix}_dupdir"
        try:
            self.conn.createDirectory(share, dirname)
            try:
                self.conn.createDirectory(share, dirname)
                result.status = "WARN"
                result.message = "Doppeltes Erstellen gab keinen Fehler"
            except OperationFailure:
                result.status = "PASS"
                result.message = "Doppeltes Erstellen korrekt abgelehnt"
            except Exception as e:
                result.status = "PASS"
                result.message = f"Doppeltes Erstellen abgelehnt: {type(e).__name__}"
        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Ordnererstellung verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Erster Ordner konnte nicht erstellt werden: {e}"
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
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Verschachtelte Ordner: Zugriff verweigert"
            else:
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
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Überschreiben verweigert"
            else:
                result.status = "FAIL"
                result.message = str(e)
        finally:
            self._safe_delete(share, filename)

    def test_overwrite_with_smaller(self, result: TestResult, share: str):
        """Datei mit kleinerem Inhalt überschreiben — prüft Truncate."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        filename = f"{self._prefix}_shrink.txt"
        try:
            self.conn.storeFile(
                share, f"/{filename}",
                io.BytesIO(b"A" * 10000),
            )
            self.conn.storeFile(
                share, f"/{filename}",
                io.BytesIO(b"klein"),
            )

            buf = io.BytesIO()
            self.conn.retrieveFile(share, f"/{filename}", buf)
            data = buf.getvalue()

            if data == b"klein":
                result.status = "PASS"
                result.message = "Truncate bei Überschreiben korrekt (10000→5 Bytes)"
            else:
                result.status = "WARN"
                result.message = (
                    f"Datei ist {len(data)} Bytes statt 5 — "
                    f"alter Inhalt nicht abgeschnitten?"
                )
        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = str(e)
        finally:
            self._safe_delete(share, filename)

    # ─── 6. Lösch-Tests ───────────────────────────────────────

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
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Löschzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Löschen verweigert: {e}"
            self._safe_delete(share, filename)

    def test_delete_nonexistent_file(self, result: TestResult, share: str):
        """Löschen einer nicht existierenden Datei."""
        if not self.config.delete_test:
            result.status = "SKIP"
            result.message = "Löschtest deaktiviert"
            return

        fake = f"/{self._prefix}_nope_{uuid.uuid4().hex[:8]}.txt"
        try:
            self.conn.deleteFiles(share, fake)
            result.status = "WARN"
            result.message = "Löschen nicht-existierender Datei gab keinen Fehler"
        except OperationFailure:
            result.status = "PASS"
            result.message = "Nicht-existierende Datei: Löschung korrekt abgelehnt"
        except Exception as e:
            result.status = "PASS"
            result.message = f"Korrekt abgelehnt: {type(e).__name__}"

    def test_delete_empty_directory(self, result: TestResult, share: str):
        """Leeren Ordner löschen."""
        if not self.config.delete_test:
            result.status = "SKIP"
            result.message = "Löschtest deaktiviert"
            return

        dirname = f"{self._prefix}_delempty"
        try:
            self.conn.createDirectory(share, dirname)
            self.conn.deleteDirectory(share, dirname)

            entries = self.conn.listPath(share, "/")
            still_there = any(e.filename == dirname for e in entries)

            if not still_there:
                result.status = "PASS"
                result.message = "Leerer Ordner erfolgreich gelöscht"
            else:
                result.status = "WARN"
                result.message = "Löschbefehl OK, Ordner aber noch sichtbar"
        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Ordner löschen verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Ordner löschen fehlgeschlagen: {e}"

    def test_delete_nonempty_directory(self, result: TestResult, share: str):
        """Nicht-leeren Ordner löschen (soll fehlschlagen)."""
        if not self.config.delete_test:
            result.status = "SKIP"
            result.message = "Löschtest deaktiviert"
            return

        dirname = f"{self._prefix}_delnotempty"
        filename = f"{dirname}/datei.txt"
        try:
            self.conn.createDirectory(share, dirname)
            self.conn.storeFile(share, filename, io.BytesIO(b"inhalt"))

            try:
                self.conn.deleteDirectory(share, dirname)
                result.status = "WARN"
                result.message = (
                    "Nicht-leerer Ordner wurde gelöscht "
                    "(manche Server erlauben das)"
                )
            except OperationFailure:
                result.status = "PASS"
                result.message = "Nicht-leerer Ordner korrekt abgelehnt"
            except Exception as e:
                result.status = "PASS"
                result.message = f"Korrekt abgelehnt: {type(e).__name__}"

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Ordner/Datei erstellen verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Setup fehlgeschlagen: {e}"
        finally:
            try:
                self.conn.deleteFiles(share, filename)
            except Exception:
                pass
            try:
                self.conn.deleteDirectory(share, dirname)
            except Exception:
                pass

    def test_delete_nonexistent_directory(self, result: TestResult, share: str):
        """Nicht existierenden Ordner löschen."""
        if not self.config.delete_test:
            result.status = "SKIP"
            result.message = "Löschtest deaktiviert"
            return

        fake = f"{self._prefix}_nodir_{uuid.uuid4().hex[:8]}"
        try:
            self.conn.deleteDirectory(share, fake)
            result.status = "WARN"
            result.message = "Löschen nicht-existierenden Ordners gab keinen Fehler"
        except OperationFailure:
            result.status = "PASS"
            result.message = "Nicht-existierender Ordner: Löschung korrekt abgelehnt"
        except Exception as e:
            result.status = "PASS"
            result.message = f"Korrekt abgelehnt: {type(e).__name__}"

    def test_recursive_delete(self, result: TestResult, share: str):
        """Rekursives Löschen: tief verschachtelte Struktur aufräumen."""
        if not self.config.delete_test:
            result.status = "SKIP"
            result.message = "Löschtest deaktiviert"
            return

        base = f"{self._prefix}_recdel"
        paths = [
            base,
            f"{base}/a",
            f"{base}/a/b",
            f"{base}/a/b/c",
        ]
        files = [
            f"{base}/root.txt",
            f"{base}/a/mid.txt",
            f"{base}/a/b/deep.txt",
            f"{base}/a/b/c/deepest.txt",
        ]

        try:
            for p in paths:
                self.conn.createDirectory(share, p)
            for f in files:
                self.conn.storeFile(share, f, io.BytesIO(b"x"))

            errors = []
            for f in reversed(files):
                try:
                    self.conn.deleteFiles(share, f)
                except Exception as e:
                    errors.append(f"Datei {f}: {e}")

            for p in reversed(paths):
                try:
                    self.conn.deleteDirectory(share, p)
                except Exception as e:
                    errors.append(f"Ordner {p}: {e}")

            entries = self.conn.listPath(share, "/")
            still_there = any(e.filename == base for e in entries)

            if not still_there and not errors:
                result.status = "PASS"
                result.message = (
                    f"Rekursives Löschen OK "
                    f"(4 Ordner + 4 Dateien)"
                )
            elif not still_there:
                result.status = "WARN"
                result.message = f"Gelöscht, aber mit Fehlern: {len(errors)}"
                result.details["errors"] = errors
            else:
                result.status = "FAIL"
                result.message = "Basis-Ordner noch vorhanden"
                result.details["errors"] = errors

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Zugriff für rekursives Erstellen/Löschen verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Setup fehlgeschlagen: {e}"
            for f in reversed(files):
                try:
                    self.conn.deleteFiles(share, f)
                except Exception:
                    pass
            for p in reversed(paths):
                try:
                    self.conn.deleteDirectory(share, p)
                except Exception:
                    pass

    def test_delete_multiple_files_pattern(self, result: TestResult, share: str):
        """Mehrere Dateien per Wildcard/Pattern löschen."""
        if not self.config.delete_test:
            result.status = "SKIP"
            result.message = "Löschtest deaktiviert"
            return

        base = f"{self._prefix}_multi"
        filenames = [f"{base}_1.txt", f"{base}_2.txt", f"{base}_3.txt"]

        try:
            for fn in filenames:
                self.conn.storeFile(share, f"/{fn}", io.BytesIO(b"multi"))

            try:
                self.conn.deleteFiles(share, f"/{base}_*.txt")
                entries = self.conn.listPath(share, "/")
                remaining = [
                    e.filename for e in entries
                    if e.filename.startswith(base)
                ]
                if not remaining:
                    result.status = "PASS"
                    result.message = "Wildcard-Löschung erfolgreich (3 Dateien)"
                else:
                    result.status = "WARN"
                    result.message = (
                        f"Wildcard-Löschung: {len(remaining)} Dateien übrig"
                    )
            except OperationFailure:
                result.status = "WARN"
                result.message = "Wildcard-Löschung nicht unterstützt — Einzellöschung"
                for fn in filenames:
                    self._safe_delete(share, fn)

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreib-/Löschzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Dateien erstellen fehlgeschlagen: {e}"
        finally:
            for fn in filenames:
                self._safe_delete(share, fn)

    # ─── 7. Umbenennen / Verschieben ──────────────────────────

    def test_rename_file(self, result: TestResult, share: str):
        """Datei umbenennen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        old_name = f"{self._prefix}_rename_old.txt"
        new_name = f"{self._prefix}_rename_new.txt"
        content = b"Umbenennungstest"

        try:
            self.conn.storeFile(share, f"/{old_name}", io.BytesIO(content))
            self.conn.rename(share, f"/{old_name}", f"/{new_name}")

            entries = self.conn.listPath(share, "/")
            old_exists = any(e.filename == old_name for e in entries)
            new_exists = any(e.filename == new_name for e in entries)

            buf = io.BytesIO()
            self.conn.retrieveFile(share, f"/{new_name}", buf)

            if not old_exists and new_exists and buf.getvalue() == content:
                result.status = "PASS"
                result.message = "Umbenennung + Inhaltsverifikation OK"
            elif new_exists:
                result.status = "WARN"
                result.message = "Neue Datei existiert, aber alte evtl. auch noch"
            else:
                result.status = "FAIL"
                result.message = "Neue Datei nicht gefunden nach Umbenennung"

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Umbenennung verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Umbenennung fehlgeschlagen: {e}"
        except AttributeError:
            result.status = "SKIP"
            result.message = "rename() nicht in dieser pysmb-Version verfügbar"
        finally:
            self._safe_delete(share, old_name)
            self._safe_delete(share, new_name)

    def test_move_file_to_subdirectory(self, result: TestResult, share: str):
        """Datei in Unterordner verschieben (rename mit Pfad)."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        dirname = f"{self._prefix}_movedir"
        filename = f"{self._prefix}_movefile.txt"
        content = b"Verschiebetest"

        try:
            self.conn.createDirectory(share, dirname)
            self.conn.storeFile(share, f"/{filename}", io.BytesIO(content))

            self.conn.rename(share, f"/{filename}", f"/{dirname}/{filename}")

            buf = io.BytesIO()
            self.conn.retrieveFile(share, f"/{dirname}/{filename}", buf)

            if buf.getvalue() == content:
                result.status = "PASS"
                result.message = "Datei in Unterordner verschoben + verifiziert"
            else:
                result.status = "WARN"
                result.message = "Verschoben, aber Inhalt weicht ab"

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Verschieben verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Verschieben fehlgeschlagen: {e}"
        except AttributeError:
            result.status = "SKIP"
            result.message = "rename() nicht verfügbar"
        finally:
            try:
                self.conn.deleteFiles(share, f"/{dirname}/{filename}")
            except Exception:
                pass
            self._safe_delete(share, filename)
            try:
                self.conn.deleteDirectory(share, dirname)
            except Exception:
                pass

    def test_rename_directory(self, result: TestResult, share: str):
        """Ordner umbenennen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        old_dir = f"{self._prefix}_rendir_old"
        new_dir = f"{self._prefix}_rendir_new"

        try:
            self.conn.createDirectory(share, old_dir)
            self.conn.storeFile(
                share, f"/{old_dir}/inhalt.txt",
                io.BytesIO(b"ordnertest"),
            )

            self.conn.rename(share, f"/{old_dir}", f"/{new_dir}")

            entries = self.conn.listPath(share, "/")
            old_exists = any(e.filename == old_dir for e in entries)
            new_exists = any(e.filename == new_dir for e in entries)

            if not old_exists and new_exists:
                buf = io.BytesIO()
                self.conn.retrieveFile(share, f"/{new_dir}/inhalt.txt", buf)
                if buf.getvalue() == b"ordnertest":
                    result.status = "PASS"
                    result.message = "Ordner umbenannt, Inhalt intakt"
                else:
                    result.status = "WARN"
                    result.message = "Ordner umbenannt, aber Inhalt abweichend"
            else:
                result.status = "FAIL"
                result.message = (
                    f"Umbenennung fehlgeschlagen "
                    f"(alt={old_exists}, neu={new_exists})"
                )

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Ordner umbenennen verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Ordner umbenennen fehlgeschlagen: {e}"
        except AttributeError:
            result.status = "SKIP"
            result.message = "rename() nicht verfügbar"
        finally:
            for d in (old_dir, new_dir):
                try:
                    self.conn.deleteFiles(share, f"/{d}/inhalt.txt")
                except Exception:
                    pass
                try:
                    self.conn.deleteDirectory(share, d)
                except Exception:
                    pass

    def test_rename_to_existing(self, result: TestResult, share: str):
        """Umbenennung auf existierenden Namen (soll Fehler geben)."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        file_a = f"{self._prefix}_renexist_a.txt"
        file_b = f"{self._prefix}_renexist_b.txt"

        try:
            self.conn.storeFile(share, f"/{file_a}", io.BytesIO(b"A"))
            self.conn.storeFile(share, f"/{file_b}", io.BytesIO(b"B"))

            try:
                self.conn.rename(share, f"/{file_a}", f"/{file_b}")

                buf = io.BytesIO()
                self.conn.retrieveFile(share, f"/{file_b}", buf)
                content = buf.getvalue()

                if content == b"A":
                    result.status = "WARN"
                    result.message = (
                        "Umbenennung auf existierenden Namen hat überschrieben"
                    )
                else:
                    result.status = "WARN"
                    result.message = "Umbenennung auf existierenden Namen erlaubt"

            except OperationFailure:
                result.status = "PASS"
                result.message = "Umbenennung auf existierenden Namen korrekt abgelehnt"
            except Exception as e:
                result.status = "PASS"
                result.message = f"Korrekt abgelehnt: {type(e).__name__}"

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Setup fehlgeschlagen: {e}"
        except AttributeError:
            result.status = "SKIP"
            result.message = "rename() nicht verfügbar"
        finally:
            self._safe_delete(share, file_a)
            self._safe_delete(share, file_b)

    # ─── 8. Sonderzeichen & Edge-Cases ────────────────────────

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
            # Prüfe ob es ein generelles Zugriffsproblem ist
            if any(is_access_denied(Exception(f)) for f in failed):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff für Sonderzeichen-Test verweigert"
            else:
                result.status = "FAIL"
                result.message = "Alle Sonderzeichen-Tests fehlgeschlagen"
            result.details["failed"] = failed

    def test_unicode_filenames(self, result: TestResult, share: str):
        """Erweiterte Unicode-Dateinamen (CJK, Emoji, Kyrillisch)."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        test_names = {
            "Kyrillisch": f"{self._prefix}_тест.txt",
            "CJK": f"{self._prefix}_测试.txt",
            "Arabisch": f"{self._prefix}_اختبار.txt",
            "Akzente": f"{self._prefix}_café_résumé.txt",
            "Gemischt": f"{self._prefix}_Hello_Мир_世界.txt",
        }

        passed = []
        failed = []

        for label, name in test_names.items():
            try:
                content = f"Unicode: {label}".encode("utf-8")
                self.conn.storeFile(share, f"/{name}", io.BytesIO(content))

                buf = io.BytesIO()
                self.conn.retrieveFile(share, f"/{name}", buf)
                if buf.getvalue() == content:
                    passed.append(label)
                else:
                    failed.append(f"{label}: Inhalt abweichend")

                self.conn.deleteFiles(share, f"/{name}")
            except Exception as e:
                failed.append(f"{label}: {e}")

        if not failed:
            result.status = "PASS"
            result.message = f"Alle {len(passed)} Unicode-Tests OK"
        elif passed:
            result.status = "WARN"
            result.message = f"{len(passed)} OK, {len(failed)} fehlgeschlagen"
            result.details["failed"] = failed
        else:
            result.status = "FAIL"
            result.message = "Alle Unicode-Tests fehlgeschlagen"
            result.details["failed"] = failed

    def test_long_filename(self, result: TestResult, share: str):
        """Maximale Dateinamenlänge testen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        max_len = self.config.max_filename_length
        available = max_len - len(self._prefix) - 5
        if available < 10:
            available = 50

        long_part = "a" * available
        filename = f"{self._prefix}_{long_part}.txt"

        try:
            self.conn.storeFile(
                share, f"/{filename}",
                io.BytesIO(b"lang"),
            )
            result.status = "PASS"
            result.message = f"Dateiname mit {len(filename)} Zeichen akzeptiert"
            self._safe_delete(share, filename)
        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff verweigert"
            else:
                result.status = "WARN"
                result.message = f"Langer Dateiname ({len(filename)} Z.) abgelehnt: {e}"
        except Exception as e:
            result.status = "WARN"
            result.message = f"Langer Dateiname ({len(filename)} Z.): {type(e).__name__}"

    def test_long_path(self, result: TestResult, share: str):
        """Langen Pfad testen (mehrere verschachtelte Ordner)."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        base = self._prefix + "_longpath"
        segment = "abcdefghijklmnopqr"
        depth = 8
        paths = [base]
        current = base
        for i in range(depth):
            current = f"{current}/{segment}{i}"
            paths.append(current)

        created = []
        try:
            for p in paths:
                self.conn.createDirectory(share, p)
                created.append(p)

            test_file = f"{current}/test.txt"
            self.conn.storeFile(share, test_file, io.BytesIO(b"tiefster Punkt"))

            buf = io.BytesIO()
            self.conn.retrieveFile(share, test_file, buf)

            total_len = len(test_file)
            if buf.getvalue() == b"tiefster Punkt":
                result.status = "PASS"
                result.message = (
                    f"Pfadtiefe {depth+1} Ebenen OK "
                    f"(Pfadlänge: {total_len} Zeichen)"
                )
            else:
                result.status = "WARN"
                result.message = f"Pfad erstellt, aber Inhalt abweichend"

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Zugriff für tiefen Pfad verweigert"
            else:
                result.status = "WARN"
                result.message = (
                    f"Langer Pfad nach {len(created)} Ebenen abgelehnt: {e}"
                )
        except Exception as e:
            result.status = "WARN"
            result.message = f"Langer Pfad: {type(e).__name__}: {e}"
        finally:
            try:
                self.conn.deleteFiles(share, f"{current}/test.txt")
            except Exception:
                pass
            for p in reversed(created):
                try:
                    self.conn.deleteDirectory(share, p)
                except Exception:
                    pass

    def test_reserved_names(self, result: TestResult, share: str):
        """Windows-reservierte Dateinamen testen (CON, PRN, NUL, etc.)."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "LPT1"]
        accepted = []
        rejected = []

        for name in reserved:
            fname = f"{self._prefix}_{name}.txt"
            try:
                self.conn.storeFile(share, f"/{fname}", io.BytesIO(b"reserved"))
                accepted.append(name)
                self._safe_delete(share, fname)
            except Exception:
                rejected.append(name)

        if accepted and rejected:
            result.status = "WARN"
            result.message = (
                f"Reservierte Namen: {len(accepted)} akzeptiert, "
                f"{len(rejected)} abgelehnt"
            )
        elif not accepted:
            result.status = "PASS"
            result.message = "Alle reservierten Namen korrekt abgelehnt"
        else:
            result.status = "PASS"
            result.message = (
                "Alle reservierten Namen akzeptiert "
                "(mit Prefix — Server-Verhalten korrekt)"
            )

        result.details["accepted"] = accepted
        result.details["rejected"] = rejected

    def test_dot_files(self, result: TestResult, share: str):
        """Versteckte Dateien (mit Punkt am Anfang) testen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        filename = f".{self._prefix}_hidden.txt"
        try:
            self.conn.storeFile(share, f"/{filename}", io.BytesIO(b"hidden"))

            entries = self.conn.listPath(share, "/")
            found = any(e.filename == filename for e in entries)

            if found:
                result.status = "PASS"
                result.message = "Dot-Datei erstellt und im Listing sichtbar"
            else:
                result.status = "WARN"
                result.message = "Dot-Datei erstellt, aber nicht im Listing"

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Dot-Datei fehlgeschlagen: {e}"
        finally:
            self._safe_delete(share, filename)

    # ─── 9. Timestamp-Tests ───────────────────────────────────

    def test_timestamps(self, result: TestResult, share: str):
        """Prüfe ob Timestamps nach Schreiben plausibel sind."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        filename = f"{self._prefix}_timestamp.txt"
        before = time.time()

        try:
            self.conn.storeFile(
                share, f"/{filename}",
                io.BytesIO(b"timestamp test"),
            )
            after = time.time()

            entries = self.conn.listPath(share, "/")
            target = next(
                (e for e in entries if e.filename == filename), None,
            )

            if not target:
                result.status = "WARN"
                result.message = "Datei nicht im Listing gefunden"
                return

            mtime = target.last_write_time
            ctime = target.create_time

            issues = []
            if mtime and (mtime < before - 60 or mtime > after + 60):
                issues.append(
                    f"Änderungszeit außerhalb Fenster: "
                    f"{datetime.fromtimestamp(mtime).isoformat()}"
                )
            if ctime and (ctime < before - 60 or ctime > after + 60):
                issues.append(
                    f"Erstellzeit außerhalb Fenster: "
                    f"{datetime.fromtimestamp(ctime).isoformat()}"
                )

            if not issues:
                result.status = "PASS"
                result.message = "Timestamps plausibel"
                if mtime:
                    result.details["mtime"] = datetime.fromtimestamp(mtime).isoformat()
                if ctime:
                    result.details["ctime"] = datetime.fromtimestamp(ctime).isoformat()
            else:
                result.status = "WARN"
                result.message = "; ".join(issues)

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = str(e)
        finally:
            self._safe_delete(share, filename)

    # ─── 10. Disconnect / Reconnect ──────────────────────────

    def test_reconnect(self, result: TestResult, share: str):
        """Verbindung trennen und erneut verbinden."""
        if not self.config.reconnect_test:
            result.status = "SKIP"
            result.message = "Reconnect-Test deaktiviert"
            return

        try:
            self.conn.close()
            self._log("Verbindung getrennt", "INFO")

            self.conn = self._new_connection()
            success = self.conn.connect(
                self.config.ip,
                self.config.port,
                timeout=self.config.timeout,
            )

            if not success:
                result.status = "FAIL"
                result.message = "Reconnect fehlgeschlagen"
                self.conn = None
                return

            entries = self.conn.listPath(share, "/")
            count = len([e for e in entries if e.filename not in (".", "..")])

            result.status = "PASS"
            result.message = f"Reconnect OK — {count} Einträge nach Neuverbindung"

        except Exception as e:
            result.status = "FAIL"
            result.message = f"Reconnect fehlgeschlagen: {e}"
            try:
                self.conn = self._new_connection()
                self.conn.connect(
                    self.config.ip, self.config.port,
                    timeout=self.config.timeout,
                )
            except Exception:
                self.conn = None

    def test_multi_session(self, result: TestResult, share: str):
        """Gleichzeitige Verbindungen zum selben Share."""
        conn2 = None
        try:
            conn2 = self._new_connection()
            success = conn2.connect(
                self.config.ip,
                self.config.port,
                timeout=self.config.timeout,
            )
            if not success:
                result.status = "FAIL"
                result.message = "Zweite Verbindung abgelehnt"
                return

            entries1 = self.conn.listPath(share, "/")
            entries2 = conn2.listPath(share, "/")

            names1 = sorted(e.filename for e in entries1)
            names2 = sorted(e.filename for e in entries2)

            if names1 == names2:
                result.status = "PASS"
                result.message = (
                    f"Zwei parallele Sessions sehen denselben Inhalt "
                    f"({len(names1)} Einträge)"
                )
            else:
                result.status = "WARN"
                result.message = (
                    f"Unterschiedliche Sicht: "
                    f"Session1={len(names1)}, Session2={len(names2)}"
                )

        except Exception as e:
            result.status = "FAIL"
            result.message = f"Multi-Session fehlgeschlagen: {e}"
        finally:
            if conn2:
                try:
                    conn2.close()
                except Exception:
                    pass

    def test_write_after_disconnect(self, result: TestResult, share: str):
        """Schreiben nach Disconnect der zweiten Session."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        filename = f"{self._prefix}_afterdisc.txt"
        conn2 = None

        try:
            conn2 = self._new_connection()
            conn2.connect(
                self.config.ip, self.config.port,
                timeout=self.config.timeout,
            )

            conn2.storeFile(share, f"/{filename}", io.BytesIO(b"session2"))

            conn2.close()
            conn2 = None

            buf = io.BytesIO()
            self.conn.retrieveFile(share, f"/{filename}", buf)

            if buf.getvalue() == b"session2":
                result.status = "PASS"
                result.message = "Datei von Session 2 über Session 1 lesbar nach Disconnect"
            else:
                result.status = "WARN"
                result.message = "Datei lesbar, aber Inhalt abweichend"

        except Exception as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Test fehlgeschlagen: {e}"
        finally:
            if conn2:
                try:
                    conn2.close()
                except Exception:
                    pass
            self._safe_delete(share, filename)

    # ─── 11. Zugriff / Concurrent ─────────────────────────────

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
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Zugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = str(e)
        finally:
            self._safe_delete(share, filename)

    def test_concurrent_write_different_files(self, result: TestResult, share: str):
        """Gleichzeitiges Schreiben verschiedener Dateien über 2 Sessions."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        file_a = f"{self._prefix}_conc_a.txt"
        file_b = f"{self._prefix}_conc_b.txt"
        conn2 = None

        try:
            conn2 = self._new_connection()
            conn2.connect(
                self.config.ip, self.config.port,
                timeout=self.config.timeout,
            )

            self.conn.storeFile(share, f"/{file_a}", io.BytesIO(b"von_session_1"))
            conn2.storeFile(share, f"/{file_b}", io.BytesIO(b"von_session_2"))

            buf_a = io.BytesIO()
            buf_b = io.BytesIO()
            self.conn.retrieveFile(share, f"/{file_a}", buf_a)
            self.conn.retrieveFile(share, f"/{file_b}", buf_b)

            if (buf_a.getvalue() == b"von_session_1"
                    and buf_b.getvalue() == b"von_session_2"):
                result.status = "PASS"
                result.message = "Paralleles Schreiben: beide Dateien korrekt"
            else:
                result.status = "WARN"
                result.message = "Paralleles Schreiben: Inhalt abweichend"

        except Exception as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Paralleles Schreiben fehlgeschlagen: {e}"
        finally:
            if conn2:
                try:
                    conn2.close()
                except Exception:
                    pass
            self._safe_delete(share, file_a)
            self._safe_delete(share, file_b)

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
                    except Exception as ex:
                        if is_access_denied(ex):
                            tree[e.filename + "/"] = {"_error": "🔒 Zugriff verweigert"}
                        else:
                            tree[e.filename + "/"] = {"_error": f"Fehler: {ex}"}
                else:
                    tree[e.filename] = f"{e.file_size} B"
        except Exception as ex:
            if is_access_denied(ex):
                tree["_error"] = "🔒 Zugriff verweigert"
            else:
                tree["_error"] = f"Fehler: {ex}"

    def _trim(self, tree, max_items):
        out = {}
        for i, (k, v) in enumerate(tree.items()):
            if i >= max_items:
                out["..."] = f"+{len(tree) - max_items} weitere"
                break
            out[k] = self._trim(v, 10) if isinstance(v, dict) else v
        return out

    # ─── 12. Benchmark ────────────────────────────────────────

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
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Kein Schreibzugriff für Benchmark"
            else:
                result.status = "SKIP"
                result.message = f"Kein Schreibzugriff für Benchmark: {e}"
        finally:
            self._safe_delete(share, filename)

    def test_many_small_files(self, result: TestResult, share: str):
        """Viele kleine Dateien erstellen/löschen (Throughput-Test)."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        count = 50
        dirname = f"{self._prefix}_manyfiles"
        files_created = []

        try:
            self.conn.createDirectory(share, dirname)

            t0 = time.perf_counter()
            for i in range(count):
                fname = f"{dirname}/file_{i:04d}.txt"
                self.conn.storeFile(
                    share, fname,
                    io.BytesIO(f"file {i}".encode()),
                )
                files_created.append(fname)
            create_time = time.perf_counter() - t0

            entries = self.conn.listPath(share, f"/{dirname}")
            real = [e for e in entries if e.filename not in (".", "..")]

            t0 = time.perf_counter()
            for fname in files_created:
                self.conn.deleteFiles(share, fname)
            delete_time = time.perf_counter() - t0

            result.status = "PASS"
            result.message = (
                f"{count} Dateien: Erstellen {create_time:.1f}s, "
                f"Listing {len(real)} Einträge, "
                f"Löschen {delete_time:.1f}s"
            )
            result.details = {
                "files": count,
                "create_time_s": round(create_time, 3),
                "delete_time_s": round(delete_time, 3),
                "listed": len(real),
                "create_per_sec": round(count / create_time, 1) if create_time else 0,
                "delete_per_sec": round(count / delete_time, 1) if delete_time else 0,
            }

        except OperationFailure as e:
            if is_access_denied(e):
                result.status = "DENIED"
                result.message = "🔒 Schreibzugriff für Massentest verweigert"
            else:
                result.status = "FAIL"
                result.message = f"Viele Dateien fehlgeschlagen: {e}"
        finally:
            for fname in files_created:
                try:
                    self.conn.deleteFiles(share, fname)
                except Exception:
                    pass
            try:
                self.conn.deleteDirectory(share, dirname)
            except Exception:
                pass

    # ─── Aufräumen ────────────────────────────────────────────

    def _safe_delete(self, share, filename):
        try:
            self.conn.deleteFiles(share, f"/{filename}")
        except Exception:
            pass

    def _cleanup_all(self, share: str):
        """Aggressives Aufräumen aller Test-Artefakte auf einem Share."""
        try:
            entries = self.conn.listPath(share, "/")
            for e in entries:
                if not e.filename.startswith(self._prefix.split("_")[0] + "_"):
                    continue
                if e.isDirectory:
                    self._recursive_cleanup(share, f"/{e.filename}")
                    try:
                        self.conn.deleteDirectory(share, e.filename)
                    except Exception:
                        pass
                else:
                    self._safe_delete(share, e.filename)
        except Exception:
            pass

    def _recursive_cleanup(self, share: str, path: str):
        """Rekursiv alle Dateien und Ordner unter path löschen."""
        try:
            entries = self.conn.listPath(share, path)
            for e in entries:
                if e.filename in (".", ".."):
                    continue
                full = f"{path.rstrip('/')}/{e.filename}"
                if e.isDirectory:
                    self._recursive_cleanup(share, full)
                    try:
                        self.conn.deleteDirectory(share, full.lstrip("/"))
                    except Exception:
                        pass
                else:
                    try:
                        self.conn.deleteFiles(share, full)
                    except Exception:
                        pass
        except Exception:
            pass

    # ─── Alle Tests ausführen ─────────────────────────────────

    def run_all(self) -> dict:
        w = 62
        print()
        print("=" * w)
        print("  SMB SERVER TESTER (Erweitert)")
        print("=" * w)
        print(f"  Ziel:     {self.config.ip}:{self.config.port}")
        print(f"  Benutzer: {self.config.username}"
              + (f"@{self.config.domain}" if self.config.domain else ""))
        print(f"  Datum:    {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        print("=" * w)

        # Phase 1: Netzwerk
        print("\n── Phase 1: Netzwerk ────────────────────────")
        self._run_test("DNS/IP Auflösung", "Netzwerk",
                       self.test_dns_resolution)
        net = self._run_test("TCP Erreichbarkeit", "Netzwerk",
                             self.test_tcp_reachability)
        self._run_test("NetBIOS Port 139", "Netzwerk",
                       self.test_port_139)
        if net.status != "PASS":
            print("\n  ❌ Server nicht erreichbar. Abbruch.")
            return self._summary()

        # Phase 2: Auth
        print("\n── Phase 2: Authentifizierung ───────────────")
        auth = self._run_test("Benutzer-Login", "Auth",
                              self.test_authentication)
        self._run_test("Gastzugang-Check", "Auth",
                       self.test_guest_access)
        self._run_test("Falsche Credentials", "Auth",
                       self.test_bad_credentials)
        self._run_test("Leeres Passwort", "Auth",
                       self.test_empty_password)

        if auth.status != "PASS":
            print("\n  ❌ Authentifizierung fehlgeschlagen. Abbruch.")
            return self._summary()

        # Phase 3: Shares
        print("\n── Phase 3: Share-Enumeration ───────────────")
        self._run_test("Share-Auflistung", "Shares",
                       self.test_share_enumeration)
        self._run_test("Admin-Shares ($)", "Shares",
                       self.test_admin_shares)

        shares = []
        if self.config.test_share:
            shares = [self.config.test_share]
        elif self.config.test_all_shares:
            shares = [
                s for s in self.discovered_shares
                if not s.endswith("$")
            ]

        if not shares:
            print("\n  ⚠️  Keine testbaren Disk-Shares gefunden.")
            if self.discovered_shares:
                print(f"  Vorhandene Shares: {', '.join(self.discovered_shares)}")
                print(f"  Nutze --share NAME um einen direkt zu testen.")
            return self._summary()

        # ── Pro-Share: Berechtigungsprüfung + Tests ───────────
        for share in shares:

            # ═══ Berechtigungsprüfung ═══
            print(f"\n{'═' * w}")
            print(f"  SHARE: {share}")
            print(f"{'═' * w}")
            print(f"\n── Berechtigungsprüfung für '{share}' ──────")

            # Sicherstellen dass Verbindung steht
            if not self._ensure_connection():
                print(f"  ❌ Verbindung verloren, überspringe Share '{share}'")
                self._skip_test(
                    f"[{share}] Alle Tests", "Permissions",
                    "Verbindung verloren",
                )
                continue

            perms = self.probe_share_permissions(share)
            print(f"\n  {perms.summary_line()}")
            print()

            # Share nicht erreichbar → alle Tests überspringen
            if not perms.accessible:
                print(f"  🔒 Share '{share}' nicht erreichbar: "
                      f"{perms.error_message}")
                print(f"     Alle Tests für diesen Share werden übersprungen.\n")
                self._denied_test(
                    f"[{share}] Share-Zugriff", "Permissions",
                    perms.error_message,
                )
                continue

            # ── Read-Tests (nur wenn Leserecht) ───────────────
            if perms.can_read:
                print(f"\n── Phase 4: Lesen auf '{share}' ──────────────")
                self._run_test(f"[{share}] Lesezugriff", "Read",
                               self.test_read_access, share)
                self._run_test(f"[{share}] Detail-Listing", "Read",
                               self.test_directory_listing_details, share)
                self._run_test(f"[{share}] Datei lesen", "Read",
                               self.test_file_read_content, share)
                self._run_test(f"[{share}] Nicht-exist. Datei lesen", "Read",
                               self.test_read_nonexistent_file, share)
                self._run_test(f"[{share}] Nicht-exist. Ordner lesen", "Read",
                               self.test_read_nonexistent_directory, share)
                self._run_test(f"[{share}] Verzeichnis-Scan", "Read",
                               self.test_deep_traversal, share)
            else:
                print(f"\n── Phase 4: Lesen auf '{share}' ──────────────")
                self._denied_test(
                    f"[{share}] Alle Lese-Tests", "Read",
                    "Kein Lesezugriff auf diesen Share",
                )

            # ── Write-Tests (nur wenn Schreibrecht) ───────────
            if perms.can_write and self.config.write_test:
                print(f"\n── Phase 5: Schreiben auf '{share}' ─────────")
                self._run_test(f"[{share}] Datei schreiben", "Write",
                               self.test_write_file, share)
                self._run_test(f"[{share}] 0-Byte-Datei", "Write",
                               self.test_write_zero_byte_file, share)
                self._run_test(f"[{share}] Grosse Datei (5 MB)", "Write",
                               self.test_write_large_content, share)
                self._run_test(f"[{share}] Ordner erstellen", "Write",
                               self.test_create_directory, share)
                self._run_test(f"[{share}] Doppelter Ordner", "Write",
                               self.test_create_duplicate_directory, share)
                self._run_test(f"[{share}] Verschachtelte Ordner", "Write",
                               self.test_nested_directories, share)
                self._run_test(f"[{share}] Datei ueberschreiben", "Write",
                               self.test_overwrite_file, share)
                self._run_test(f"[{share}] Ueberschreiben (kleiner)", "Write",
                               self.test_overwrite_with_smaller, share)
            elif not self.config.write_test:
                print(f"\n── Phase 5: Schreiben auf '{share}' ─────────")
                self._skip_test(
                    f"[{share}] Alle Schreib-Tests", "Write",
                    "Schreibtests deaktiviert (--no-write)",
                )
            else:
                print(f"\n── Phase 5: Schreiben auf '{share}' ─────────")
                self._denied_test(
                    f"[{share}] Alle Schreib-Tests", "Write",
                    "Kein Schreibzugriff auf diesen Share",
                )

            # ── Lösch-Tests (nur wenn Löschrecht) ─────────────
            if perms.can_write and perms.can_delete and self.config.delete_test:
                print(f"\n── Phase 6: Löschen auf '{share}' ──────────")
                self._run_test(f"[{share}] Datei loeschen", "Delete",
                               self.test_delete_file, share)
                self._run_test(f"[{share}] Nicht-exist. Datei loeschen", "Delete",
                               self.test_delete_nonexistent_file, share)
                self._run_test(f"[{share}] Leeren Ordner loeschen", "Delete",
                               self.test_delete_empty_directory, share)
                self._run_test(f"[{share}] Nicht-leeren Ordner loeschen", "Delete",
                               self.test_delete_nonempty_directory, share)
                self._run_test(f"[{share}] Nicht-exist. Ordner loeschen", "Delete",
                               self.test_delete_nonexistent_directory, share)
                self._run_test(f"[{share}] Rekursives Loeschen", "Delete",
                               self.test_recursive_delete, share)
                self._run_test(f"[{share}] Wildcard-Loeschen", "Delete",
                               self.test_delete_multiple_files_pattern, share)
            elif not self.config.delete_test:
                print(f"\n── Phase 6: Löschen auf '{share}' ──────────")
                self._skip_test(
                    f"[{share}] Alle Lösch-Tests", "Delete",
                    "Löschtests deaktiviert (--no-write)",
                )
            elif not perms.can_write:
                print(f"\n── Phase 6: Löschen auf '{share}' ──────────")
                self._denied_test(
                    f"[{share}] Alle Lösch-Tests", "Delete",
                    "Kein Schreibzugriff — Lösch-Tests nicht möglich",
                )
            else:
                print(f"\n── Phase 6: Löschen auf '{share}' ──────────")
                self._denied_test(
                    f"[{share}] Alle Lösch-Tests", "Delete",
                    "Kein Löschrecht auf diesen Share",
                )

            # ── Umbenennen / Verschieben (nur wenn Rename-Recht) ──
            if perms.can_write and perms.can_rename and self.config.write_test:
                print(f"\n── Phase 7: Umbenennen auf '{share}' ───────")
                self._run_test(f"[{share}] Datei umbenennen", "Rename",
                               self.test_rename_file, share)
                self._run_test(f"[{share}] Datei verschieben", "Rename",
                               self.test_move_file_to_subdirectory, share)
                self._run_test(f"[{share}] Ordner umbenennen", "Rename",
                               self.test_rename_directory, share)
                self._run_test(f"[{share}] Rename auf existierend", "Rename",
                               self.test_rename_to_existing, share)
            elif not self.config.write_test:
                print(f"\n── Phase 7: Umbenennen auf '{share}' ───────")
                self._skip_test(
                    f"[{share}] Alle Rename-Tests", "Rename",
                    "Schreibtests deaktiviert (--no-write)",
                )
            elif not perms.can_write:
                print(f"\n── Phase 7: Umbenennen auf '{share}' ───────")
                self._denied_test(
                    f"[{share}] Alle Rename-Tests", "Rename",
                    "Kein Schreibzugriff — Rename nicht möglich",
                )
            else:
                print(f"\n── Phase 7: Umbenennen auf '{share}' ───────")
                self._denied_test(
                    f"[{share}] Alle Rename-Tests", "Rename",
                    "Kein Umbennennungsrecht auf diesen Share",
                )

            # ── Sonderzeichen & Edge-Cases (braucht Write) ────
            if perms.can_write and self.config.write_test:
                print(f"\n── Phase 8: Edge-Cases auf '{share}' ───────")
                self._run_test(f"[{share}] Sonderzeichen", "EdgeCase",
                               self.test_special_characters, share)
                self._run_test(f"[{share}] Unicode-Dateinamen", "EdgeCase",
                               self.test_unicode_filenames, share)
                self._run_test(f"[{share}] Langer Dateiname", "EdgeCase",
                               self.test_long_filename, share)
                self._run_test(f"[{share}] Langer Pfad", "EdgeCase",
                               self.test_long_path, share)
                self._run_test(f"[{share}] Reservierte Namen", "EdgeCase",
                               self.test_reserved_names, share)
                self._run_test(f"[{share}] Dot-Dateien", "EdgeCase",
                               self.test_dot_files, share)
                self._run_test(f"[{share}] Timestamps", "EdgeCase",
                               self.test_timestamps, share)
            elif not self.config.write_test:
                print(f"\n── Phase 8: Edge-Cases auf '{share}' ───────")
                self._skip_test(
                    f"[{share}] Alle Edge-Case-Tests", "EdgeCase",
                    "Schreibtests deaktiviert (--no-write)",
                )
            else:
                print(f"\n── Phase 8: Edge-Cases auf '{share}' ───────")
                self._denied_test(
                    f"[{share}] Alle Edge-Case-Tests", "EdgeCase",
                    "Kein Schreibzugriff — Edge-Case-Tests benötigen Schreibrecht",
                )

            # ── Concurrent / Sessions (Read reicht für manche) ──
            if perms.can_read:
                print(f"\n── Phase 9: Sessions auf '{share}' ─────────")
                if perms.can_write and self.config.write_test:
                    self._run_test(f"[{share}] Mehrfach-Lesen", "Session",
                                   self.test_concurrent_read, share)
                    self._run_test(f"[{share}] Paralleles Schreiben", "Session",
                                   self.test_concurrent_write_different_files, share)
                else:
                    self._skip_test(
                        f"[{share}] Mehrfach-Lesen", "Session",
                        "Benötigt Schreibrecht (Testdatei erstellen)",
                    )
                    self._skip_test(
                        f"[{share}] Paralleles Schreiben", "Session",
                        "Kein Schreibzugriff",
                    )

                self._run_test(f"[{share}] Multi-Session", "Session",
                               self.test_multi_session, share)

                if perms.can_write and self.config.write_test:
                    self._run_test(f"[{share}] Schreiben nach Disconnect", "Session",
                                   self.test_write_after_disconnect, share)
                else:
                    self._skip_test(
                        f"[{share}] Schreiben nach Disconnect", "Session",
                        "Kein Schreibzugriff",
                    )

                self._run_test(f"[{share}] Reconnect", "Session",
                               self.test_reconnect, share)
            else:
                print(f"\n── Phase 9: Sessions auf '{share}' ─────────")
                self._denied_test(
                    f"[{share}] Alle Session-Tests", "Session",
                    "Kein Lesezugriff auf diesen Share",
                )

            # ── Benchmark (braucht Write) ─────────────────────
            if perms.can_write and self.config.write_test and self.config.benchmark:
                print(f"\n── Phase 10: Benchmark auf '{share}' ───────")
                self._run_test(f"[{share}] Transfer-Benchmark", "Bench",
                               self.test_benchmark, share)
                self._run_test(f"[{share}] Viele kleine Dateien", "Bench",
                               self.test_many_small_files, share)
            elif not self.config.benchmark:
                print(f"\n── Phase 10: Benchmark auf '{share}' ───────")
                self._skip_test(
                    f"[{share}] Alle Benchmark-Tests", "Bench",
                    "Benchmark deaktiviert (--no-bench)",
                )
            elif not perms.can_write:
                print(f"\n── Phase 10: Benchmark auf '{share}' ───────")
                self._denied_test(
                    f"[{share}] Alle Benchmark-Tests", "Bench",
                    "Kein Schreibzugriff — Benchmark nicht möglich",
                )
            else:
                print(f"\n── Phase 10: Benchmark auf '{share}' ───────")
                self._skip_test(
                    f"[{share}] Alle Benchmark-Tests", "Bench",
                    "Schreibtests deaktiviert (--no-write)",
                )

            # ── Aufräumen ─────────────────────────────────────
            if perms.accessible:
                print(f"\n  Räume Testartefakte auf '{share}' auf...")
                self._cleanup_all(share)

        # Verbindung schliessen
        if self.conn:
            try:
                self.conn.close()
            except Exception:
                pass

        return self._summary()

    # ─── Zusammenfassung ──────────────────────────────────────

    def _summary(self) -> dict:
        counts = {"PASS": 0, "FAIL": 0, "WARN": 0, "SKIP": 0, "DENIED": 0}
        for r in self.results:
            counts[r.status] = counts.get(r.status, 0) + 1

        # Ergebnisse nach Kategorie gruppieren
        categories = {}
        for r in self.results:
            cat = r.category
            if cat not in categories:
                categories[cat] = {
                    "PASS": 0, "FAIL": 0, "WARN": 0, "SKIP": 0, "DENIED": 0,
                }
            categories[cat][r.status] = categories[cat].get(r.status, 0) + 1

        w = 62
        print()
        print("=" * w)
        print("  ERGEBNIS")
        print("=" * w)
        print(f"  Gesamt:        {len(self.results)} Tests")
        print(f"  ✅ Bestanden:    {counts['PASS']}")
        print(f"  ❌ Fehler:       {counts['FAIL']}")
        print(f"  ⚠️ Warnungen:    {counts['WARN']}")
        print(f"  🔒 Verweigert:   {counts['DENIED']}")
        print(f"  ⏭️ Übersprungen: {counts['SKIP']}")
        print("-" * w)

        # Berechtigungs-Übersicht
        if self.share_permissions:
            print()
            print("  BERECHTIGUNGEN PRO SHARE:")
            print("  " + "─" * (w - 2))
            for share_name, perms in self.share_permissions.items():
                print(f"  {perms.summary_line()}")
            print("  " + "─" * (w - 2))
            print()

        # Pro-Kategorie Zusammenfassung
        print("  Ergebnisse nach Kategorie:")
        for cat, cnts in categories.items():
            total = sum(cnts.values())
            parts = []
            if cnts.get("PASS", 0):
                parts.append(f"✅ {cnts['PASS']}")
            if cnts.get("FAIL", 0):
                parts.append(f"❌ {cnts['FAIL']}")
            if cnts.get("WARN", 0):
                parts.append(f"⚠️ {cnts['WARN']}")
            if cnts.get("DENIED", 0):
                parts.append(f"🔒 {cnts['DENIED']}")
            if cnts.get("SKIP", 0):
                parts.append(f"⏭️ {cnts['SKIP']}")
            print(f"    {cat:<12} {total:>3} Tests | {' '.join(parts)}")
        print("-" * w)

        if counts["FAIL"] == 0 and counts["WARN"] == 0 and counts["DENIED"] == 0:
            print("  ✅ Alle Tests bestanden!")
        elif counts["FAIL"] == 0 and counts["DENIED"] == 0:
            print("  ⚠️  Keine Fehler, aber Warnungen beachten.")
        elif counts["FAIL"] == 0:
            print("  🔒 Keine technischen Fehler, aber fehlende Berechtigungen.")
        else:
            print("  ❌ Fehler gefunden — Details oben prüfen.")

        failed = [r for r in self.results if r.status == "FAIL"]
        if failed:
            print()
            print("  Fehlgeschlagene Tests:")
            for r in failed:
                print(f"    ❌ {r.name}: {r.message}")

        denied = [r for r in self.results if r.status == "DENIED"]
        if denied:
            print()
            print("  Fehlende Berechtigungen:")
            for r in denied:
                print(f"    🔒 {r.name}: {r.message}")

        warned = [r for r in self.results if r.status == "WARN"]
        if warned:
            print()
            print("  Warnungen:")
            for r in warned:
                print(f"    ⚠️  {r.name}: {r.message}")

        print("=" * w)
        print()

        return {
            "target": f"{self.config.ip}:{self.config.port}",
            "username": self.config.username,
            "timestamp": datetime.now().isoformat(),
            "counts": counts,
            "categories": categories,
            "results": [asdict(r) for r in self.results],
            "discovered_shares": self.discovered_shares,
            "share_permissions": {
                k: asdict(v) for k, v in self.share_permissions.items()
            },
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

    admin = input("  Admin-Shares ($) testen? [j/N]: ").strip().lower()
    config.test_admin_shares = admin == "j"

    verbose = input("  Verbose? [j/N]: ").strip().lower()
    config.verbose = verbose == "j"

    return config


# ═══════════════════════════════════════════════════════════════
# CLI
# ═══════════════════════════════════════════════════════════════

def parse_args() -> Optional[SMBConfig]:
    parser = argparse.ArgumentParser(
        description="SMB Server Tester — Umfassende SMB-Tests (Erweitert)",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Beispiele:
  %(prog)s --ip 192.168.1.10 --user admin --pass secret
  %(prog)s --ip server --user DOMAIN\\\\user --share SharedDocs
  %(prog)s --config my_config.json
  %(prog)s --ip 10.0.0.5 --user admin --test-admin-shares
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
    parser.add_argument("--no-write", action="store_true",
                        help="Schreib-/Lösch-/Rename-Tests deaktivieren")
    parser.add_argument("--no-bench", action="store_true",
                        help="Benchmark deaktivieren")
    parser.add_argument("--no-bad-creds", action="store_true",
                        help="Tests mit falschen Credentials deaktivieren")
    parser.add_argument("--no-reconnect", action="store_true",
                        help="Reconnect-Tests deaktivieren")
    parser.add_argument("--test-admin-shares", action="store_true",
                        help="Admin-Shares ($) testen")
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
        test_admin_shares=args.test_admin_shares,
        test_bad_credentials=not args.no_bad_creds,
        reconnect_test=not args.no_reconnect,
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