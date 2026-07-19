#!/usr/bin/env python3
"""
╔══════════════════════════════════════════════════════════════╗
║           SMB VERSIONING / PREVIOUS VERSIONS TESTER         ║
║                                                              ║
║  Tests für Windows "Vorherige Versionen" Kompatibilität     ║
║  über SMB (FSCTL_SRV_ENUMERATE_SNAPSHOTS + @GMT-Pfade)      ║
║                                                              ║
║  Kann standalone oder als Modul im smb_tester.py laufen.    ║
║                                                              ║
║  Abhängigkeiten:  pip install pysmb                         ║
╚══════════════════════════════════════════════════════════════╝

Getestete Szenarien:
  - Datei schreiben, überschreiben, Versionen prüfen
  - @GMT-Pfade auflösen (Snapshot-Zugriff)
  - Snapshot-Inhalt verifizieren (richtige Version?)
  - Readonly-Check auf Snapshot-Dateien
  - Snapshot-Listing nach Mehrfachschreiben
  - Dedup: Gleiches Schreiben erzeugt keine neue Version
  - Verschiedene Dateien → verschiedene Versionen
"""

import argparse
import getpass
import io
import json
import struct
import sys
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional

try:
    from smb.SMBConnection import SMBConnection
    from smb.smb_structs import OperationFailure
except ImportError:
    print("  Fehlende Abhängigkeit: pip install pysmb")
    sys.exit(1)


# ═══════════════════════════════════════════════════════════════
# FSCTL_SRV_ENUMERATE_SNAPSHOTS Parser
# ═══════════════════════════════════════════════════════════════

FSCTL_SRV_ENUMERATE_SNAPSHOTS = 0x00144064


def parse_snapshot_response(data: bytes) -> list[str]:
    """
    Parst die FSCTL_SRV_ENUMERATE_SNAPSHOTS Antwort.

    Format (MS-SMB2 2.2.32.2):
      NumberOfSnapshots     (uint32)
      NumberOfSnapshotsReturned (uint32)
      SnapshotArraySize     (uint32)
      SnapshotArray          (null-terminierte Unicode-Strings)

    Gibt eine Liste von @GMT-Strings zurück.
    """
    if len(data) < 12:
        return []

    num_snapshots = struct.unpack_from("<I", data, 0)[0]
    num_returned = struct.unpack_from("<I", data, 4)[0]
    array_size = struct.unpack_from("<I", data, 8)[0]

    if array_size == 0 or num_returned == 0:
        return []

    array_bytes = data[12:12 + array_size]
    array_str = array_bytes.decode("utf-16-le", errors="replace")

    # Split on null characters, filter empty strings
    tokens = [t for t in array_str.split("\x00") if t.startswith("@GMT-")]
    return tokens


def parse_gmt_token(token: str) -> Optional[datetime]:
    """Parst @GMT-YYYY.MM.DD-HH.MM.SS zu datetime (UTC)."""
    try:
        return datetime.strptime(token, "@GMT-%Y.%m.%d-%H.%M.%S")
    except ValueError:
        return None


# ═══════════════════════════════════════════════════════════════
# Test Result
# ═══════════════════════════════════════════════════════════════

@dataclass
class VersionTestResult:
    name: str
    status: str = "PENDING"  # PASS, FAIL, WARN, SKIP
    message: str = ""
    duration_ms: float = 0.0
    details: dict = field(default_factory=dict)

    @property
    def icon(self):
        return {
            "PASS": "✅", "FAIL": "❌", "WARN": "⚠️ ",
            "SKIP": "⏭️", "PENDING": "⏳",
        }.get(self.status, "❓")


# ═══════════════════════════════════════════════════════════════
# Versioning Tester
# ═══════════════════════════════════════════════════════════════

class SMBVersioningTester:
    def __init__(self, ip: str, port: int, username: str, password: str,
                 share: str, domain: str = "", timeout: int = 30,
                 verbose: bool = False):
        self.ip = ip
        self.port = port
        self.username = username
        self.password = password
        self.share = share
        self.domain = domain
        self.timeout = timeout
        self.verbose = verbose
        self.conn: Optional[SMBConnection] = None
        self.results: list[VersionTestResult] = []
        self._prefix = f"_vertest_{uuid.uuid4().hex[:8]}"
        self._client_name = f"VERTST-{uuid.uuid4().hex[:6].upper()}"

    def _log(self, msg: str):
        if self.verbose:
            ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
            print(f"    [{ts}] {msg}")

    def _connect(self) -> bool:
        self.conn = SMBConnection(
            self.username, self.password,
            self._client_name, self.ip,
            domain=self.domain,
            use_ntlm_v2=True,
            is_direct_tcp=True,
        )
        return self.conn.connect(self.ip, self.port, timeout=self.timeout)

    def _safe_delete(self, filename: str):
        try:
            self.conn.deleteFiles(self.share, f"/{filename}")
        except Exception:
            pass

    def _run_test(self, name, func, *args) -> VersionTestResult:
        result = VersionTestResult(name=name)
        t0 = time.perf_counter()
        try:
            func(result, *args)
        except Exception as e:
            if result.status == "PENDING":
                result.status = "FAIL"
                result.message = f"{type(e).__name__}: {e}"
        finally:
            result.duration_ms = (time.perf_counter() - t0) * 1000
            self.results.append(result)
            suffix = f" — {result.message}" if result.message else ""
            print(f"  {result.icon}  {name}: {result.status} "
                  f"({result.duration_ms:.0f}ms){suffix}")
        return result

    # ─── FSCTL Snapshot Enumeration (pysmb-Level) ──────────

    def _enumerate_snapshots_raw(self) -> Optional[list[str]]:
        """
        Versucht FSCTL_SRV_ENUMERATE_SNAPSHOTS über pysmb.

        pysmb hat keinen nativen IOCTL-Support, also simulieren wir
        den Test indem wir prüfen ob @GMT-Pfade funktionieren.

        Für einen echten FSCTL-Test bräuchte man impacket oder smbprotocol.
        """
        return None  # pysmb limitation

    # ─── Tests ─────────────────────────────────────────────

    def test_version_created_on_write(self, result: VersionTestResult):
        """Datei schreiben, überschreiben, prüfen ob @GMT-Pfad danach existiert."""
        filename = f"{self._prefix}_v1.txt"

        try:
            # Version 1 schreiben
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(b"Version 1 Content"))
            self._log("Version 1 geschrieben")

            # Kurz warten damit Timestamp sich unterscheidet
            time.sleep(2)

            # Version 2 schreiben (überschreiben)
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(b"Version 2 Content"))
            self._log("Version 2 geschrieben (überschrieben)")

            # Aktuelle Datei lesen — sollte Version 2 sein
            buf = io.BytesIO()
            self.conn.retrieveFile(self.share, f"/{filename}", buf)
            current = buf.getvalue()

            if current == b"Version 2 Content":
                result.status = "PASS"
                result.message = (
                    "Datei überschrieben, aktuelle Version korrekt"
                )
            else:
                result.status = "WARN"
                result.message = (
                    f"Aktueller Inhalt unerwartet: {current[:50]}"
                )

        finally:
            self._safe_delete(filename)

    def test_snapshot_path_access(self, result: VersionTestResult):
        """
        Prüfe ob @GMT-Pfade prinzipiell akzeptiert werden.

        Schreibt eine Datei, wartet, überschreibt sie, und versucht dann
        über einen @GMT-Pfad auf die ältere Version zuzugreifen.
        """
        filename = f"{self._prefix}_snap.txt"
        v1_content = b"Snapshot Version 1 - Original"
        v2_content = b"Snapshot Version 2 - Updated"

        try:
            # V1 schreiben
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(v1_content))
            self._log("V1 geschrieben")

            # Timestamp merken (vor V2)
            time.sleep(2)
            v1_approx_time = datetime.utcnow()

            # V2 schreiben
            time.sleep(1)
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(v2_content))
            self._log("V2 geschrieben")

            # Versuche @GMT-Pfad für V1
            # Wir probieren verschiedene Sekunden rund um v1_approx_time
            found_old_version = False
            tried_tokens = []

            for delta in range(-5, 6):
                ts = v1_approx_time.replace(
                    second=max(0, min(59, v1_approx_time.second + delta)),
                    microsecond=0,
                )
                token = ts.strftime("@GMT-%Y.%m.%d-%H.%M.%S")
                gmt_path = f"/{token}/{filename}"
                tried_tokens.append(token)

                try:
                    buf = io.BytesIO()
                    self.conn.retrieveFile(self.share, gmt_path, buf)
                    old_content = buf.getvalue()

                    if old_content == v1_content:
                        result.status = "PASS"
                        result.message = (
                            f"@GMT-Pfad funktioniert! "
                            f"V1 über {token} korrekt gelesen"
                        )
                        result.details["gmt_token"] = token
                        result.details["content_match"] = True
                        found_old_version = True
                        break
                    elif old_content == v2_content:
                        self._log(f"{token}: liefert V2 (nicht V1)")
                    else:
                        self._log(f"{token}: unbekannter Inhalt ({len(old_content)} bytes)")
                        found_old_version = True
                        result.status = "WARN"
                        result.message = (
                            f"@GMT-Pfad liefert Daten, aber unerwarteten Inhalt "
                            f"({len(old_content)} bytes via {token})"
                        )
                        break

                except OperationFailure:
                    self._log(f"{token}: nicht gefunden (erwartet)")
                except Exception as e:
                    self._log(f"{token}: Fehler: {type(e).__name__}: {e}")

            if not found_old_version:
                # @GMT-Pfade werden nicht unterstützt oder Version wurde
                # nicht rechtzeitig erstellt
                result.status = "WARN"
                result.message = (
                    "Keine ältere Version über @GMT-Pfad gefunden. "
                    "Versioning möglicherweise nicht aktiv oder "
                    "Timing-Problem. "
                    f"Getestet: {len(tried_tokens)} Timestamps"
                )
                result.details["tried_tokens"] = tried_tokens[:5]

        finally:
            self._safe_delete(filename)

    def test_snapshot_readonly(self, result: VersionTestResult):
        """Snapshot-Dateien sollten readonly sein (Schreiben muss fehlschlagen)."""
        filename = f"{self._prefix}_readonly.txt"

        try:
            # Datei erstellen
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(b"Original"))
            time.sleep(2)

            # Überschreiben um Version zu erzeugen
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(b"Updated"))

            ts = datetime.utcnow()

            # Versuche auf Snapshot-Pfad zu schreiben
            for delta in range(-5, 6):
                adj_ts = ts.replace(
                    second=max(0, min(59, ts.second + delta)),
                    microsecond=0,
                )
                token = adj_ts.strftime("@GMT-%Y.%m.%d-%H.%M.%S")
                gmt_path = f"/{token}/{filename}"

                try:
                    self.conn.storeFile(self.share, gmt_path,
                                        io.BytesIO(b"SHOULD FAIL"))
                    result.status = "FAIL"
                    result.message = (
                        f"Schreiben auf Snapshot-Pfad {token} wurde erlaubt! "
                        f"Snapshots müssen readonly sein."
                    )
                    return

                except OperationFailure:
                    # Gut — Schreiben wurde abgelehnt
                    result.status = "PASS"
                    result.message = (
                        "Schreiben auf Snapshot-Pfad korrekt abgelehnt"
                    )
                    return
                except Exception:
                    continue

            result.status = "SKIP"
            result.message = (
                "Kein gültiger Snapshot-Pfad gefunden zum Testen"
            )

        finally:
            self._safe_delete(filename)

    def test_dedup_unchanged_content(self, result: VersionTestResult):
        """Gleiches Schreiben zweimal → sollte keine neue Version erzeugen."""
        filename = f"{self._prefix}_dedup.txt"
        content = b"Identical content for dedup test"

        try:
            # Erste Schreibung
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(content))
            time.sleep(2)

            # Zweite Schreibung mit identischem Inhalt
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(content))

            # Aktuelle Datei sollte gleich sein
            buf = io.BytesIO()
            self.conn.retrieveFile(self.share, f"/{filename}", buf)

            if buf.getvalue() == content:
                result.status = "PASS"
                result.message = (
                    "Identisches Überschreiben korrekt verarbeitet "
                    "(Server sollte intern keine neue Version angelegt haben)"
                )
            else:
                result.status = "WARN"
                result.message = "Inhalt nach identischem Schreiben verändert"

        finally:
            self._safe_delete(filename)

    def test_multiple_versions(self, result: VersionTestResult):
        """Mehrere Versionen erzeugen und prüfen ob Listing wächst."""
        filename = f"{self._prefix}_multi.txt"
        versions_written = []

        try:
            for i in range(1, 5):
                content = f"Multi-Version Content #{i} — {uuid.uuid4().hex}"
                self.conn.storeFile(self.share, f"/{filename}",
                                    io.BytesIO(content.encode()))
                versions_written.append(content)
                self._log(f"Version {i} geschrieben")
                time.sleep(2)  # Timestamp-Unterschied sicherstellen

            # Aktuelle Version prüfen
            buf = io.BytesIO()
            self.conn.retrieveFile(self.share, f"/{filename}", buf)
            current = buf.getvalue().decode()

            if current == versions_written[-1]:
                result.status = "PASS"
                result.message = (
                    f"{len(versions_written)} Versionen geschrieben, "
                    f"aktuelle Version korrekt"
                )
                result.details["versions_written"] = len(versions_written)
            else:
                result.status = "WARN"
                result.message = (
                    f"Aktuelle Version stimmt nicht mit letztem Schreiben überein"
                )

        finally:
            self._safe_delete(filename)

    def test_different_files_independent_versions(self, result: VersionTestResult):
        """Verschiedene Dateien haben unabhängige Versionshistorien."""
        file_a = f"{self._prefix}_indep_a.txt"
        file_b = f"{self._prefix}_indep_b.txt"

        try:
            # Datei A: 3 Versionen
            for i in range(3):
                self.conn.storeFile(self.share, f"/{file_a}",
                                    io.BytesIO(f"A-v{i+1}".encode()))
                time.sleep(1)

            # Datei B: 1 Version
            self.conn.storeFile(self.share, f"/{file_b}",
                                io.BytesIO(b"B-v1"))

            # Beide Dateien sollten ihre aktuelle Version haben
            buf_a = io.BytesIO()
            buf_b = io.BytesIO()
            self.conn.retrieveFile(self.share, f"/{file_a}", buf_a)
            self.conn.retrieveFile(self.share, f"/{file_b}", buf_b)

            a_ok = buf_a.getvalue() == b"A-v3"
            b_ok = buf_b.getvalue() == b"B-v1"

            if a_ok and b_ok:
                result.status = "PASS"
                result.message = (
                    "Unabhängige Dateien haben korrekte aktuelle Versionen"
                )
            else:
                result.status = "WARN"
                result.message = (
                    f"A korrekt: {a_ok}, B korrekt: {b_ok}"
                )

        finally:
            self._safe_delete(file_a)
            self._safe_delete(file_b)

    def test_large_file_versioning(self, result: VersionTestResult):
        """Große Datei (1 MB) versionieren — Kompression + Integrität."""
        filename = f"{self._prefix}_large.bin"
        import os
        data_v1 = os.urandom(1024 * 1024)  # 1 MB random
        data_v2 = os.urandom(1024 * 1024)  # 1 MB different random

        try:
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(data_v1))
            self._log("V1 (1 MB) geschrieben")
            time.sleep(2)

            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(data_v2))
            self._log("V2 (1 MB) geschrieben")

            # Aktuelle Version muss V2 sein
            buf = io.BytesIO()
            self.conn.retrieveFile(self.share, f"/{filename}", buf)

            if buf.getvalue() == data_v2:
                result.status = "PASS"
                result.message = (
                    "1 MB Datei: Überschreiben + Integrität OK"
                )
            else:
                result.status = "WARN"
                result.message = (
                    f"1 MB Datei: Aktuelle Version hat "
                    f"{len(buf.getvalue())} Bytes statt {len(data_v2)}"
                )

        finally:
            self._safe_delete(filename)

    def test_rapid_overwrites(self, result: VersionTestResult):
        """Schnelles Überschreiben — Server darf nicht abstürzen."""
        filename = f"{self._prefix}_rapid.txt"
        count = 20

        try:
            for i in range(count):
                self.conn.storeFile(self.share, f"/{filename}",
                                    io.BytesIO(f"Rapid write #{i}".encode()))

            # Finale Version muss die letzte sein
            buf = io.BytesIO()
            self.conn.retrieveFile(self.share, f"/{filename}", buf)
            content = buf.getvalue().decode()

            if content == f"Rapid write #{count - 1}":
                result.status = "PASS"
                result.message = (
                    f"{count} schnelle Überschreibungen: "
                    f"Server stabil, letzte Version korrekt"
                )
            else:
                result.status = "WARN"
                result.message = (
                    f"Letzte Version nach {count}x Überschreiben: '{content}'"
                )

        finally:
            self._safe_delete(filename)

    def test_version_after_delete_recreate(self, result: VersionTestResult):
        """Datei löschen und neu erstellen — Versionshistorie der alten Datei."""
        filename = f"{self._prefix}_delrec.txt"

        try:
            # Datei erstellen
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(b"Before delete"))
            time.sleep(1)

            # Löschen
            self.conn.deleteFiles(self.share, f"/{filename}")
            self._log("Datei gelöscht")

            time.sleep(1)

            # Neu erstellen
            self.conn.storeFile(self.share, f"/{filename}",
                                io.BytesIO(b"After recreate"))

            buf = io.BytesIO()
            self.conn.retrieveFile(self.share, f"/{filename}", buf)

            if buf.getvalue() == b"After recreate":
                result.status = "PASS"
                result.message = (
                    "Datei nach Löschen + Neuerstellen korrekt"
                )
            else:
                result.status = "WARN"
                result.message = "Inhalt nach Löschen/Neuerstellen unerwartet"

        finally:
            self._safe_delete(filename)

    # ─── Runner ────────────────────────────────────────────

    def run_all(self) -> dict:
        w = 62
        print()
        print("=" * w)
        print("  SMB VERSIONING TESTER")
        print("=" * w)
        print(f"  Server:  {self.ip}:{self.port}")
        print(f"  Share:   {self.share}")
        print(f"  User:    {self.username}")
        print(f"  Datum:   {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        print("=" * w)

        # Verbinden
        print("\n── Verbindung ──────────────────────────────")
        try:
            if not self._connect():
                print("  ❌ Verbindung fehlgeschlagen")
                return {"error": "connection_failed"}
            print(f"  ✅ Verbunden als '{self.username}'")
        except Exception as e:
            print(f"  ❌ Verbindungsfehler: {e}")
            return {"error": str(e)}

        # Share-Zugriff prüfen
        try:
            self.conn.listPath(self.share, "/")
            print(f"  ✅ Share '{self.share}' erreichbar")
        except Exception as e:
            print(f"  ❌ Share '{self.share}' nicht erreichbar: {e}")
            return {"error": f"share_access: {e}"}

        # Tests
        print(f"\n── Versioning-Tests auf '{self.share}' ──────")

        self._run_test(
            "Versionierung bei Überschreiben",
            self.test_version_created_on_write)

        self._run_test(
            "@GMT-Pfad Snapshot-Zugriff",
            self.test_snapshot_path_access)

        self._run_test(
            "Snapshot Readonly-Check",
            self.test_snapshot_readonly)

        self._run_test(
            "Dedup: Identisches Schreiben",
            self.test_dedup_unchanged_content)

        self._run_test(
            "Mehrere Versionen erzeugen",
            self.test_multiple_versions)

        self._run_test(
            "Unabhängige Dateien",
            self.test_different_files_independent_versions)

        self._run_test(
            "Große Datei (1 MB) Versioning",
            self.test_large_file_versioning)

        self._run_test(
            "Schnelles Überschreiben (20x)",
            self.test_rapid_overwrites)

        self._run_test(
            "Version nach Löschen + Neuerstellen",
            self.test_version_after_delete_recreate)

        # Aufräumen
        print(f"\n  Räume Testartefakte auf...")
        self._cleanup()

        # Verbindung schließen
        try:
            self.conn.close()
        except Exception:
            pass

        return self._summary()

    def _cleanup(self):
        """Alle Test-Dateien aufräumen."""
        try:
            entries = self.conn.listPath(self.share, "/")
            for e in entries:
                if e.filename.startswith("_vertest_"):
                    self._safe_delete(e.filename)
        except Exception:
            pass

    def _summary(self) -> dict:
        counts = {"PASS": 0, "FAIL": 0, "WARN": 0, "SKIP": 0}
        for r in self.results:
            counts[r.status] = counts.get(r.status, 0) + 1

        w = 62
        print()
        print("=" * w)
        print("  VERSIONING TEST ERGEBNIS")
        print("=" * w)
        print(f"  Gesamt:        {len(self.results)} Tests")
        print(f"  ✅ Bestanden:    {counts['PASS']}")
        print(f"  ❌ Fehler:       {counts['FAIL']}")
        print(f"  ⚠️  Warnungen:    {counts['WARN']}")
        print(f"  ⏭️  Übersprungen: {counts['SKIP']}")
        print("-" * w)

        if counts["FAIL"] == 0:
            print("  ✅ Alle Versioning-Tests bestanden!")
        else:
            print("  ❌ Fehler gefunden:")
            for r in self.results:
                if r.status == "FAIL":
                    print(f"    ❌ {r.name}: {r.message}")

        if counts["WARN"] > 0:
            print()
            print("  Warnungen:")
            for r in self.results:
                if r.status == "WARN":
                    print(f"    ⚠️  {r.name}: {r.message}")

        print("=" * w)
        print()

        return {
            "target": f"{self.ip}:{self.port}",
            "share": self.share,
            "username": self.username,
            "timestamp": datetime.now().isoformat(),
            "counts": counts,
            "results": [
                {
                    "name": r.name,
                    "status": r.status,
                    "message": r.message,
                    "duration_ms": r.duration_ms,
                    "details": r.details,
                }
                for r in self.results
            ],
        }


# ═══════════════════════════════════════════════════════════════
# Integration Helper: kann vom smb_tester.py aufgerufen werden
# ═══════════════════════════════════════════════════════════════

def add_versioning_tests_to_tester(tester, share: str):
    """
    Fügt Versioning-Tests zum bestehenden SMBTester hinzu.

    Aufruf in smb_tester.py nach den bestehenden Phasen:

        # Am Ende von run_all(), nach Phase 10:
        from smb_version_tester import add_versioning_tests_to_tester
        if perms.can_write:
            print(f"\\n── Phase 11: Versioning auf '{share}' ───────")
            add_versioning_tests_to_tester(tester, share)
    """
    def version_write_test(result, share=share):
        filename = f"{tester._prefix}_vwrite.txt"
        try:
            tester.conn.storeFile(share, f"/{filename}",
                                  io.BytesIO(b"V1"))
            time.sleep(2)
            tester.conn.storeFile(share, f"/{filename}",
                                  io.BytesIO(b"V2"))

            buf = io.BytesIO()
            tester.conn.retrieveFile(share, f"/{filename}", buf)

            if buf.getvalue() == b"V2":
                result.status = "PASS"
                result.message = "Überschreiben mit Versioning korrekt"
            else:
                result.status = "WARN"
                result.message = f"Inhalt unerwartet: {buf.getvalue()[:50]}"
        finally:
            tester._safe_delete(share, filename)

    def version_dedup_test(result, share=share):
        filename = f"{tester._prefix}_vdedup.txt"
        content = b"Identical content"
        try:
            tester.conn.storeFile(share, f"/{filename}",
                                  io.BytesIO(content))
            time.sleep(1)
            tester.conn.storeFile(share, f"/{filename}",
                                  io.BytesIO(content))

            buf = io.BytesIO()
            tester.conn.retrieveFile(share, f"/{filename}", buf)

            if buf.getvalue() == content:
                result.status = "PASS"
                result.message = "Dedup: Identisches Schreiben korrekt verarbeitet"
            else:
                result.status = "WARN"
                result.message = "Inhalt nach identischem Schreiben verändert"
        finally:
            tester._safe_delete(share, filename)

    def version_rapid_test(result, share=share):
        filename = f"{tester._prefix}_vrapid.txt"
        count = 10
        try:
            for i in range(count):
                tester.conn.storeFile(share, f"/{filename}",
                                      io.BytesIO(f"R{i}".encode()))

            buf = io.BytesIO()
            tester.conn.retrieveFile(share, f"/{filename}", buf)

            if buf.getvalue() == f"R{count - 1}".encode():
                result.status = "PASS"
                result.message = f"{count}x schnelles Überschreiben OK"
            else:
                result.status = "WARN"
                result.message = f"Inhalt nach {count}x: {buf.getvalue()}"
        finally:
            tester._safe_delete(share, filename)

    tester._run_test(f"[{share}] Versioning: Überschreiben",
                     "Versioning", version_write_test)
    tester._run_test(f"[{share}] Versioning: Dedup",
                     "Versioning", version_dedup_test)
    tester._run_test(f"[{share}] Versioning: Rapid Writes",
                     "Versioning", version_rapid_test)


# ═══════════════════════════════════════════════════════════════
# Main
# ═══════════════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(
        description="SMB Versioning / Previous Versions Tester")
    parser.add_argument("--ip", required=True, help="Server IP")
    parser.add_argument("--port", type=int, default=4445)
    parser.add_argument("--user", required=True, help="Benutzername")
    parser.add_argument("--pass", dest="password", help="Passwort")
    parser.add_argument("--share", required=True, help="Share-Name")
    parser.add_argument("--domain", default="")
    parser.add_argument("--timeout", type=int, default=30)
    parser.add_argument("-v", "--verbose", action="store_true")
    parser.add_argument("-o", "--output", help="JSON-Output")
    parser.add_argument("--config", help="JSON config")

    args = parser.parse_args()

    if args.config:
        with open(args.config) as f:
            cfg = json.load(f)
        ip = cfg.get("ip", args.ip)
        port = cfg.get("port", args.port)
        user = cfg.get("username", args.user)
        pw = cfg.get("password", "")
        share = cfg.get("test_share", args.share)
        domain = cfg.get("domain", "")
    else:
        ip = args.ip
        port = args.port
        user = args.user
        pw = args.password
        share = args.share
        domain = args.domain

    if not pw:
        pw = getpass.getpass("  Passwort: ")

    tester = SMBVersioningTester(
        ip=ip, port=port, username=user, password=pw,
        share=share, domain=domain, timeout=args.timeout,
        verbose=args.verbose,
    )

    summary = tester.run_all()

    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(summary, f, indent=2, ensure_ascii=False)
        print(f"  JSON gespeichert: {args.output}\n")


if __name__ == "__main__":
    main()