#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""Rebuild vfs_kaimo_bridge.so with gcov instrumentation (test images only).

Re-running ./configure with --enable-coverage would rebuild all of Samba. The
module is a single translation unit, so instead this replays waf's own
compile and link commands for that one target (captured with ``waf -v``) with
``--coverage -O0`` added. The compile step reads the source and its local
headers from KAIMO_MODULE_SOURCE so gcov reports map to the repository paths.

Usage: build-vfs-module-coverage.py <samba-source-dir> <module-source-dir>
"""

from __future__ import annotations

import ast
import glob
import os
import shutil
import subprocess
import sys
from pathlib import Path

TARGET = "vfs_kaimo_bridge"


def capture_commands(samba: Path) -> list[list[str]]:
    # waf tracks content hashes, not mtimes: change the in-tree copy so it
    # re-runs (and prints) exactly this target's compile and link steps.
    source = samba / "source3" / "modules" / f"{TARGET}.c"
    with source.open("a", encoding="utf-8") as handle:
        handle.write("\n/* coverage relink */\n")
    # An unchanged object would not be relinked; a missing output always is.
    for linked in glob.glob(str(samba / "bin" / "default" / "source3" / "modules"
                                / "libvfs_module_kaimo_bridge*.so")):
        os.unlink(linked)
    output = subprocess.run(
        ["./buildtools/bin/waf", "build", f"--targets={TARGET}", "-v"],
        cwd=samba,
        env={**os.environ, "PYTHONHASHSEED": "1"},
        check=True,
        text=True,
        capture_output=True,
    ).stdout
    commands = []
    for line in output.splitlines():
        marker = line.find(" runner [")
        if marker < 0 or TARGET not in line:
            continue
        commands.append(ast.literal_eval(line[marker + len(" runner ") :]))
    return commands


def main() -> int:
    samba = Path(sys.argv[1]).resolve()
    module_source = Path(sys.argv[2]).resolve()
    build_dir = samba / "bin" / "default"
    commands = capture_commands(samba)
    compile_commands = [c for c in commands if "-c" in c]
    link_commands = [c for c in commands if "-shared" in c]
    if len(compile_commands) != 1 or len(link_commands) != 1:
        raise SystemExit(f"unexpected waf commands for {TARGET}: {commands}")

    compile_command = [
        str(module_source / f"{TARGET}.c")
        if argument.endswith(f"source3/modules/{TARGET}.c")
        else argument
        for argument in compile_commands[0]
    ]
    # -O0 after waf's flags wins; the module dir first resolves local headers.
    compile_command[1:1] = [f"-I{module_source}"]
    compile_command += ["--coverage", "-O0", "-fprofile-update=atomic"]
    link_command = link_commands[0] + ["--coverage"]

    object_path = next(a[2:] for a in compile_command if a.startswith("-o"))
    for stale in glob.glob(str(Path(object_path).with_suffix("")) + "*.gc*"):
        os.unlink(stale)
    subprocess.run(compile_command, cwd=build_dir, check=True)
    subprocess.run(link_command, cwd=build_dir, check=True)

    built = next(a[2:] for a in link_command if a.startswith("-o"))
    built_path = Path(built) if os.path.isabs(built) else build_dir / built
    installed = glob.glob("/opt/samba/lib/**/*kaimo_bridge*.so", recursive=True)
    if len(installed) != 1:
        raise SystemExit(f"cannot locate installed module: {installed}")
    shutil.copy2(built_path, installed[0])
    # smbd children may drop privileges before exiting; let them write .gcda.
    os.chmod(Path(object_path).parent, 0o1777)
    print(f"instrumented {installed[0]} (notes: {Path(object_path).parent})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
