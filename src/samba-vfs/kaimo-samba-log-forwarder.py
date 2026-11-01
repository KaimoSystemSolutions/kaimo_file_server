#!/usr/bin/env python3
"""Archive Samba stdout as NDJSON and forward only the configured console level.

SMB authentication events (Samba's auth_json_audit lines) are additionally
reduced to time, account, address and status and spooled for kaimo_authreport,
which reports them to the bridge's security overview.
"""

from __future__ import annotations

import ipaddress
import json
import os
import re
import socket
import sys
import threading
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path


HEADER = re.compile(r"^\[[^,]+,\s*(\d+)\]")
LEVEL_RANK = {"Debug": 1, "Information": 2, "Warning": 3, "Error": 4, "Critical": 5}
NT_STATUS = re.compile(r"^NT_STATUS_[A-Z0-9_]{1,64}$")
CONTROL = re.compile(r"[\x00-\x1f\x7f]")


def positive_int(name: str, default: int, minimum: int) -> int:
    try:
        return max(int(os.environ.get(name, str(default))), minimum)
    except ValueError:
        return default


class ArchiveWriter:
    def __init__(self) -> None:
        self.root = Path(os.environ.get("KAIMO_LOG_ARCHIVE_ROOT", "/data/kaimo-logs"))
        self.service = "samba"
        self.instance = re.sub(r"[^A-Za-z0-9_.-]", "-", socket.gethostname())[:80] or "instance"
        self.max_file_bytes = positive_int("KAIMO_LOG_ARCHIVE_MAX_FILE_BYTES", 10 * 1024 * 1024, 1024 * 1024)
        self.retention_days = positive_int("KAIMO_LOG_ARCHIVE_RETENTION_DAYS", 14, 1)
        self.max_source_bytes = positive_int("KAIMO_LOG_ARCHIVE_MAX_SOURCE_BYTES", 250 * 1024 * 1024, self.max_file_bytes)
        self.stream = None
        self.day = None
        self.path = None
        self.lines_since_flush = 0
        self.last_flush = time.monotonic()

    def write(self, entry: dict, now: datetime) -> None:
        if self.stream is None or self.day != now.date() or self.stream.tell() >= self.max_file_bytes:
            self.rotate(now)
        self.stream.write(json.dumps(entry, ensure_ascii=False, separators=(",", ":")) + "\n")
        self.lines_since_flush += 1
        if self.lines_since_flush >= 100 or time.monotonic() - self.last_flush >= 1:
            self.stream.flush()
            self.lines_since_flush = 0
            self.last_flush = time.monotonic()

    def rotate(self, now: datetime) -> None:
        self.close()
        directory = self.root / self.service / f"{now.year:04d}" / f"{now.month:02d}" / f"{now.day:02d}"
        directory.mkdir(parents=True, exist_ok=True)
        self.make_tree_readable(directory)
        prefix = f"{self.service}-{self.instance}-{now:%Y%m%dT%H%M%SZ}"
        sequence = 0
        while True:
            path = directory / f"{prefix}-{sequence:03d}.ndjson"
            try:
                self.stream = path.open("x", encoding="utf-8", buffering=64 * 1024)
                path.chmod(0o644)
                self.path = path
                break
            except FileExistsError:
                sequence += 1
        self.day = now.date()
        self.cleanup(now)

    def make_tree_readable(self, directory: Path) -> None:
        """Allow the non-root web container to traverse and read the bind mount."""
        current = self.root
        paths = [current]
        for part in directory.relative_to(self.root).parts:
            current /= part
            paths.append(current)
        for path in paths:
            try:
                path.chmod(0o755)
            except (FileNotFoundError, PermissionError, OSError):
                pass

        # Repair chunks produced by an older image with a restrictive umask.
        source_root = self.root / self.service
        try:
            for path in source_root.rglob("*"):
                try:
                    path.chmod(0o755 if path.is_dir() else 0o644)
                except (FileNotFoundError, PermissionError, OSError):
                    pass
        except (FileNotFoundError, PermissionError, OSError):
            pass

    def cleanup(self, now: datetime) -> None:
        source_root = self.root / self.service
        files = sorted(
            (path for path in source_root.rglob("*.ndjson") if path != self.path),
            key=lambda path: path.stat().st_mtime,
            reverse=True,
        )
        cutoff = (now - timedelta(days=self.retention_days)).timestamp()
        for path in list(files):
            try:
                if path.stat().st_mtime < cutoff:
                    path.unlink()
            except (FileNotFoundError, PermissionError, OSError):
                pass

        files = [path for path in files if path.exists()]
        total = sum(path.stat().st_size for path in files)
        for path in reversed(files):
            if total <= self.max_source_bytes:
                break
            try:
                size = path.stat().st_size
                path.unlink()
                total -= size
            except (FileNotFoundError, PermissionError, OSError):
                pass

    def close(self) -> None:
        if self.stream is not None:
            self.stream.flush()
            self.stream.close()
            self.stream = None


class ConsoleLevel:
    def __init__(self) -> None:
        self.path = Path(os.environ.get("KAIMO_LOG_CONSOLE_LEVEL_FILE", "/var/run/kaimo/console-log-level"))
        self.level = "Warning"
        self.next_read = 0.0

    def current(self) -> str:
        now = time.monotonic()
        if now < self.next_read:
            return self.level
        self.next_read = now + 1
        try:
            candidate = self.path.read_text(encoding="utf-8").strip().capitalize()
            if candidate in LEVEL_RANK:
                self.level = candidate
        except (FileNotFoundError, PermissionError, OSError):
            pass
        return self.level


class AuthEventSpool:
    """Private TSV batches of SMB authentication events for kaimo_authreport.

    Each batch is written to a temporary name and renamed, so the reporter only
    ever sees complete files. Writing never blocks or fails the log stream: a
    full or unwritable spool drops the batch (the archive still has the line).
    Samba may log nothing after a burst of failed logins, so an idle flusher
    thread writes a waiting batch without a further log line.
    """

    MAX_BATCH_EVENTS = 500
    MAX_ACCOUNT_LENGTH = 256
    IDLE_FLUSH_SECONDS = 0.5

    def __init__(self) -> None:
        self.directory = Path(os.environ.get("KAIMO_AUTH_EVENT_SPOOL", "/var/run/kaimo/auth-events"))
        self.max_files = positive_int("KAIMO_AUTH_EVENT_SPOOL_MAX_FILES", 1000, 1)
        self.events: list[tuple[int, str, str, str]] = []
        self.first_buffered = 0.0
        self.sequence = 0
        self.lock = threading.RLock()
        self.stopped = threading.Event()

    @staticmethod
    def parse(line: str) -> tuple[int, str, str, str] | None:
        """(unix ms, account, address, NT status) of an auth_json_audit line, else None."""
        if not line.startswith('{"timestamp"'):
            return None
        try:
            document = json.loads(line)
        except ValueError:
            return None
        if not isinstance(document, dict) or document.get("type") != "Authentication":
            return None
        auth = document.get("Authentication")
        if not isinstance(auth, dict):
            return None
        status = auth.get("status")
        if not isinstance(status, str) or not NT_STATUS.match(status):
            return None
        account = auth.get("clientAccount") or auth.get("mappedAccount")
        if not isinstance(account, str):
            return None
        account = CONTROL.sub("", account).strip()[:AuthEventSpool.MAX_ACCOUNT_LENGTH]
        if not account:
            return None
        return (AuthEventSpool.unix_ms(document.get("timestamp")), account,
                AuthEventSpool.address(auth.get("remoteAddress")), status)

    @staticmethod
    def unix_ms(value) -> int:
        try:
            return int(datetime.strptime(value, "%Y-%m-%dT%H:%M:%S.%f%z").timestamp() * 1000)
        except (TypeError, ValueError):
            return int(time.time() * 1000)

    @staticmethod
    def address(value) -> str:
        """IP of a Samba tsocket address ("ipv4:10.0.0.5:445", "ipv6:fd00::1:445"); "" otherwise."""
        if not isinstance(value, str) or not value.startswith(("ipv4:", "ipv6:")):
            return ""
        host = value[5:].rpartition(":")[0]
        try:
            parsed = ipaddress.ip_address(host)
        except ValueError:
            return ""
        mapped = getattr(parsed, "ipv4_mapped", None)
        return str(mapped or parsed)

    def add(self, event: tuple[int, str, str, str]) -> None:
        with self.lock:
            if not self.events:
                self.first_buffered = time.monotonic()
            self.events.append(event)

    def maybe_flush(self) -> None:
        with self.lock:
            if self.events and (len(self.events) >= self.MAX_BATCH_EVENTS
                                or time.monotonic() - self.first_buffered >= 1):
                self.flush()

    def start_idle_flush(self) -> None:
        def run() -> None:
            while not self.stopped.wait(self.IDLE_FLUSH_SECONDS):
                self.maybe_flush()
        threading.Thread(target=run, name="auth-event-flush", daemon=True).start()

    def close(self) -> None:
        self.stopped.set()
        self.flush()

    def flush(self) -> None:
        with self.lock:
            events, self.events = self.events, []
            if events:
                self.write(events)

    def write(self, events: list[tuple[int, str, str, str]]) -> None:
        try:
            self.directory.mkdir(mode=0o700, parents=True, exist_ok=True)
            if sum(1 for _ in self.directory.glob("*.tsv")) >= self.max_files:
                return  # ponytail: bridge unreachable for long; newest events are dropped
            self.sequence += 1
            name = f"auth-{time.time_ns():020d}-{self.sequence:06d}"
            temporary = self.directory / f"{name}.tmp"
            with temporary.open("x", encoding="utf-8") as stream:
                temporary.chmod(0o600)
                stream.writelines(f"{at}\t{account}\t{address}\t{status}\n"
                                  for at, account, address, status in events)
            temporary.replace(self.directory / f"{name}.tsv")
        except OSError:
            pass


def samba_level(number: int) -> str:
    if number <= 0:
        return "Error"
    if number <= 1:
        return "Warning"
    if number <= 5:
        return "Information"
    return "Debug"


def main() -> int:
    writer = ArchiveWriter()
    console = ConsoleLevel()
    auth_events = AuthEventSpool()
    auth_events.start_idle_flush()
    current_number = 5
    sequence = 0
    try:
        for raw_line in sys.stdin:
            line = raw_line.rstrip("\r\n")
            event = AuthEventSpool.parse(line)
            if event is not None:
                auth_events.add(event)
            auth_events.maybe_flush()
            match = HEADER.match(line)
            if match:
                current_number = int(match.group(1))
            level = samba_level(current_number)
            now = datetime.now(timezone.utc)
            sequence += 1

            if level != "Debug":
                writer.write(
                    {
                        "timestampUtc": now.isoformat(timespec="milliseconds").replace("+00:00", "Z"),
                        "sequence": sequence,
                        "level": level,
                        "service": "samba",
                        "instance": writer.instance,
                        "category": "Samba",
                        "eventId": 0,
                        "message": line,
                        "properties": {"sambaDebugLevel": str(current_number)},
                    },
                    now,
                )

            if LEVEL_RANK[level] >= LEVEL_RANK[console.current()]:
                print(line, flush=True)
    except BrokenPipeError:
        return 0
    finally:
        auth_events.close()
        writer.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
