# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""create_file / readdir / mkdir / close: path ACL, listing filter, events."""

from __future__ import annotations

import os
import re
import stat

import pytest

import kaimo_testlib as kt
from kaimo_testlib import P, Reply

FILE_GENERIC_READ = 0x00120089
OPEN_MALFORMED = "malformed OPEN authorization response, denied"


def deny_path(path: str, fallback=kt.default_handler):
    def handler(request: kt.Request) -> Reply:
        if request.fields.get("path") == path:
            return kt.deny()
        return fallback(request)

    return handler


def upload(s, name: str, content: str = "payload\n"):
    local = s.root / "upload.tmp"
    local.write_text(content, encoding="utf-8")
    return s.smb(f"put {local} {name}")


def download(s, name: str):
    local = s.root / "download.tmp"
    if local.exists():
        local.unlink()
    result = s.smb(f"get {name} {local}")
    return result, (local.read_text(encoding="utf-8") if local.exists() else None)


# ---------------------------------------------------------------- open / ACL


def test_open_request_carries_path_mask_and_intent(s):
    result = upload(s, "new.txt")
    assert result.returncode == 0, s.details(result)
    creates = [r for r in s.authd.seen(P.OP_OPEN) if r["path"] == "new.txt"]
    assert creates, s.details(result)
    request = creates[0]
    assert request["user"] == s.samba.user and request["share"] == "data"
    assert request["create"] == 1 and request["directory"] == 0
    assert request["listing"] == 0
    assert request["access"] != 0


def test_open_deny_blocks_read(s):
    (s.share / "secret.txt").write_text("secret\n")
    s.authd.on(P.OP_OPEN, deny_path("secret.txt"))
    result, content = download(s, "secret.txt")
    assert content is None
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert "CREATE DENIED path=[secret.txt]" in s.new_log()


def test_open_deny_blocks_create(s):
    s.authd.on(P.OP_OPEN, deny_path("blocked.txt"))
    result = upload(s, "blocked.txt")
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert not (s.share / "blocked.txt").exists()


def read_only_grant(path: str):
    def handler(request):
        if request.fields.get("path") == path:
            return kt.allow_open(FILE_GENERIC_READ)
        return kt.default_handler(request)

    return handler


def test_granted_mask_blocks_writes_through_the_handle(s):
    target = s.file("readonly.txt", "original\n")
    s.authd.on(P.OP_OPEN, read_only_grant("readonly.txt"))
    # Open an existing file without overwrite intent and try to write into it.
    local = s.root / "append.tmp"
    local.write_text("XX")
    result = s.smb(f"open readonly.txt; put {local} readonly.txt -a")
    read_result, content = download(s, "readonly.txt")
    assert content is not None and "XX" not in content, s.details(result, read_result)


def test_read_only_grant_prevents_truncation(s):
    target = s.file("readonly.txt", "original\n")
    s.authd.on(P.OP_OPEN, read_only_grant("readonly.txt"))
    result = upload(s, "readonly.txt", "overwritten\n")
    assert "NT_STATUS_ACCESS_DENIED" in result.output
    assert target.read_text() == "original\n", s.details(result)


@pytest.mark.parametrize(
    "reply",
    [
        pytest.param(kt.allow(), id="missing-mask"),
        pytest.param(kt.allow(kt.u32(0x10000000)), id="generic-bit"),
        pytest.param(kt.allow(kt.u32(0x02000000)), id="maximum-allowed-bit"),
        pytest.param(kt.allow(kt.u32(FILE_GENERIC_READ) + b"\0"), id="trailing-byte"),
        pytest.param(kt.allow(b"\0\0"), id="short-mask"),
        pytest.param(Reply(P.STATUS_UNAUTHORIZED_PEER), id="unauthorized-peer"),
    ],
)
def test_malformed_open_reply_fails_closed(s, reply):
    (s.share / "file.txt").write_text("content\n")

    def handler(request):
        if request.fields.get("path") == "file.txt":
            return reply
        return kt.default_handler(request)

    s.authd.on(P.OP_OPEN, handler)
    result, content = download(s, "file.txt")
    assert content is None
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)
    assert OPEN_MALFORMED in s.new_log()


def test_open_infrastructure_error_fails_closed(s):
    (s.share / "file.txt").write_text("content\n")

    def handler(request):
        if request.fields.get("path") == "file.txt":
            return Reply(P.STATUS_ERROR)
        return kt.default_handler(request)

    s.authd.on(P.OP_OPEN, handler)
    result, content = download(s, "file.txt")
    assert content is None
    assert result.status() == "NT_STATUS_ACCESS_DENIED", s.details(result)


@pytest.mark.parametrize(
    "command",
    [
        "get .kaimo-close-captures/x.cap {local}",
        "put {upload} .kaimo-anything.txt",
        "mkdir .kaimo-snapshots",
        "ls .kaimo-close-captures/*",
        "rename visible.txt .kaimo-close-captures",
        "del .kaimo-close-captures",
    ],
)
def test_reserved_namespace_is_unreachable(s, command):
    captures = s.share / ".kaimo-close-captures"
    captures.mkdir(mode=0o777)
    (captures / "x.cap").write_text("captured\n")
    (s.share / "visible.txt").write_text("v\n")
    local = s.root / "local.tmp"
    upload_source = s.root / "upload.tmp"
    upload_source.write_text("x\n")
    result = s.smb(command.format(local=local, upload=upload_source))
    assert "NT_STATUS_ACCESS_DENIED" in result.output or "NT_STATUS_OBJECT_NAME_NOT_FOUND" in result.output or result.returncode != 0, s.details(result)
    assert not local.exists()
    assert (captures / "x.cap").read_text() == "captured\n"
    assert (s.share / "visible.txt").exists()
    assert not (s.share / ".kaimo-anything.txt").exists()
    assert not (s.share / ".kaimo-snapshots").exists()
    paths = [str(r.fields.get("path", "")) for r in s.authd.requests]
    assert not any(p.startswith(".kaimo-") for p in paths), paths


# ---------------------------------------------------------------- readdir


def listing(result: kt.SmbResult) -> set[str]:
    names = set()
    for line in result.stdout.splitlines():
        match = re.match(r"^\s{2}(\S.*?)\s+[A-Z]*\s+\d+\s+\w{3} ", line)
        if match:
            names.add(match.group(1))
    return names


def test_listing_hides_denied_and_reserved_entries(s):
    for name in ("public.txt", "hidden.txt"):
        (s.share / name).write_text(name)
    (s.share / "folder").mkdir()
    (s.share / ".kaimo-close-captures").mkdir()

    def handler(request):
        if request["listing"] == 1 and request["path"] == "hidden.txt":
            return kt.deny()
        return kt.default_handler(request)

    s.authd.on(P.OP_OPEN, handler)
    result = s.smb("ls")
    assert result.returncode == 0, s.details(result)
    names = listing(result)
    assert {"public.txt", "folder", ".", ".."} <= names, s.details(result)
    assert "hidden.txt" not in names
    assert ".kaimo-close-captures" not in names
    listed = {r["path"] for r in s.authd.seen(P.OP_OPEN) if r["listing"] == 1}
    assert {"public.txt", "hidden.txt", "folder"} <= listed
    assert not any(p.startswith(".kaimo-") or p in (".", "..") for p in listed)
    assert all(r["access"] == 0x1 for r in s.authd.seen(P.OP_OPEN) if r["listing"] == 1)
    assert "LIST hide [hidden.txt]" in s.new_log()


def test_listing_in_subdirectory_uses_share_relative_paths(s):
    s.file("dir/inner.txt", "x")
    s.file("dir/inner-hidden.txt", "x")
    s.authd.on(P.OP_OPEN, deny_path("dir/inner-hidden.txt"))
    result = s.smb("cd dir; ls")
    assert result.returncode == 0, s.details(result)
    names = listing(result)
    assert "inner.txt" in names and "inner-hidden.txt" not in names
    listed = {r["path"] for r in s.authd.seen(P.OP_OPEN) if r["listing"] == 1}
    assert "dir/inner.txt" in listed


# ---------------------------------------------------------------- mkdir


def test_mkdir_emits_mkdir_event(s):
    result = s.smb("mkdir newdir")
    assert result.returncode == 0, s.details(result)
    assert (s.share / "newdir").is_dir()
    creates = [r for r in s.authd.seen(P.OP_OPEN) if r["path"] == "newdir"]
    assert creates and creates[0]["create"] == 1 and creates[0]["directory"] == 1
    events = [e for e in s.authd.seen(P.OP_MKDIR) if e["path"] == "newdir"]
    assert events, s.details(result)
    assert events[0]["user"] == s.samba.user and events[0]["share"] == "data"


def test_mkdir_nested_path_event(s):
    s.directory("parent")
    result = s.smb("mkdir parent/child")
    assert result.returncode == 0, s.details(result)
    assert "parent/child" in {e["path"] for e in s.authd.seen(P.OP_MKDIR)}


def test_mkdir_never_reports_samba_temporary_names(s):
    result = s.smb("mkdir newdir")
    assert result.returncode == 0, s.details(result)
    paths = [str(r.fields.get("path", r.fields.get("source", ""))) for r in s.authd.requests]
    assert not [p for p in paths if "TMPNAME" in p], paths


def test_mkdir_denied_creates_nothing(s):
    s.authd.on(P.OP_OPEN, deny_path("denied-dir"))
    result = s.smb("mkdir denied-dir")
    assert "NT_STATUS_ACCESS_DENIED" in result.output, s.details(result)
    assert not (s.share / "denied-dir").exists()
    assert s.authd.seen(P.OP_MKDIR) == []


def test_mkdir_event_failure_does_not_fail_operation(s):
    s.authd.on(P.OP_MKDIR, Reply(P.STATUS_ERROR))
    result = s.smb("mkdir still-created")
    assert result.returncode == 0, s.details(result)
    assert (s.share / "still-created").is_dir()
    assert "lifecycle event 6 was not durably accepted" in s.new_log()


# ---------------------------------------------------------------- close


def prepare_capture_directory(s):
    captures = s.share / ".kaimo-close-captures"
    captures.mkdir(mode=0o777)
    os.chmod(captures, 0o777)
    return captures


def test_close_captures_exact_content_and_emits_event(s):
    captures = prepare_capture_directory(s)
    result = upload(s, "doc.txt", "versioned content\n")
    assert result.returncode == 0, s.details(result)
    events = s.authd.seen(P.OP_CLOSE)
    assert len(events) == 1, s.details(result)
    event = events[0]
    assert event["path"] == "doc.txt" and event["share"] == "data"
    assert re.fullmatch(r"[0-9a-f]{32}", event["capture"])
    capture = captures / f"{event['capture']}.cap"
    assert capture.read_text() == "versioned content\n"
    assert stat.S_IMODE(capture.stat().st_mode) == 0o440
    assert not [p for p in captures.iterdir() if p.name.endswith(".tmp")]


def test_close_of_large_file_copies_every_chunk(s):
    captures = prepare_capture_directory(s)
    content = "".join(f"{i:08d}\n" for i in range(40000))  # ~360 KiB > 128 KiB buffer
    result = upload(s, "large.txt", content)
    assert result.returncode == 0, s.details(result)
    (event,) = s.authd.seen(P.OP_CLOSE)
    assert (captures / f"{event['capture']}.cap").read_text() == content


def test_close_of_unmodified_file_emits_nothing(s):
    prepare_capture_directory(s)
    (s.share / "read-only.txt").write_text("unchanged\n")
    result, content = download(s, "read-only.txt")
    assert content == "unchanged\n", s.details(result)
    assert s.authd.seen(P.OP_CLOSE) == []


def test_close_event_rejection_discards_capture(s):
    captures = prepare_capture_directory(s)
    s.authd.on(P.OP_CLOSE, Reply(P.STATUS_ERROR))
    result = upload(s, "doc.txt")
    assert result.returncode == 0, s.details(result)
    assert len(s.authd.seen(P.OP_CLOSE)) == 1
    assert list(captures.iterdir()) == []
    assert "lifecycle event 5 was not durably accepted" in s.new_log()


def test_close_without_capture_directory_suppresses_event(s):
    result = upload(s, "doc.txt")
    assert result.returncode == 0, s.details(result)
    assert (s.share / "doc.txt").read_text() == "payload\n"
    assert s.authd.seen(P.OP_CLOSE) == []
    assert "CLOSE could not capture the exact handle content" in s.new_log()


def test_close_capture_directory_symlink_is_refused(s):
    target = s.root / "elsewhere"
    target.mkdir(mode=0o777, exist_ok=True)
    os.chmod(target, 0o777)
    os.symlink(target, s.share / ".kaimo-close-captures")
    result = upload(s, "doc.txt")
    assert result.returncode == 0, s.details(result)
    assert s.authd.seen(P.OP_CLOSE) == []
    assert list(target.iterdir()) == []
