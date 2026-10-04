# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""Unit tests for kaimo-samba-log-forwarder.py (archive + console filter)."""

from __future__ import annotations

import importlib.util
import io
import json
import os
import stat
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

SOURCES = Path(
    os.environ.get(
        "KAIMO_SCRIPT_SOURCES", Path(__file__).resolve().parent.parent.parent
    )
)


def load_forwarder():
    spec = importlib.util.spec_from_file_location(
        "kaimo_samba_log_forwarder", SOURCES / "kaimo-samba-log-forwarder.py"
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


fwd = load_forwarder()
NOW = datetime(2026, 3, 4, 5, 6, 7, tzinfo=timezone.utc)
MIB = 1024 * 1024


@pytest.fixture
def archive(tmp_path, monkeypatch):
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_ROOT", str(tmp_path / "logs"))
    monkeypatch.setenv("KAIMO_LOG_CONSOLE_LEVEL_FILE", str(tmp_path / "level"))
    for name in (
        "KAIMO_LOG_ARCHIVE_MAX_FILE_BYTES",
        "KAIMO_LOG_ARCHIVE_RETENTION_DAYS",
        "KAIMO_LOG_ARCHIVE_MAX_SOURCE_BYTES",
    ):
        monkeypatch.delenv(name, raising=False)
    return tmp_path


def read_entries(root: Path) -> list[dict]:
    entries = []
    for path in sorted(root.rglob("*.ndjson")):
        entries += [json.loads(line) for line in path.read_text().splitlines()]
    return entries


# ---------------------------------------------------------------- settings


@pytest.mark.parametrize(
    ("value", "expected"),
    [(None, 7), ("12", 12), ("1", 5), ("-3", 5), ("abc", 7), ("", 7)],
)
def test_positive_int(monkeypatch, value, expected):
    if value is None:
        monkeypatch.delenv("KAIMO_TEST_INT", raising=False)
    else:
        monkeypatch.setenv("KAIMO_TEST_INT", value)
    assert fwd.positive_int("KAIMO_TEST_INT", 7, 5) == expected


@pytest.mark.parametrize(
    ("number", "level"),
    [(-1, "Error"), (0, "Error"), (1, "Warning"), (2, "Information"),
     (5, "Information"), (6, "Debug"), (10, "Debug")],
)
def test_samba_level(number, level):
    assert fwd.samba_level(number) == level


def test_archive_writer_limits(archive, monkeypatch):
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_MAX_FILE_BYTES", "10")
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_MAX_SOURCE_BYTES", "1")
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_RETENTION_DAYS", "0")
    writer = fwd.ArchiveWriter()
    # Minimums: 1 MiB per file, source cap >= file cap, retention >= 1 day.
    assert writer.max_file_bytes == MIB
    assert writer.max_source_bytes == MIB
    assert writer.retention_days == 1
    assert writer.service == "samba"
    assert writer.instance and "/" not in writer.instance


# ---------------------------------------------------------------- archive


def test_write_creates_dated_readable_chunk(archive):
    writer = fwd.ArchiveWriter()
    writer.write({"message": "hello"}, NOW)
    writer.close()
    directory = archive / "logs" / "samba" / "2026" / "03" / "04"
    (chunk,) = directory.iterdir()
    assert chunk.name.startswith(f"samba-{writer.instance}-20260304T050607Z-000")
    assert stat.S_IMODE(chunk.stat().st_mode) == 0o644
    assert stat.S_IMODE(directory.stat().st_mode) == 0o755
    assert read_entries(archive) == [{"message": "hello"}]


def test_rotation_on_day_change_and_size(archive, monkeypatch):
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_MAX_FILE_BYTES", str(MIB))
    writer = fwd.ArchiveWriter()
    writer.write({"n": 1}, NOW)
    writer.write({"n": 2}, NOW + timedelta(days=1))
    big = "x" * (MIB + 10)
    writer.write({"n": 3, "pad": big}, NOW + timedelta(days=1))
    writer.write({"n": 4}, NOW + timedelta(days=1))
    writer.close()
    chunks = sorted((archive / "logs").rglob("*.ndjson"))
    assert len(chunks) == 3, chunks
    assert [e["n"] for e in read_entries(archive)] == [1, 2, 3, 4]


def test_rotation_avoids_existing_chunk_names(archive):
    writer = fwd.ArchiveWriter()
    writer.rotate(NOW)
    first = writer.path
    writer.rotate(NOW)
    second = writer.path
    writer.close()
    assert first != second
    assert second.name.endswith("-001.ndjson")


def test_flush_after_hundred_lines(archive):
    writer = fwd.ArchiveWriter()
    for number in range(100):
        writer.write({"n": number}, NOW)
    assert writer.lines_since_flush == 0
    assert len(writer.path.read_text().splitlines()) == 100
    writer.close()
    writer.close()  # idempotent


def test_flush_after_one_second(archive, monkeypatch):
    writer = fwd.ArchiveWriter()
    writer.write({"n": 1}, NOW)
    writer.last_flush = time.monotonic() - 5
    writer.write({"n": 2}, NOW)
    assert writer.lines_since_flush == 0
    writer.close()


def test_cleanup_removes_expired_and_caps_total_size(archive, monkeypatch):
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_RETENTION_DAYS", "2")
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_MAX_FILE_BYTES", str(MIB))
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_MAX_SOURCE_BYTES", str(2 * MIB))
    root = archive / "logs" / "samba" / "old"
    root.mkdir(parents=True)
    expired = root / "expired.ndjson"
    expired.write_text("x")
    old_time = (NOW - timedelta(days=5)).timestamp()
    os.utime(expired, (old_time, old_time))
    sizes = []
    for index in range(3):
        chunk = root / f"chunk{index}.ndjson"
        chunk.write_bytes(b"y" * MIB)
        recent = (NOW - timedelta(hours=3 - index)).timestamp()
        os.utime(chunk, (recent, recent))
        sizes.append(chunk)
    unrelated = root / "notes.txt"
    unrelated.write_text("kept")

    writer = fwd.ArchiveWriter()
    writer.rotate(NOW)
    writer.close()
    assert not expired.exists()
    # Oldest chunk evicted to respect the 2 MiB source cap; newest kept.
    assert not sizes[0].exists()
    assert sizes[1].exists() and sizes[2].exists()
    assert unrelated.exists()


def test_make_tree_readable_repairs_restrictive_modes(archive):
    stale = archive / "logs" / "samba" / "2020" / "01"
    stale.mkdir(parents=True)
    chunk = stale / "old.ndjson"
    chunk.write_text("{}\n")
    chunk.chmod(0o600)
    stale.chmod(0o700)
    writer = fwd.ArchiveWriter()
    writer.rotate(NOW)
    writer.close()
    assert stat.S_IMODE(chunk.stat().st_mode) == 0o644
    assert stat.S_IMODE(stale.stat().st_mode) == 0o755


def test_make_tree_readable_tolerates_vanishing_paths(archive, monkeypatch):
    writer = fwd.ArchiveWriter()
    directory = archive / "logs" / "samba" / "x"
    directory.mkdir(parents=True)
    original_chmod = Path.chmod

    def failing_chmod(self, mode, *args, **kwargs):
        if self.name in ("x", "logs"):
            raise PermissionError("denied")
        return original_chmod(self, mode, *args, **kwargs)

    monkeypatch.setattr(Path, "chmod", failing_chmod)
    writer.make_tree_readable(directory)

    def failing_rglob(self, pattern):
        raise PermissionError("denied")

    monkeypatch.setattr(Path, "rglob", failing_rglob)
    writer.make_tree_readable(directory)


def test_cleanup_tolerates_unlink_failures(archive, monkeypatch):
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_MAX_FILE_BYTES", str(MIB))
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_MAX_SOURCE_BYTES", str(MIB))
    root = archive / "logs" / "samba" / "old"
    root.mkdir(parents=True)
    expired = root / "expired.ndjson"
    expired.write_bytes(b"z" * (2 * MIB))
    old_time = (NOW - timedelta(days=30)).timestamp()
    os.utime(expired, (old_time, old_time))
    original_unlink = Path.unlink

    def failing_unlink(self, *args, **kwargs):
        if self.name == "expired.ndjson":
            raise PermissionError("denied")
        return original_unlink(self, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", failing_unlink)
    writer = fwd.ArchiveWriter()
    writer.rotate(NOW)
    writer.close()
    assert expired.exists()


# ---------------------------------------------------------------- console


def test_console_level_reads_file_once_per_second(archive):
    level_file = archive / "level"
    console = fwd.ConsoleLevel()
    assert console.current() == "Warning"  # missing file keeps the default
    level_file.write_text("debug\n")
    assert console.current() == "Warning"  # cached until next_read
    console.next_read = 0
    assert console.current() == "Debug"
    level_file.write_text("nonsense")
    console.next_read = 0
    assert console.current() == "Debug"  # invalid values are ignored


# ---------------------------------------------------------------- main


def run_main(monkeypatch, lines: str) -> str:
    output = io.StringIO()
    monkeypatch.setattr(fwd.sys, "stdin", io.StringIO(lines))
    monkeypatch.setattr(fwd.sys, "stdout", output)
    assert fwd.main() == 0
    return output.getvalue()


def test_main_archives_non_debug_and_filters_console(archive, monkeypatch):
    (archive / "level").write_text("Information")
    lines = (
        "[2026/03/04 05:06:07.000000,  0] smbd/server.c:1(main)\n"
        "  continuation of an error\n"
        "[2026/03/04 05:06:07.000000,  3] vfs/x.c:1(f)\n"
        "  info detail\n"
        "[2026/03/04 05:06:07.000000, 10] vfs/x.c:1(f)\n"
        "  debug detail\r\n"
    )
    printed = run_main(monkeypatch, lines)
    assert "continuation of an error" in printed
    assert "info detail" in printed
    assert "debug detail" not in printed
    entries = read_entries(archive)
    assert [e["level"] for e in entries] == ["Error", "Error", "Information", "Information"]
    assert entries[1]["properties"] == {"sambaDebugLevel": "0"}
    assert entries[0]["timestampUtc"].endswith("Z")
    assert [e["sequence"] for e in entries] == [1, 2, 3, 4]
    assert all(e["service"] == "samba" and e["category"] == "Samba" for e in entries)


def test_main_untagged_lines_default_to_information(archive, monkeypatch):
    printed = run_main(monkeypatch, "plain line\n")
    assert printed == ""  # default console level is Warning
    assert [e["level"] for e in read_entries(archive)] == ["Information"]


def test_main_stops_quietly_on_broken_pipe(archive, monkeypatch):
    class BrokenStdout(io.StringIO):
        def write(self, _text):
            raise BrokenPipeError()

    (archive / "level").write_text("Debug")
    monkeypatch.setattr(fwd.sys, "stdin", io.StringIO("line\n"))
    monkeypatch.setattr(fwd.sys, "stdout", BrokenStdout())
    assert fwd.main() == 0
