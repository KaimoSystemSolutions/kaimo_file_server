# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""kaimo_authreport against a fake bridge (AP8d).

The log forwarder spools SMB authentication events as private TSV batches;
the reporter sends them to SecurityEventService/ReportAuthentication in file
order and deletes a batch only after the bridge acknowledged it.
"""

from __future__ import annotations

import os
import subprocess
from pathlib import Path

import grpc
import pytest

from conftest import SIDECARS

METHOD = "ReportAuthentication"
LINE = "1793474721584\tanna.weber\t172.19.0.1\tNT_STATUS_WRONG_PASSWORD\n"


@pytest.fixture
def spool(tmp_path):
    directory = tmp_path / "auth-events"
    directory.mkdir(mode=0o700)
    return directory


def batch(spool: Path, name: str, content: str) -> Path:
    path = spool / name
    path.write_text(content)
    path.chmod(0o600)
    return path


def report(bridge, spool: Path) -> subprocess.CompletedProcess:
    env = os.environ.copy()
    env.update(bridge.pki.client_env(bridge.address))
    env["KAIMO_AUTH_EVENT_SPOOL"] = str(spool)
    return subprocess.run([str(SIDECARS / "kaimo_authreport")], env=env, text=True,
                          capture_output=True, timeout=60)


def sent(bridge) -> list[tuple[int, str, str, str]]:
    return [(e.at_unix_ms, e.username, e.address, e.status)
            for request in bridge.requests(METHOD) for e in request.events]


def test_sends_every_batch_in_file_order_and_deletes_it(bridge, spool):
    batch(spool, "auth-00000000000000000002-000002.tsv", LINE.replace("anna.weber", "second"))
    batch(spool, "auth-00000000000000000001-000001.tsv", LINE + LINE.replace("WRONG_PASSWORD", "OK"))

    run = report(bridge, spool)

    assert run.returncode == 0, run.stderr
    assert sent(bridge) == [
        (1793474721584, "anna.weber", "172.19.0.1", "NT_STATUS_WRONG_PASSWORD"),
        (1793474721584, "anna.weber", "172.19.0.1", "NT_STATUS_OK"),
        (1793474721584, "second", "172.19.0.1", "NT_STATUS_WRONG_PASSWORD"),
    ]
    assert list(spool.iterdir()) == []


def test_empty_or_missing_spool_sends_nothing(bridge, spool, tmp_path):
    assert report(bridge, spool).returncode == 0
    assert report(bridge, tmp_path / "missing").returncode == 0
    assert bridge.requests(METHOD) == []


def test_failed_call_keeps_the_batch_for_the_next_run(bridge, spool):
    path = batch(spool, "auth-00000000000000000001-000001.tsv", LINE)
    bridge.on(METHOD, lambda _r, context: context.abort(grpc.StatusCode.UNAVAILABLE, "down"))

    run = report(bridge, spool)

    assert run.returncode == 1
    assert "ReportAuthentication RPC failed" in run.stderr
    assert path.exists()


def test_malformed_lines_are_skipped(bridge, spool):
    batch(spool, "auth-00000000000000000001-000001.tsv",
          "not a record\n"
          "12x\tanna\t10.0.0.1\tNT_STATUS_OK\n"
          "1\tanna\t10.0.0.1\tNT_STATUS_ok\n"
          "1\t\t10.0.0.1\tNT_STATUS_OK\n"
          "1\tanna\t10.0.0.1\tNT_STATUS_OK\textra\n"
          + LINE)

    run = report(bridge, spool)

    assert run.returncode == 0, run.stderr
    assert sent(bridge) == [(1793474721584, "anna.weber", "172.19.0.1", "NT_STATUS_WRONG_PASSWORD")]
    assert list(spool.iterdir()) == []


def test_large_batches_are_split_into_bounded_calls(bridge, spool):
    batch(spool, "auth-00000000000000000001-000001.tsv", LINE * 2500)

    assert report(bridge, spool).returncode == 0
    assert [len(r.events) for r in bridge.requests(METHOD)] == [1000, 1000, 500]


def test_foreign_files_and_links_are_left_alone(bridge, spool, tmp_path):
    target = tmp_path / "elsewhere.tsv"
    target.write_text(LINE)
    (spool / "auth-00000000000000000001-000001.tsv").symlink_to(target)
    foreign = batch(spool, "notes.txt", LINE)
    pending = batch(spool, "auth-00000000000000000002-000002.tmp", LINE)

    assert report(bridge, spool).returncode == 0
    assert bridge.requests(METHOD) == []
    assert target.exists() and foreign.exists() and pending.exists()


def test_an_oversized_batch_is_discarded_and_the_rest_is_sent(bridge, spool):
    oversized = spool / "auth-00000000000000000001-000001.tsv"
    with oversized.open("w") as stream:
        stream.truncate(5 * 1024 * 1024)
    batch(spool, "auth-00000000000000000002-000002.tsv", LINE)

    run = report(bridge, spool)

    assert run.returncode == 0, run.stderr
    assert "discarding unreadable or oversized batch" in run.stderr
    assert len(sent(bridge)) == 1
    assert list(spool.iterdir()) == []


def test_missing_credentials_fail_without_touching_the_spool(bridge, spool, tmp_path):
    path = batch(spool, "auth-00000000000000000001-000001.tsv", LINE)
    env = os.environ.copy()
    env.update(bridge.pki.client_env(bridge.address))
    env["KAIMO_AUTH_EVENT_SPOOL"] = str(spool)
    env["KAIMO_BRIDGE_AUTH_REPORT_CERT"] = str(tmp_path / "missing.crt")

    run = subprocess.run([str(SIDECARS / "kaimo_authreport")], env=env, text=True,
                         capture_output=True, timeout=60)

    assert run.returncode == 1
    assert "cannot open control-plane credential" in run.stderr
    assert path.exists() and bridge.requests(METHOD) == []
