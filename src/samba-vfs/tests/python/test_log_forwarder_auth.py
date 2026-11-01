# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""SMB authentication events in kaimo-samba-log-forwarder.py (AP8d).

Samba's auth_json_audit lines are reduced to time, account, address and status
and written as small private TSV batches that kaimo_authreport sends to the
bridge. The raw line never leaves the archive.
"""

from __future__ import annotations

import importlib.util
import io
import json
import os
import stat
import time
from pathlib import Path

import pytest

SOURCES = Path(
    os.environ.get(
        "KAIMO_SCRIPT_SOURCES", Path(__file__).resolve().parent.parent.parent
    )
)


def load_forwarder():
    spec = importlib.util.spec_from_file_location(
        "kaimo_samba_log_forwarder_auth", SOURCES / "kaimo-samba-log-forwarder.py"
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


fwd = load_forwarder()


def auth_line(status="NT_STATUS_WRONG_PASSWORD", account="anna.weber",
              remote="ipv4:172.19.0.1:33672",
              timestamp="2026-10-31T20:25:21.584676+0100", **extra) -> str:
    body = {"version": {"major": 1, "minor": 3}, "eventId": 4625, "status": status,
            "localAddress": "ipv4:172.19.0.7:445", "remoteAddress": remote,
            "serviceDescription": "SMB2", "clientDomain": "PC-MARCO",
            "clientAccount": account, "mappedAccount": account, "passwordType": "NTLMv2"}
    body.update(extra)
    return json.dumps({"timestamp": timestamp, "type": "Authentication", "Authentication": body})


@pytest.fixture
def spool(tmp_path, monkeypatch):
    monkeypatch.setenv("KAIMO_LOG_ARCHIVE_ROOT", str(tmp_path / "logs"))
    monkeypatch.setenv("KAIMO_LOG_CONSOLE_LEVEL_FILE", str(tmp_path / "level"))
    monkeypatch.setenv("KAIMO_AUTH_EVENT_SPOOL", str(tmp_path / "auth-events"))
    monkeypatch.delenv("KAIMO_AUTH_EVENT_SPOOL_MAX_FILES", raising=False)
    return tmp_path / "auth-events"


def batches(directory: Path) -> list[list[list[str]]]:
    return [[line.split("\t") for line in path.read_text().splitlines()]
            for path in sorted(directory.glob("*.tsv"))]


def run_main(monkeypatch, lines: list[str]) -> None:
    monkeypatch.setattr(fwd.sys, "stdin", io.StringIO("".join(line + "\n" for line in lines)))
    monkeypatch.setattr(fwd.sys, "stdout", io.StringIO())
    assert fwd.main() == 0


# ---------------------------------------------------------------- parsing


def test_parse_reduces_the_samba_line_to_four_fields():
    event = fwd.AuthEventSpool.parse(auth_line())
    # 20:25:21.584676+01:00 == 19:25:21.584 UTC
    assert event == (1793474721584, "anna.weber", "172.19.0.1", "NT_STATUS_WRONG_PASSWORD")


@pytest.mark.parametrize("remote, address", [
    ("ipv6:fd00::1:51234", "fd00::1"),
    ("ipv6:::ffff:10.0.0.9:445", "10.0.0.9"),
    ("ipv4:10.1.2.3:445", "10.1.2.3"),
    ("unix:/tmp/socket", ""),
    ("ipv4:999.1.1.1:445", ""),
    (None, ""),
])
def test_parse_normalizes_the_remote_address(remote, address):
    assert fwd.AuthEventSpool.parse(auth_line(remote=remote))[2] == address


@pytest.mark.parametrize("line", [
    "plain samba log line",
    "  auth_json_audit: 5",
    '{"timestamp": "x", "type": "Authorization", "Authorization": {}}',
    '{"timestamp": "x", "type": "Authentication"',
    '{"timestamp": "x", "type": "Authentication", "Authentication": "nope"}',
    auth_line(status="OK"),
    auth_line(status="NT_STATUS_" + "A" * 80),
    auth_line(status=None),
    auth_line(account=""),
    auth_line(account=None, mappedAccount=None),
])
def test_parse_ignores_everything_but_valid_authentication_events(line):
    assert fwd.AuthEventSpool.parse(line) is None


def test_parse_strips_control_characters_and_bounds_the_account():
    event = fwd.AuthEventSpool.parse(auth_line(account="ev\til\nuser\x7f" + "x" * 300))
    assert "\t" not in event[1] and "\n" not in event[1] and "\x7f" not in event[1]
    assert event[1].startswith("eviluser")
    assert len(event[1]) == 256


def test_parse_falls_back_to_the_mapped_account():
    assert fwd.AuthEventSpool.parse(auth_line(account=None, mappedAccount="bob"))[1] == "bob"


def test_parse_uses_the_current_time_for_an_unreadable_timestamp(monkeypatch):
    monkeypatch.setattr(fwd.time, "time", lambda: 1_000.5)
    assert fwd.AuthEventSpool.parse(auth_line(timestamp="yesterday"))[0] == 1_000_500


# ---------------------------------------------------------------- spooling


def test_main_writes_one_private_batch_and_still_archives_the_line(spool, monkeypatch, tmp_path):
    run_main(monkeypatch, [auth_line(), "other line", auth_line(status="NT_STATUS_OK")])

    assert batches(spool) == [[
        ["1793474721584", "anna.weber", "172.19.0.1", "NT_STATUS_WRONG_PASSWORD"],
        ["1793474721584", "anna.weber", "172.19.0.1", "NT_STATUS_OK"],
    ]]
    assert stat.S_IMODE(spool.stat().st_mode) == 0o700
    assert all(stat.S_IMODE(p.stat().st_mode) == 0o600 for p in spool.iterdir())
    assert not list(spool.glob("*.tmp"))
    archived = [json.loads(line) for p in (tmp_path / "logs").rglob("*.ndjson")
                for line in p.read_text().splitlines()]
    assert len(archived) == 3


def test_main_without_auth_lines_writes_no_batch(spool, monkeypatch):
    run_main(monkeypatch, ["just a line"])
    assert not spool.exists() or not list(spool.iterdir())


def test_a_full_buffer_is_written_before_the_end_of_input(spool, monkeypatch):
    monkeypatch.setattr(fwd.AuthEventSpool, "MAX_BATCH_EVENTS", 2)
    run_main(monkeypatch, [auth_line()] * 5)
    assert [len(b) for b in batches(spool)] == [2, 2, 1]


def test_the_buffer_is_written_after_one_second(spool, monkeypatch):
    # first add, then one check per event: 0.3 and 0.6 wait, 1.2 writes all three
    clock = iter([0.0, 0.3, 0.6, 1.2])
    monkeypatch.setattr(fwd.time, "monotonic", lambda: next(clock, 5.0))
    auth = fwd.AuthEventSpool()
    for _ in range(3):
        auth.add(fwd.AuthEventSpool.parse(auth_line()))
        auth.maybe_flush()
    assert [len(b) for b in batches(spool)] == [3]


def test_a_buffered_batch_is_written_while_samba_logs_nothing(spool, monkeypatch):
    # No further log line arrives after a burst: the idle flusher still writes it.
    monkeypatch.setattr(fwd.AuthEventSpool, "IDLE_FLUSH_SECONDS", 0.05)
    auth = fwd.AuthEventSpool()
    auth.start_idle_flush()
    try:
        auth.add(fwd.AuthEventSpool.parse(auth_line()))
        deadline = time.monotonic() + 5
        while not list(spool.glob("*.tsv")) and time.monotonic() < deadline:
            time.sleep(0.05)
        assert [len(b) for b in batches(spool)] == [1]
    finally:
        auth.close()
    assert not auth.events


def test_main_starts_and_stops_the_idle_flusher(spool, monkeypatch):
    calls = []
    monkeypatch.setattr(fwd.AuthEventSpool, "start_idle_flush", lambda self: calls.append("start"))
    monkeypatch.setattr(fwd.AuthEventSpool, "close", lambda self: calls.append("close"))
    run_main(monkeypatch, ["just a line"])
    assert calls == ["start", "close"]


def test_a_full_spool_drops_new_batches_without_failing(spool, monkeypatch):
    monkeypatch.setenv("KAIMO_AUTH_EVENT_SPOOL_MAX_FILES", "2")
    monkeypatch.setattr(fwd.AuthEventSpool, "MAX_BATCH_EVENTS", 1)
    run_main(monkeypatch, [auth_line()] * 4)
    assert len(batches(spool)) == 2


def test_an_unwritable_spool_never_stops_log_forwarding(spool, monkeypatch):
    spool.parent.mkdir(parents=True, exist_ok=True)
    spool.write_text("not a directory")
    run_main(monkeypatch, [auth_line()])
    assert spool.read_text() == "not a directory"
