#!/usr/bin/env python3
"""
╔══════════════════════════════════════════════════════════════╗
║                    SMB SERVER TESTER                        ║
║          Comprehensive SMB Share Testing Suite              ║
╚══════════════════════════════════════════════════════════════╝

Testet dynamisch alle Aspekte eines SMB-Servers:
  - Verbindung & Authentifizierung
  - Share-Enumeration
  - Ordner-/Datei-Zugriff (Read/Write/Delete)
  - Berechtigungen
  - Protokoll-Negotiation
  - Performance-Benchmarks

Abhängigkeiten:  pip install smbprotocol

Verwendung:
  python smb_tester.py                          # Interaktiver Modus
  python smb_tester.py --ip 192.168.1.10 ...    # CLI-Modus
  python smb_tester.py --config config.json      # Config-Datei
"""

import argparse
import json
import os
import sys
import time
import uuid
import socket
import getpass
import traceback
from datetime import datetime
from pathlib import PurePosixPath
from dataclasses import dataclass, field, asdict
from typing import Optional

# ─── Dependency Check ────────────────────────────────────────
try:
    from smbclient import (
        register_session,
        listdir,
        mkdir,
        rmdir,
        open_file,
        remove,
        stat,
        scandir,
    )
    from smbclient._os import SMBDirEntry
    from smbprotocol.session import Session
    from smbprotocol.connection import Connection
    from smbprotocol.exceptions import SMBException
except ImportError:
    print("\n[!] Fehlende Abhängigkeit: smbprotocol")
    print("    Installieren mit:  pip install smbprotocol")
    print("    Optional für Kerberos: pip install smbprotocol[kerberos]\n")
    sys.exit(1)


# ═══════════════════════════════════════════════════════════════
# Datenklassen
# ═══════════════════════════════════════════════════════════════

@dataclass
class TestResult:
    name: str
    category: str
    status: str = "PENDING"       # PASS, FAIL, WARN, SKIP, PENDING
    message: str = ""
    duration_ms: float = 0.0
    details: dict = field(default_factory=dict)

    @property
    def icon(self):
        return {
            "PASS": "✅", "FAIL": "❌", "WARN": "⚠️",
            "SKIP": "⏭️", "PENDING": "⏳"
        }.get(self.status, "❓")


@dataclass
class SMBConfig:
    ip: str = ""
    port: int = 445
    username: str = ""
    password: str = ""
    domain: str = ""
    test_share: str = ""          # Spezifischer Share zum Testen
    test_all_shares: bool = True  # Alle Shares testen
    write_test: bool = True       # Schreibtests durchführen
    delete_test: bool = True      # Löschtests durchführen
    benchmark: bool = True        # Performance-Tests
    benchmark_size_kb: int = 1024 # Benchmark-Dateigröße in KB
    timeout: int = 30             # Verbindungs-Timeout
    verbose: bool = False


# ═══════════════════════════════════════════════════════════════
# Haupt-Tester-Klasse
# ═══════════════════════════════════════════════════════════════

class SMBTester:
    def __init__(self, config: SMBConfig):
        self.config = config
        self.results: list[TestResult] = []
        self.discovered_shares: list[str] = []
        self.session_registered = False
        self._test_prefix = f"_smbtest_{uuid.uuid4().hex[:8]}"

    # ─── Hilfsfunktionen ──────────────────────────────────────

    def _log(self, msg: str, level: str = "INFO"):
        if self.config.verbose or level in ("ERROR", "WARN"):
            ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
            print(f"  [{ts}] [{level}] {msg}")

    def _run_test(self, name: str, category: str, func, *args, **kwargs) -> TestResult:
        result = TestResult(name=name, category=category)
        t0 = time.perf_counter()
        try:
            func(result, *args, **kwargs)
        except SMBException as e:
            result.status = "FAIL"
            result.message = f"SMB-Fehler: {e}"
            self._log(f"{name}: {e}", "ERROR")
        except PermissionError as e:
            result.status = "FAIL"
            result.message = f"Zugriff verweigert: {e}"
        except Exception as e:
            result.status = "FAIL"
            result.message = f"Fehler: {type(e).__name__}: {e}"
            if self.config.verbose:
                traceback.print_exc()
        finally:
            result.duration_ms = (time.perf_counter() - t0) * 1000
            self.results.append(result)
            icon = result.icon
            print(f"  {icon}  {name}: {result.status} ({result.duration_ms:.0f}ms)"
                  + (f" — {result.message}" if result.message else ""))
        return result

    def _smb_path(self, share: str, *parts: str) -> str:
        base = f"\\\\{self.config.ip}\\{share}"
        for p in parts:
            base += f"\\{p}"
        return base

    # ─── 1. Netzwerk-Tests ────────────────────────────────────

    def test_network_reachability(self, result: TestResult):
        """TCP-Verbindung zum SMB-Port prüfen."""
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(self.config.timeout)
        try:
            sock.connect((self.config.ip, self.config.port))
            result.status = "PASS"
            result.message = f"Port {self.config.port} erreichbar"
        except socket.timeout:
            result.status = "FAIL"
            result.message = f"Timeout nach {self.config.timeout}s"
        except ConnectionRefusedError:
            result.status = "FAIL"
            result.message = f"Verbindung abgelehnt auf Port {self.config.port}"
        except OSError as e:
            result.status = "FAIL"
            result.message = f"Netzwerkfehler: {e}"
        finally:
            sock.close()

    def test_dns_resolution(self, result: TestResult):
        """Hostname-Auflösung testen."""
        try:
            resolved = socket.gethostbyname(self.config.ip)
            result.status = "PASS"
            result.message = f"{self.config.ip} → {resolved}"
            result.details["resolved_ip"] = resolved
        except socket.gaierror:
            # Könnte eine direkte IP sein – das ist ok
            try:
                socket.inet_aton(self.config.ip)
                result.status = "PASS"
                result.message = f"Direkte IP-Adresse: {self.config.ip}"
            except socket.error:
                result.status = "FAIL"
                result.message = f"Kann '{self.config.ip}' nicht auflösen"

    # ─── 2. Authentifizierung ─────────────────────────────────

    def test_authentication(self, result: TestResult):
        """SMB-Session registrieren und Authentifizierung prüfen."""
        try:
            register_session(
                self.config.ip,
                username=self.config.username,
                password=self.config.password,
                port=self.config.port,
                connection_timeout=self.config.timeout,
            )
            self.session_registered = True
            result.status = "PASS"
            result.message = f"Authentifiziert als '{self.config.username}'"
        except Exception as e:
            result.status = "FAIL"
            result.message = f"Authentifizierung fehlgeschlagen: {e}"

    def test_guest_access(self, result: TestResult):
        """Testen ob Gastzugang möglich ist."""
        try:
            register_session(
                self.config.ip,
                username="guest",
                password="",
                port=self.config.port,
                connection_timeout=self.config.timeout,
            )
            result.status = "WARN"
            result.message = "Gastzugang ist aktiviert (Sicherheitsrisiko!)"
        except Exception:
            result.status = "PASS"
            result.message = "Gastzugang ist deaktiviert"

    # ─── 3. Share-Enumeration ─────────────────────────────────

    def test_share_enumeration(self, result: TestResult):
        """Verfügbare Shares auflisten."""
        if not self.session_registered:
            result.status = "SKIP"
            result.message = "Keine aktive Session"
            return

        try:
            # smbclient hat keine direkte share-list API,
            # daher nutzen wir smbprotocol direkt
            from smbprotocol.tree import TreeConnect
            from smbprotocol.open import (
                Open, CreateDisposition, FileAttributes,
                ShareAccess, CreateOptions, ImpersonationLevel,
                FilePipePrinterAccessMask
            )
            from smbprotocol.ioctl import (
                SMB2IOCTLRequest, CtlCode, SMB2IOCTLResponse
            )
            import smbclient

            # Versuche über IPC$ und srvsvc
            shares = []
            try:
                entries = listdir(self._smb_path("IPC$"))
                # Fallback: Versuche bekannte Standard-Shares
            except Exception:
                pass

            # Fallback: Versuche gängige Share-Namen
            common_shares = [
                "C$", "D$", "E$", "ADMIN$", "IPC$", "print$",
                "NETLOGON", "SYSVOL", "Users", "Public", "Shared",
                "Data", "Backup", "Home", "homes", "public",
                "shared", "files", "documents", "media",
            ]

            if self.config.test_share:
                common_shares.insert(0, self.config.test_share)

            for share_name in common_shares:
                try:
                    path = self._smb_path(share_name)
                    listdir(path)
                    shares.append(share_name)
                    self._log(f"Share gefunden: {share_name}")
                except PermissionError:
                    shares.append(f"{share_name} (kein Zugriff)")
                except Exception:
                    pass  # Share existiert nicht

            self.discovered_shares = [
                s for s in shares if "(kein Zugriff)" not in s
            ]

            if shares:
                result.status = "PASS"
                result.message = f"{len(shares)} Share(s) gefunden"
                result.details["shares"] = shares
            else:
                result.status = "WARN"
                result.message = "Keine Shares gefunden oder Enumeration blockiert"

        except Exception as e:
            result.status = "FAIL"
            result.message = f"Share-Enumeration fehlgeschlagen: {e}"

    # ─── 4. Share-Zugriffstests ───────────────────────────────

    def test_share_read_access(self, result: TestResult, share: str):
        """Lesezugriff auf einen Share testen."""
        try:
            path = self._smb_path(share)
            entries = listdir(path)
            result.status = "PASS"
            result.message = f"{len(entries)} Einträge in \\\\{self.config.ip}\\{share}"
            result.details["entry_count"] = len(entries)
            result.details["entries_sample"] = entries[:20]
        except PermissionError:
            result.status = "FAIL"
            result.message = f"Lesezugriff verweigert auf {share}"
        except Exception as e:
            result.status = "FAIL"
            result.message = str(e)

    def test_share_directory_listing(self, result: TestResult, share: str):
        """Detailliertes Verzeichnislisting mit Metadaten."""
        try:
            path = self._smb_path(share)
            items = []
            for entry in scandir(path):
                try:
                    info = {
                        "name": entry.name,
                        "is_dir": entry.is_dir(),
                        "is_file": entry.is_file(),
                    }
                    try:
                        st = entry.stat()
                        info["size"] = st.st_size
                        info["modified"] = datetime.fromtimestamp(
                            st.st_mtime
                        ).isoformat()
                    except Exception:
                        pass
                    items.append(info)
                except Exception:
                    items.append({"name": entry.name, "error": "Metadaten nicht lesbar"})

            result.status = "PASS"
            result.message = f"{len(items)} Einträge mit Metadaten gelesen"
            result.details["items"] = items[:50]
        except Exception as e:
            result.status = "FAIL"
            result.message = str(e)

    def test_share_write_access(self, result: TestResult, share: str):
        """Schreibzugriff testen (Ordner und Datei erstellen)."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        test_dir = f"{self._test_prefix}_dir"
        test_file = f"{self._test_prefix}_file.txt"
        test_content = f"SMB Tester — Schreibtest {datetime.now().isoformat()}"
        created_dir = False
        created_file = False

        try:
            # Ordner erstellen
            dir_path = self._smb_path(share, test_dir)
            mkdir(dir_path)
            created_dir = True
            self._log(f"Ordner erstellt: {test_dir}")

            # Datei erstellen und schreiben
            file_path = self._smb_path(share, test_dir, test_file)
            with open_file(file_path, mode="w") as f:
                f.write(test_content)
            created_file = True
            self._log(f"Datei geschrieben: {test_file}")

            # Datei zurücklesen und verifizieren
            with open_file(file_path, mode="r") as f:
                read_back = f.read()

            if read_back == test_content:
                result.status = "PASS"
                result.message = f"Schreiben+Lesen verifiziert auf {share}"
            else:
                result.status = "WARN"
                result.message = "Geschrieben, aber gelesener Inhalt weicht ab"

        except PermissionError:
            result.status = "FAIL"
            result.message = f"Schreibzugriff verweigert auf {share}"
        except Exception as e:
            result.status = "FAIL"
            result.message = str(e)
        finally:
            # Aufräumen
            self._cleanup(share, test_dir, test_file, created_dir, created_file)

    def test_share_delete_access(self, result: TestResult, share: str):
        """Löschberechtigung testen."""
        if not self.config.delete_test:
            result.status = "SKIP"
            result.message = "Löschtest deaktiviert"
            return

        test_file = f"{self._test_prefix}_deltest.txt"
        try:
            file_path = self._smb_path(share, test_file)
            with open_file(file_path, mode="w") as f:
                f.write("delete test")

            remove(file_path)
            result.status = "PASS"
            result.message = f"Datei erstellt und gelöscht auf {share}"

        except PermissionError:
            result.status = "FAIL"
            result.message = f"Löschberechtigung fehlt auf {share}"
            # Aufräumen versuchen
            try:
                remove(self._smb_path(share, test_file))
            except Exception:
                pass
        except Exception as e:
            result.status = "FAIL"
            result.message = str(e)

    def test_share_large_file(self, result: TestResult, share: str):
        """Große Datei schreiben/lesen (Performance-Benchmark)."""
        if not self.config.benchmark or not self.config.write_test:
            result.status = "SKIP"
            result.message = "Benchmark deaktiviert"
            return

        size_bytes = self.config.benchmark_size_kb * 1024
        test_file = f"{self._test_prefix}_bench.bin"
        data = os.urandom(size_bytes)

        try:
            file_path = self._smb_path(share, test_file)

            # Schreib-Benchmark
            t0 = time.perf_counter()
            with open_file(file_path, mode="wb") as f:
                f.write(data)
            write_time = time.perf_counter() - t0
            write_speed = (size_bytes / 1024 / 1024) / write_time if write_time > 0 else 0

            # Lese-Benchmark
            t0 = time.perf_counter()
            with open_file(file_path, mode="rb") as f:
                read_data = f.read()
            read_time = time.perf_counter() - t0
            read_speed = (size_bytes / 1024 / 1024) / read_time if read_time > 0 else 0

            # Integrität prüfen
            integrity = read_data == data

            result.status = "PASS" if integrity else "WARN"
            result.message = (
                f"Write: {write_speed:.1f} MB/s | "
                f"Read: {read_speed:.1f} MB/s | "
                f"Größe: {self.config.benchmark_size_kb} KB | "
                f"Integrität: {'OK' if integrity else 'FEHLER'}"
            )
            result.details = {
                "write_speed_mbps": round(write_speed, 2),
                "read_speed_mbps": round(read_speed, 2),
                "write_time_s": round(write_time, 3),
                "read_time_s": round(read_time, 3),
                "size_kb": self.config.benchmark_size_kb,
                "integrity_ok": integrity,
            }

        except PermissionError:
            result.status = "SKIP"
            result.message = f"Kein Schreibzugriff für Benchmark auf {share}"
        except Exception as e:
            result.status = "FAIL"
            result.message = str(e)
        finally:
            try:
                remove(self._smb_path(share, test_file))
            except Exception:
                pass

    def test_deep_directory_traversal(self, result: TestResult, share: str):
        """Rekursive Verzeichnisstruktur testen (max. 3 Ebenen)."""
        try:
            tree = {}
            self._traverse(share, "", tree, depth=0, max_depth=3)
            total_items = self._count_tree(tree)
            result.status = "PASS"
            result.message = f"{total_items} Einträge in 3 Ebenen gefunden"
            result.details["tree_sample"] = self._trim_tree(tree, max_items=30)
        except PermissionError:
            result.status = "FAIL"
            result.message = "Zugriff verweigert bei Traversierung"
        except Exception as e:
            result.status = "FAIL"
            result.message = str(e)

    def _traverse(self, share, subpath, tree, depth, max_depth):
        if depth >= max_depth:
            return
        path = self._smb_path(share, subpath) if subpath else self._smb_path(share)
        try:
            for entry_name in listdir(path):
                full = f"{subpath}\\{entry_name}" if subpath else entry_name
                full_path = self._smb_path(share, full)
                try:
                    st = stat(full_path)
                    # Prüfe ob Verzeichnis (bit 0x10 in st_file_attributes)
                    is_dir = hasattr(st, 'st_file_attributes') and (st.st_file_attributes & 0x10)
                    if not is_dir:
                        # Fallback
                        try:
                            listdir(full_path)
                            is_dir = True
                        except Exception:
                            is_dir = False
                except Exception:
                    is_dir = False

                if is_dir:
                    tree[entry_name] = {}
                    try:
                        self._traverse(share, full, tree[entry_name], depth + 1, max_depth)
                    except Exception:
                        tree[entry_name] = {"_error": "Zugriff verweigert"}
                else:
                    tree[entry_name] = "file"
        except PermissionError:
            tree["_error"] = "Zugriff verweigert"

    def _count_tree(self, tree):
        count = 0
        for v in tree.values():
            count += 1
            if isinstance(v, dict):
                count += self._count_tree(v)
        return count

    def _trim_tree(self, tree, max_items=30):
        trimmed = {}
        for i, (k, v) in enumerate(tree.items()):
            if i >= max_items:
                trimmed["..."] = f"und {len(tree) - max_items} weitere"
                break
            if isinstance(v, dict):
                trimmed[k] = self._trim_tree(v, max_items=10)
            else:
                trimmed[k] = v
        return trimmed

    def test_special_characters(self, result: TestResult, share: str):
        """Dateien mit Sonderzeichen im Namen testen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        test_names = [
            f"{self._test_prefix} leerzeichen.txt",
            f"{self._test_prefix}_ÄÖÜ_ß.txt",
            f"{self._test_prefix}_特殊.txt",
            f"{self._test_prefix}_(klammern).txt",
        ]

        passed = []
        failed = []

        for name in test_names:
            try:
                path = self._smb_path(share, name)
                with open_file(path, mode="w") as f:
                    f.write("test")
                remove(path)
                passed.append(name.replace(self._test_prefix, ""))
            except Exception as e:
                failed.append(f"{name.replace(self._test_prefix, '')}: {e}")

        if not failed:
            result.status = "PASS"
            result.message = f"Alle {len(passed)} Sonderzeichen-Tests bestanden"
        elif passed:
            result.status = "WARN"
            result.message = f"{len(passed)} OK, {len(failed)} fehlgeschlagen"
            result.details["failed"] = failed
        else:
            result.status = "FAIL"
            result.message = "Alle Sonderzeichen-Tests fehlgeschlagen"
            result.details["failed"] = failed

    def test_concurrent_access(self, result: TestResult, share: str):
        """Simultanen Dateizugriff testen."""
        if not self.config.write_test:
            result.status = "SKIP"
            result.message = "Schreibtest deaktiviert"
            return

        test_file = f"{self._test_prefix}_concurrent.txt"
        try:
            path = self._smb_path(share, test_file)

            # Datei erstellen
            with open_file(path, mode="w") as f:
                f.write("initial content")

            # Versuche gleichzeitiges Lesen
            with open_file(path, mode="r") as f1:
                with open_file(path, mode="r") as f2:
                    data1 = f1.read()
                    data2 = f2.read()

            if data1 == data2 == "initial content":
                result.status = "PASS"
                result.message = "Gleichzeitiges Lesen funktioniert"
            else:
                result.status = "WARN"
                result.message = "Daten bei gleichzeitigem Lesen inkonsistent"

        except Exception as e:
            result.status = "FAIL"
            result.message = str(e)
        finally:
            try:
                remove(self._smb_path(share, test_file))
            except Exception:
                pass

    def test_file_metadata(self, result: TestResult, share: str):
        """Datei-Metadaten (Zeitstempel, Größe, Attribute) lesen."""
        try:
            path = self._smb_path(share)
            entries = list(scandir(path))[:5]
            metadata = []
            for entry in entries:
                try:
                    st = entry.stat()
                    metadata.append({
                        "name": entry.name,
                        "size": st.st_size,
                        "atime": datetime.fromtimestamp(st.st_atime).isoformat(),
                        "mtime": datetime.fromtimestamp(st.st_mtime).isoformat(),
                        "ctime": datetime.fromtimestamp(st.st_ctime).isoformat(),
                    })
                except Exception:
                    metadata.append({"name": entry.name, "error": "Metadaten nicht lesbar"})

            result.status = "PASS" if metadata else "WARN"
            result.message = f"Metadaten von {len(metadata)} Einträgen gelesen"
            result.details["metadata"] = metadata
        except Exception as e:
            result.status = "FAIL"
            result.message = str(e)

    # ─── Aufräumen ────────────────────────────────────────────

    def _cleanup(self, share, test_dir, test_file, created_dir, created_file):
        try:
            if created_file:
                remove(self._smb_path(share, test_dir, test_file))
        except Exception:
            pass
        try:
            if created_dir:
                rmdir(self._smb_path(share, test_dir))
        except Exception:
            pass

    # ─── Alle Tests ausführen ─────────────────────────────────

    def run_all(self):
        """Alle Tests in logischer Reihenfolge ausführen."""

        print("\n" + "═" * 62)
        print("  SMB SERVER TESTER — Umfassende Testausführung")
        print("═" * 62)
        print(f"  Ziel:     {self.config.ip}:{self.config.port}")
        print(f"  Benutzer: {self.config.username}")
        print(f"  Datum:    {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        print("═" * 62)

        # ── Phase 1: Netzwerk ──
        print("\n── Phase 1: Netzwerk ────────────────────────")
        self._run_test("DNS-Auflösung", "Netzwerk", self.test_dns_resolution)
        net = self._run_test("TCP-Erreichbarkeit", "Netzwerk", self.test_network_reachability)

        if net.status != "PASS":
            print("\n  ❌ Server nicht erreichbar — weitere Tests abgebrochen.")
            return self._summary()

        # ── Phase 2: Authentifizierung ──
        print("\n── Phase 2: Authentifizierung ───────────────")
        auth = self._run_test("Benutzer-Authentifizierung", "Auth", self.test_authentication)
        self._run_test("Gastzugang-Check", "Auth", self.test_guest_access)

        if auth.status != "PASS":
            print("\n  ❌ Authentifizierung fehlgeschlagen — weitere Tests abgebrochen.")
            return self._summary()

        # ── Phase 3: Share-Enumeration ──
        print("\n── Phase 3: Share-Enumeration ───────────────")
        self._run_test("Share-Auflistung", "Shares", self.test_share_enumeration)

        # Shares zum Testen bestimmen
        shares_to_test = []
        if self.config.test_share:
            shares_to_test = [self.config.test_share]
        elif self.config.test_all_shares and self.discovered_shares:
            # Admin-Shares überspringen für Write-Tests
            shares_to_test = [
                s for s in self.discovered_shares
                if s not in ("IPC$", "ADMIN$", "print$")
            ]

        if not shares_to_test:
            print("\n  ⚠️  Keine testbaren Shares gefunden.")
            return self._summary()

        # ── Phase 4: Share-Tests ──
        for share in shares_to_test:
            print(f"\n── Phase 4: Tests für Share '{share}' ───────")

            self._run_test(
                f"[{share}] Lesezugriff", "Access",
                self.test_share_read_access, share
            )
            self._run_test(
                f"[{share}] Verzeichnislisting", "Access",
                self.test_share_directory_listing, share
            )
            self._run_test(
                f"[{share}] Datei-Metadaten", "Access",
                self.test_file_metadata, share
            )
            self._run_test(
                f"[{share}] Verzeichnis-Traversierung", "Access",
                self.test_deep_directory_traversal, share
            )
            self._run_test(
                f"[{share}] Schreibzugriff", "Write",
                self.test_share_write_access, share
            )
            self._run_test(
                f"[{share}] Löschberechtigung", "Write",
                self.test_share_delete_access, share
            )
            self._run_test(
                f"[{share}] Sonderzeichen", "Write",
                self.test_special_characters, share
            )
            self._run_test(
                f"[{share}] Simultaner Zugriff", "Access",
                self.test_concurrent_access, share
            )
            self._run_test(
                f"[{share}] Performance-Benchmark", "Benchmark",
                self.test_share_large_file, share
            )

        return self._summary()

    # ─── Zusammenfassung ──────────────────────────────────────

    def _summary(self) -> dict:
        counts = {"PASS": 0, "FAIL": 0, "WARN": 0, "SKIP": 0}
        for r in self.results:
            counts[r.status] = counts.get(r.status, 0) + 1

        total = len(self.results)
        print("\n" + "═" * 62)
        print("  ZUSAMMENFASSUNG")
        print("═" * 62)
        print(f"  Gesamt:       {total} Tests")
        print(f"  ✅ Bestanden:  {counts['PASS']}")
        print(f"  ❌ Fehler:     {counts['FAIL']}")
        print(f"  ⚠️  Warnungen: {counts['WARN']}")
        print(f"  ⏭️  Übersprung: {counts['SKIP']}")
        print("═" * 62)

        if counts["FAIL"] == 0 and counts["WARN"] == 0:
            print("  🎉 Alle Tests bestanden!\n")
        elif counts["FAIL"] == 0:
            print("  👍 Keine Fehler, aber Warnungen beachten.\n")
        else:
            print("  🔍 Es gibt Fehler — Details oben prüfen.\n")

        summary = {
            "target": f"{self.config.ip}:{self.config.port}",
            "username": self.config.username,
            "timestamp": datetime.now().isoformat(),
            "counts": counts,
            "results": [asdict(r) for r in self.results],
            "discovered_shares": self.discovered_shares,
        }
        return summary


# ═══════════════════════════════════════════════════════════════
# Interaktiver Modus
# ═══════════════════════════════════════════════════════════════

def interactive_mode() -> SMBConfig:
    """Konfiguration interaktiv abfragen."""
    print("\n╔══════════════════════════════════════════════╗")
    print("║         SMB TESTER — Konfiguration          ║")
    print("╚══════════════════════════════════════════════╝\n")

    config = SMBConfig()
    config.ip = input("  Server IP/Hostname: ").strip()
    port_input = input("  Port [445]: ").strip()
    config.port = int(port_input) if port_input else 445
    config.username = input("  Benutzername: ").strip()
    config.password = getpass.getpass("  Passwort: ")
    config.domain = input("  Domäne (optional): ").strip()

    share = input("  Spezifischer Share (leer = alle testen): ").strip()
    if share:
        config.test_share = share
        config.test_all_shares = False

    write = input("  Schreibtests durchführen? [J/n]: ").strip().lower()
    config.write_test = write != "n"
    config.delete_test = config.write_test

    bench = input("  Performance-Benchmark? [J/n]: ").strip().lower()
    config.benchmark = bench != "n"

    if config.benchmark:
        size = input("  Benchmark-Dateigröße in KB [1024]: ").strip()
        config.benchmark_size_kb = int(size) if size else 1024

    verbose = input("  Ausführliche Ausgabe? [j/N]: ").strip().lower()
    config.verbose = verbose == "j"

    return config


# ═══════════════════════════════════════════════════════════════
# CLI-Modus
# ═══════════════════════════════════════════════════════════════

def parse_args() -> Optional[SMBConfig]:
    """Kommandozeilen-Argumente parsen."""
    parser = argparse.ArgumentParser(
        description="SMB Server Tester — Umfassende SMB-Tests",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Beispiele:
  %(prog)s --ip 192.168.1.10 --user admin --pass secret
  %(prog)s --ip fileserver --user domain\\\\user --share Data
  %(prog)s --config test_config.json
  %(prog)s                           # Interaktiver Modus
        """
    )
    parser.add_argument("--ip", help="Server IP oder Hostname")
    parser.add_argument("--port", type=int, default=445, help="SMB Port (Standard: 445)")
    parser.add_argument("--user", "--username", dest="username", help="Benutzername")
    parser.add_argument("--pass", "--password", dest="password", help="Passwort")
    parser.add_argument("--domain", default="", help="Domäne")
    parser.add_argument("--share", default="", help="Spezifischer Share")
    parser.add_argument("--no-write", action="store_true", help="Keine Schreibtests")
    parser.add_argument("--no-bench", action="store_true", help="Kein Benchmark")
    parser.add_argument("--bench-size", type=int, default=1024, help="Benchmark KB")
    parser.add_argument("--timeout", type=int, default=30, help="Timeout in Sekunden")
    parser.add_argument("--verbose", "-v", action="store_true", help="Ausführliche Ausgabe")
    parser.add_argument("--config", help="JSON-Konfigurationsdatei")
    parser.add_argument("--output", "-o", help="Ergebnis als JSON speichern")

    args = parser.parse_args()

    # Config-Datei laden
    if args.config:
        with open(args.config) as f:
            data = json.load(f)
        return SMBConfig(**data)

    # Wenn keine IP → interaktiver Modus
    if not args.ip:
        return None

    config = SMBConfig(
        ip=args.ip,
        port=args.port,
        username=args.username or "",
        password=args.password or "",
        domain=args.domain,
        test_share=args.share,
        test_all_shares=not bool(args.share),
        write_test=not args.no_write,
        delete_test=not args.no_write,
        benchmark=not args.no_bench,
        benchmark_size_kb=args.bench_size,
        timeout=args.timeout,
        verbose=args.verbose,
    )

    # Fehlende Pflichtfelder interaktiv abfragen
    if not config.username:
        config.username = input("  Benutzername: ").strip()
    if not config.password:
        config.password = getpass.getpass("  Passwort: ")

    return config


# ═══════════════════════════════════════════════════════════════
# Hauptprogramm
# ═══════════════════════════════════════════════════════════════

def main():
    config = parse_args()
    if config is None:
        config = interactive_mode()

    tester = SMBTester(config)
    summary = tester.run_all()

    # Optionale JSON-Ausgabe
    if len(sys.argv) > 1:
        for i, arg in enumerate(sys.argv):
            if arg in ("--output", "-o") and i + 1 < len(sys.argv):
                outfile = sys.argv[i + 1]
                with open(outfile, "w") as f:
                    json.dump(summary, f, indent=2, ensure_ascii=False)
                print(f"  📄 Ergebnis gespeichert: {outfile}\n")
                break


if __name__ == "__main__":
    main()
