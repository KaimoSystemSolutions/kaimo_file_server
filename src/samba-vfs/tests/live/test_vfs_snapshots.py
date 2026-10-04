# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""@GMT "Previous Versions": enumeration, resolve/redirect, lease, read-only."""

from __future__ import annotations

import os
from pathlib import Path

import pytest

import kaimo_testlib as kt
from kaimo_testlib import P, Reply

GMT = "@GMT-2024.01.02-03.04.05"
SHARE_ID = "0123456789abcdef0123456789abcdef"
USER_ID = "fedcba9876543210fedcba9876543210"
TOKEN_DIR = f"{SHARE_ID}/{GMT}"
LABELS = ["@GMT-2024.01.02-03.04.05", "@GMT-2023.12.31-23.59.59"]


def materialize(s, logical: str, content: str, *, lease: bool = True) -> str:
    """Create a cache projection like the bridge does; return the relative path."""
    relative = f"{TOKEN_DIR}/{USER_ID}/{logical}"
    target = s.cache / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content)
    # POSIX-writable on purpose: denials must come from the VFS policy.
    os.chmod(target, 0o666)
    if lease:
        (s.cache / TOKEN_DIR / ".kaimo-lease").touch()
    kt.chown_tree(s.cache, s.samba.user)
    return relative


def resolve_to(relative: str):
    def handler(request):
        return kt.snapshot_resolution(relative, lease="lease-1", size=1)

    return handler


def download(s, name: str):
    local = s.root / "download.tmp"
    if local.exists():
        local.unlink()
    result = s.smb(f"get {name} {local}")
    return result, (local.read_text() if local.exists() else None)


def live_file(s, name: str = "historical.txt", content: str = "LIVE\n") -> Path:
    path = s.share / name
    path.write_text(content)
    os.chmod(path, 0o666)
    return path


# ---------------------------------------------------------------- enumeration


def test_enumeration_returns_labels(s):
    live_file(s)
    s.authd.on(P.OP_SNAPSHOT_ENUMERATE, kt.snapshot_enumeration(LABELS))
    result = s.smb("allinfo historical.txt")
    assert result.returncode == 0, s.details(result)
    for label in LABELS:
        assert label in result.stdout, s.details(result)
    requests = s.authd.seen(P.OP_SNAPSHOT_ENUMERATE)
    assert requests and requests[0]["path"] == "historical.txt"
    assert "SNAPENUM path=[historical.txt] -> 2 labels" in s.new_log()


@pytest.mark.parametrize(
    ("reply", "marker"),
    [
        pytest.param(kt.ok(kt.u32(2) + kt.encode_string(LABELS[0])),
                     "malformed or oversized SNAPENUM", id="count-mismatch"),
        pytest.param(kt.ok(kt.u32(1) + kt.encode_string("@GMT-2024.13.02-03.04.05x")),
                     "malformed or oversized SNAPENUM", id="bad-token"),
        pytest.param(kt.ok(kt.u32(1) + kt.encode_string(LABELS[0]) + b"\0"),
                     "malformed or oversized SNAPENUM", id="trailing"),
        pytest.param(Reply(P.STATUS_DENY), None, id="deny"),
        pytest.param(Reply(P.STATUS_ERROR), "SNAPENUM authd unreachable", id="error"),
        pytest.param(Reply(P.STATUS_ALLOW, magic=b"XXXX"), "SNAPENUM authd unreachable",
                     id="garbage"),
    ],
)
def test_enumeration_failures_report_no_snapshots(s, reply, marker):
    live_file(s)
    s.authd.on(P.OP_SNAPSHOT_ENUMERATE, reply)
    result = s.smb("allinfo historical.txt")
    assert result.returncode == 0, s.details(result)
    assert "@GMT-" not in result.stdout, s.details(result)
    if marker:
        assert marker in s.new_log(), s.details(result)


# ---------------------------------------------------------------- resolve/read


def test_snapshot_read_is_redirected_to_cache(s):
    live_file(s)
    relative = materialize(s, "historical.txt", "SNAPSHOT\n")
    s.authd.on(P.OP_SNAPSHOT_RESOLVE, resolve_to(relative))
    result, content = download(s, f"{GMT}/historical.txt")
    assert content == "SNAPSHOT\n", s.details(result)
    resolves = s.authd.seen(P.OP_SNAPSHOT_RESOLVE)
    assert resolves and resolves[0]["gmt"] == GMT
    assert resolves[0]["path"] == "historical.txt"
    releases = s.authd.seen(P.OP_SNAPSHOT_RELEASE)
    assert releases and all(r["lease"] == "lease-1" for r in releases)
    log = s.new_log()
    assert f"OPENAT SNAPSHOT [historical.txt@{GMT}]" in log
    assert "Bad talloc magic" not in log and "INTERNAL ERROR" not in log
    # The next VFS layer (full_audit) saw the open of the logical name.
    assert f"KT|openat|ok|r|{s.share}/historical.txt" in log


def test_repeated_metadata_and_reads_in_one_session(s):
    live_file(s)
    relative = materialize(s, "historical.txt", "SNAPSHOT\n")
    s.authd.on(P.OP_SNAPSHOT_RESOLVE, resolve_to(relative))
    local = s.root / "again.tmp"
    result = s.smb(
        f"allinfo {GMT}/historical.txt; get {GMT}/historical.txt {local}; "
        f"allinfo {GMT}/historical.txt"
    )
    assert result.returncode == 0, s.details(result)
    assert local.read_text() == "SNAPSHOT\n"
    assert "Bad talloc magic" not in s.new_log()


def test_snapshot_directory_listing_inherits_parent_lease(s):
    live_file(s)
    folder_relative = f"{TOKEN_DIR}/{USER_ID}/folder"
    materialize(s, "folder/inner.txt", "INNER\n")
    s.authd.on(P.OP_SNAPSHOT_RESOLVE, resolve_to(folder_relative))
    local = s.root / "inner.tmp"
    result = s.smb(f"cd {GMT}/folder; ls; get inner.txt {local}")
    assert result.returncode == 0, s.details(result)
    assert "inner.txt" in result.stdout
    assert local.read_text() == "INNER\n", s.details(result)
    assert "OPENAT inherited snapshot dirfd" in s.new_log()


def test_missing_version_is_not_found(s):
    live_file(s)
    s.authd.on(P.OP_SNAPSHOT_RESOLVE, Reply(P.STATUS_NOT_FOUND))
    result, content = download(s, f"{GMT}/historical.txt")
    assert content is None
    assert result.status() == "NT_STATUS_OBJECT_NAME_NOT_FOUND", s.details(result)


@pytest.mark.parametrize(
    "reply",
    [
        pytest.param(kt.snapshot_resolution("../escape.txt"), id="dotdot"),
        pytest.param(kt.snapshot_resolution("/etc/passwd"), id="absolute"),
        pytest.param(kt.snapshot_resolution(f"{TOKEN_DIR}//x"), id="empty-component"),
        pytest.param(kt.snapshot_resolution(f"{TOKEN_DIR}/./x"), id="dot-component"),
        pytest.param(kt.snapshot_resolution("a\\b"), id="backslash"),
        pytest.param(kt.snapshot_resolution("a\x01b"), id="control"),
        pytest.param(kt.snapshot_resolution("single"), id="no-lease-scope"),
        pytest.param(kt.snapshot_resolution(""), id="empty-path"),
        pytest.param(kt.snapshot_resolution(f"{TOKEN_DIR}/{USER_ID}/x", lease=""), id="empty-lease"),
        pytest.param(kt.ok(kt.encode_string("x")), id="truncated"),
        pytest.param(kt.snapshot_resolution("x" * 7000), id="path-too-long"),
        pytest.param(kt.snapshot_resolution(f"{TOKEN_DIR}/{USER_ID}/x", lease="l" * 200),
                     id="lease-too-long"),
        pytest.param(Reply(P.STATUS_ERROR), id="error"),
        pytest.param(Reply(P.STATUS_DENY), id="deny"),
        pytest.param(Reply(P.STATUS_NOT_FOUND, b"x"), id="not-found-payload"),
    ],
)
def test_invalid_resolution_never_serves_live_or_foreign_content(s, reply):
    live_file(s)
    s.authd.on(P.OP_SNAPSHOT_RESOLVE, reply)
    result, content = download(s, f"{GMT}/historical.txt")
    assert content is None, s.details(result)
    assert result.returncode != 0 or "NT_STATUS_" in result.output


def test_missing_lease_file_fails(s):
    live_file(s)
    relative = materialize(s, "historical.txt", "SNAPSHOT\n", lease=False)
    s.authd.on(P.OP_SNAPSHOT_RESOLVE, resolve_to(relative))
    result, content = download(s, f"{GMT}/historical.txt")
    assert content is None, s.details(result)
    assert s.authd.seen(P.OP_SNAPSHOT_RELEASE), "lease handoff must be released"


def test_lease_symlink_or_hardlink_is_refused(s):
    live_file(s)
    relative = materialize(s, "historical.txt", "SNAPSHOT\n", lease=False)
    lease = s.cache / TOKEN_DIR / ".kaimo-lease"
    os.symlink(s.cache / relative, lease)
    s.authd.on(P.OP_SNAPSHOT_RESOLVE, resolve_to(relative))
    result, content = download(s, f"{GMT}/historical.txt")
    assert content is None, s.details(result)

    lease.unlink()
    real = s.cache / TOKEN_DIR / "real-lease"
    real.touch()
    os.link(real, lease)
    result, content = download(s, f"{GMT}/historical.txt")
    assert content is None, s.details(result)


def test_sweeper_lock_defers_open_until_released(s):
    """While the bridge sweeper holds the token's exclusive flock the shared
    lease cannot be taken (EWOULDBLOCK); smbd retries the open, so the client
    is served only after the sweeper let go, never from a half-deleted tree."""
    import fcntl
    import threading
    import time

    live_file(s)
    relative = materialize(s, "historical.txt", "SNAPSHOT\n")
    s.authd.on(P.OP_SNAPSHOT_RESOLVE, resolve_to(relative))
    lease = open(s.cache / TOKEN_DIR / ".kaimo-lease")
    fcntl.flock(lease, fcntl.LOCK_EX)
    releaser = threading.Timer(1.0, lease.close)
    releaser.start()
    started = time.monotonic()
    try:
        result, content = download(s, f"{GMT}/historical.txt")
    finally:
        releaser.join()
    assert time.monotonic() - started >= 0.9
    assert content == "SNAPSHOT\n", s.details(result)
    assert "OPENAT snapshot lease unavailable" in s.new_log()


# ---------------------------------------------------------------- read-only


@pytest.mark.parametrize(
    "command",
    [
        "put {upload} {gmt}/historical.txt",
        "del {gmt}/historical.txt",
        "rename {gmt}/historical.txt moved.txt",
        "mkdir {gmt}/new-directory",
    ],
)
def test_snapshot_mutation_is_write_protected(s, command):
    live_file(s)
    relative = materialize(s, "historical.txt", "SNAPSHOT\n")
    s.authd.on(P.OP_SNAPSHOT_RESOLVE, resolve_to(relative))
    upload = s.root / "upload.tmp"
    upload.write_text("REPLACEMENT\n")
    result = s.smb(command.format(upload=upload, gmt=GMT))
    assert "NT_STATUS_MEDIA_WRITE_PROTECTED" in result.output, s.details(result)
    assert (s.cache / relative).read_text() == "SNAPSHOT\n"
    assert (s.share / "historical.txt").read_text() == "LIVE\n"
    assert not (s.share / "moved.txt").exists()
    assert not (s.share / "new-directory").exists()
    assert "CREATE twrp write intent denied" in s.new_log()


def test_properties_style_traversal_completes(s):
    live_file(s)
    result = s.smb("allinfo .; du")
    assert result.returncode == 0, s.details(result)
