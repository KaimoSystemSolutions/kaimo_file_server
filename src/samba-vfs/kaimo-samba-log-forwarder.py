#!/usr/bin/env python3
"""Archive Samba stdout as NDJSON and forward only the configured console level."""

from __future__ import annotations

import json
import os
import re
import socket
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path


HEADER = re.compile(r"^\[[^,]+,\s*(\d+)\]")
LEVEL_RANK = {"Debug": 1, "Information": 2, "Warning": 3, "Error": 4, "Critical": 5}


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
    current_number = 5
    sequence = 0
    try:
        for raw_line in sys.stdin:
            line = raw_line.rstrip("\r\n")
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
        writer.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
