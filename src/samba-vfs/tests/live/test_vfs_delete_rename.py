# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""unlinkat (authorization, recycle bin, permanent delete) and renameat."""

from __future__ import annotations

import os
import re

import pytest

import kaimo_testlib as kt
from kaimo_testlib import P, Reply

DELETE_MALFORMED = "malformed DELETE authorization response, denied"
RENAME_MALFORMED = "malformed RENAME authorization response, denied"


def make_file(s, relative: str, content: str = "data\n"):
    return s.file(relative, content)


def recycle_bin(s, root: str = "") -> os.PathLike:
    return (s.share / root / ".RECYCLE_BIN") if root else s.share / ".RECYCLE_BIN"


# ---------------------------------------------------------------- delete


def test_permanent_delete_emits_delete_event(s):
    make_file(s, "gone.txt")
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(False))
    result = s.smb("del gone.txt")
    assert result.returncode == 0, s.details(result)
    assert not (s.share / "gone.txt").exists()
    (auth,) = s.authd.seen(P.OP_DELETE_AUTH)
    assert auth.fields == {
        "user": s.samba.user, "share": "data", "directory": 0, "path": "gone.txt",
    }
    (event,) = s.authd.seen(P.OP_DELETE)
    assert event["path"] == "gone.txt" and event["directory"] == 0
    assert not recycle_bin(s).exists()


def test_rmdir_emits_directory_delete_event(s):
    s.directory("empty")
    result = s.smb("rmdir empty")
    assert result.returncode == 0, s.details(result)
    assert not (s.share / "empty").exists()
    (auth,) = s.authd.seen(P.OP_DELETE_AUTH)
    assert auth["directory"] == 1
    (event,) = s.authd.seen(P.OP_DELETE)
    assert event["directory"] == 1 and event["path"] == "empty"


def test_delete_denied_keeps_file(s):
    target = make_file(s, "keep.txt")
    s.authd.on(P.OP_DELETE_AUTH, kt.deny())
    result = s.smb("del keep.txt")
    assert target.exists(), s.details(result)
    assert "NT_STATUS_ACCESS_DENIED" in result.output, s.details(result)
    assert s.authd.seen(P.OP_DELETE) == []
    assert "DELETE DENIED path=[keep.txt]" in s.new_log()


@pytest.mark.parametrize(
    ("reply", "malformed"),
    [
        pytest.param(kt.allow(), True, id="missing-disposition"),
        pytest.param(kt.allow(b"\x02\x00"), True, id="invalid-disposition"),
        pytest.param(kt.allow(b"\x01\x02"), True, id="depth-too-large"),
        pytest.param(kt.allow(b"\x01\x00\x00"), True, id="trailing-byte"),
        pytest.param(kt.allow(b"\x01"), True, id="missing-depth"),
        pytest.param(Reply(P.STATUS_ERROR), False, id="infrastructure-error"),
        pytest.param(Reply(P.STATUS_OVERLOADED), False, id="overloaded"),
    ],
)
def test_bad_delete_replies_fail_closed(s, reply, malformed):
    target = make_file(s, "keep.txt")
    s.authd.on(P.OP_DELETE_AUTH, reply)
    result = s.smb("del keep.txt")
    assert target.exists(), s.details(result)
    assert not recycle_bin(s).exists()
    log = s.new_log()
    if malformed:
        assert DELETE_MALFORMED in log, s.details(result)
    else:
        assert "fail-closed because recycle disposition is unknown" in log


def test_recycle_moves_file_and_emits_rename(s):
    make_file(s, "dir/doc.txt", "recycled\n")
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(True, 0))
    result = s.smb("del dir/doc.txt")
    assert result.returncode == 0, s.details(result)
    assert not (s.share / "dir" / "doc.txt").exists()
    recycled = recycle_bin(s) / "dir" / "doc.txt"
    assert recycled.read_text() == "recycled\n", s.details(result)
    (event,) = s.authd.seen(P.OP_RENAME)
    assert event.fields == {
        "user": s.samba.user,
        "share": "data",
        "directory": 0,
        "source": "dir/doc.txt",
        "destination": ".RECYCLE_BIN/dir/doc.txt",
    }
    assert s.authd.seen(P.OP_DELETE) == []


def test_recycle_collision_gets_timestamped_name(s):
    make_file(s, "doc.txt", "second\n")
    existing = make_file(s, ".RECYCLE_BIN/doc.txt", "first\n")
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(True, 0))
    result = s.smb("del doc.txt")
    assert result.returncode == 0, s.details(result)
    assert existing.read_text() == "first\n"
    (event,) = s.authd.seen(P.OP_RENAME)
    destination = event["destination"]
    # <leaf>_<timestamp> (recycle_move.h kaimo_recycle_candidate_leaf).
    assert re.fullmatch(r"\.RECYCLE_BIN/doc\.txt_\S+", destination), destination
    assert (s.share / destination).read_text() == "second\n"


def test_recycle_repeated_collisions_count_up(s):
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(True, 0))
    destinations = []
    for round_number in range(3):
        make_file(s, "same.txt", f"round {round_number}\n")
        result = s.smb("del same.txt")
        assert result.returncode == 0, s.details(result)
        destinations.append(s.authd.seen(P.OP_RENAME)[-1]["destination"])
    assert len(set(destinations)) == 3, destinations
    for round_number, destination in enumerate(destinations):
        assert (s.share / destination).read_text() == f"round {round_number}\n"


def test_recycle_directory_delete(s):
    s.directory("folder")
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(True, 0))
    result = s.smb("rmdir folder")
    assert result.returncode == 0, s.details(result)
    assert (recycle_bin(s) / "folder").is_dir()
    (event,) = s.authd.seen(P.OP_RENAME)
    assert event["directory"] == 1 and event["destination"] == ".RECYCLE_BIN/folder"


def test_recycle_home_root_depth_one(s):
    make_file(s, "alice/notes/todo.txt", "home\n")
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(True, 1))
    result = s.smb("del alice/notes/todo.txt")
    assert result.returncode == 0, s.details(result)
    recycled = s.share / "alice" / ".RECYCLE_BIN" / "notes" / "todo.txt"
    assert recycled.read_text() == "home\n", s.details(result)
    assert not recycle_bin(s).exists()
    (event,) = s.authd.seen(P.OP_RENAME)
    assert event["destination"] == "alice/.RECYCLE_BIN/notes/todo.txt"


def test_recycle_depth_one_without_home_component_fails(s):
    target = make_file(s, "toplevel.txt")
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(True, 1))
    result = s.smb("del toplevel.txt")
    assert target.exists(), s.details(result)
    assert "RECYCLE FAILED path=[toplevel.txt]" in s.new_log()


@pytest.mark.parametrize("bin_root", ["", "alice"])
def test_delete_inside_recycle_bin_is_permanent(s, bin_root):
    prefix = f"{bin_root}/" if bin_root else ""
    make_file(s, f"{prefix}.RECYCLE_BIN/old.txt")
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(True, 1 if bin_root else 0))
    result = s.smb(f"del {prefix}.RECYCLE_BIN/old.txt")
    assert result.returncode == 0, s.details(result)
    assert not (s.share / f"{prefix}.RECYCLE_BIN/old.txt").exists()
    assert not (s.share / f"{prefix}.RECYCLE_BIN/.RECYCLE_BIN").exists()
    (event,) = s.authd.seen(P.OP_DELETE)
    assert event["path"] == f"{prefix}.RECYCLE_BIN/old.txt"


def test_recycle_bin_symlink_is_never_followed(s):
    outside = s.root / "outside"
    outside.mkdir(mode=0o777, exist_ok=True)
    os.chmod(outside, 0o777)
    os.symlink(outside, s.share / ".RECYCLE_BIN")
    target = make_file(s, "doc.txt")
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(True, 0))
    result = s.smb("del doc.txt")
    assert target.exists(), s.details(result)
    assert list(outside.iterdir()) == []
    assert s.authd.seen(P.OP_RENAME) == []
    assert "RECYCLE FAILED path=[doc.txt]" in s.new_log()


def test_recycle_bin_regular_file_blocks_move(s):
    (s.share / ".RECYCLE_BIN").write_text("not a directory")
    target = make_file(s, "doc.txt")
    s.authd.on(P.OP_DELETE_AUTH, kt.allow_delete(True, 0))
    result = s.smb("del doc.txt")
    assert target.exists(), s.details(result)
    assert "RECYCLE FAILED path=[doc.txt]" in s.new_log()


def test_delete_event_failure_does_not_fail_delete(s):
    make_file(s, "gone.txt")
    s.authd.on(P.OP_DELETE, Reply(P.STATUS_ERROR))
    result = s.smb("del gone.txt")
    assert result.returncode == 0, s.details(result)
    assert not (s.share / "gone.txt").exists()
    assert "lifecycle event 7 was not durably accepted" in s.new_log()


# ---------------------------------------------------------------- rename


def test_rename_authorizes_and_emits_event(s):
    make_file(s, "a.txt", "moved\n")
    result = s.smb("rename a.txt b.txt")
    assert result.returncode == 0, s.details(result)
    assert (s.share / "b.txt").read_text() == "moved\n"
    (auth,) = s.authd.seen(P.OP_RENAME_AUTH)
    assert auth.fields == {
        "user": s.samba.user,
        "share": "data",
        "source_directory": 0,
        "destination_exists": 0,
        "destination_directory": 0,
        "replace": 0,
        "source": "a.txt",
        "destination": "b.txt",
    }
    (event,) = s.authd.seen(P.OP_RENAME)
    assert (event["source"], event["destination"], event["directory"]) == ("a.txt", "b.txt", 0)


def test_rename_directory_across_folders(s):
    s.directory("src/inner")
    s.directory("dst")
    result = s.smb("rename src/inner dst/inner")
    assert result.returncode == 0, s.details(result)
    assert (s.share / "dst" / "inner").is_dir()
    (auth,) = s.authd.seen(P.OP_RENAME_AUTH)
    assert auth["source_directory"] == 1
    assert (auth["source"], auth["destination"]) == ("src/inner", "dst/inner")
    (event,) = s.authd.seen(P.OP_RENAME)
    assert event["directory"] == 1


def test_rename_replacing_existing_destination(s):
    make_file(s, "new.txt", "new\n")
    make_file(s, "old.txt", "old\n")
    result = s.smb("rename new.txt old.txt -f")
    assert result.returncode == 0, s.details(result)
    assert (s.share / "old.txt").read_text() == "new\n"
    (auth,) = s.authd.seen(P.OP_RENAME_AUTH)
    assert auth["destination_exists"] == 1 and auth["replace"] == 1
    assert auth["destination_directory"] == 0


def test_rename_denied_keeps_source(s):
    make_file(s, "a.txt")
    s.authd.on(P.OP_RENAME_AUTH, kt.deny())
    result = s.smb("rename a.txt b.txt")
    assert "NT_STATUS_ACCESS_DENIED" in result.output, s.details(result)
    assert (s.share / "a.txt").exists() and not (s.share / "b.txt").exists()
    assert s.authd.seen(P.OP_RENAME) == []
    assert "RENAME DENIED [a.txt] -> [b.txt]" in s.new_log()


@pytest.mark.parametrize(
    ("reply", "marker"),
    [
        pytest.param(kt.allow(b"x"), RENAME_MALFORMED, id="payload"),
        pytest.param(Reply(P.STATUS_UNAUTHORIZED_PEER), RENAME_MALFORMED, id="peer"),
        pytest.param(Reply(P.STATUS_ERROR), "authd unreachable (rename), fail-closed", id="error"),
    ],
)
def test_bad_rename_replies_fail_closed(s, reply, marker):
    make_file(s, "a.txt")
    s.authd.on(P.OP_RENAME_AUTH, reply)
    result = s.smb("rename a.txt b.txt")
    assert (s.share / "a.txt").exists() and not (s.share / "b.txt").exists(), s.details(result)
    assert marker in s.new_log()


def test_rename_toctou_destination_appears_during_authorization(s):
    make_file(s, "a.txt", "source\n")
    destination = s.share / "b.txt"

    def race(_request):
        destination.write_text("intruder\n")

    s.authd.on(P.OP_RENAME_AUTH, Reply(P.STATUS_ALLOW, before=race))
    result = s.smb("rename a.txt b.txt")
    assert (s.share / "a.txt").read_text() == "source\n", s.details(result)
    assert destination.read_text() == "intruder\n"
    assert s.authd.seen(P.OP_RENAME) == []
    assert "RENAME state changed during authorization" in s.new_log()


def test_rename_toctou_source_replaced_during_authorization(s):
    source = make_file(s, "a.txt", "original\n")

    def race(_request):
        source.unlink()
        source.write_text("swapped inode\n")

    s.authd.on(P.OP_RENAME_AUTH, Reply(P.STATUS_ALLOW, before=race))
    result = s.smb("rename a.txt b.txt")
    assert not (s.share / "b.txt").exists(), s.details(result)
    assert "RENAME state changed during authorization" in s.new_log()


def test_rename_event_failure_does_not_fail_rename(s):
    make_file(s, "a.txt")
    s.authd.on(P.OP_RENAME, Reply(P.STATUS_ERROR))
    result = s.smb("rename a.txt b.txt")
    assert result.returncode == 0, s.details(result)
    assert (s.share / "b.txt").exists()
    assert "lifecycle event 8 was not durably accepted" in s.new_log()
