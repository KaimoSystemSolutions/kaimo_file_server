#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Kaimo File Server
"""Merge per-area coverage numbers and enforce the ratchet thresholds.

Inputs: gcovr JSON summary (C/C++), kcov merged cobertura (bash) and
coverage.py JSON (Python helpers). Writes a Markdown summary (used for the CI
job summary) and fails when an area drops below tests/quality-gates.json.

Usage: check-coverage.py <thresholds.json> <coverage-dir>
"""

from __future__ import annotations

import fnmatch
import json
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path


def gcovr_files(path: Path) -> dict[str, dict[str, float]]:
    data = json.loads(path.read_text())
    files = {}
    for entry in data.get("files", []):
        files[entry["filename"]] = {
            "lines": entry["line_total"],
            "lines_hit": entry["line_covered"],
            "branches": entry["branch_total"],
            "branches_hit": entry["branch_covered"],
        }
    return files


def cobertura_files(path: Path) -> dict[str, dict[str, float]]:
    files: dict[str, dict[str, float]] = {}
    if not path.exists():
        return files
    root = ElementTree.parse(path).getroot()
    for cls in root.iter("class"):
        name = Path(cls.get("filename", "")).name
        lines = cls.findall("./lines/line")
        entry = files.setdefault(name, {"lines": 0, "lines_hit": 0, "branches": 0, "branches_hit": 0})
        entry["lines"] += len(lines)
        entry["lines_hit"] += sum(1 for line in lines if int(line.get("hits", "0")) > 0)
    return files


def coveragepy_files(path: Path) -> dict[str, dict[str, float]]:
    files: dict[str, dict[str, float]] = {}
    if not path.exists():
        return files
    data = json.loads(path.read_text())
    for name, entry in data.get("files", {}).items():
        summary = entry["summary"]
        files[Path(name).name] = {
            "lines": summary["num_statements"],
            "lines_hit": summary["covered_lines"],
            "branches": summary.get("num_branches", 0),
            "branches_hit": summary.get("covered_branches", 0),
        }
    return files


def percent(hit: float, total: float) -> float | None:
    return None if total == 0 else 100.0 * hit / total


def fmt(value: float | None) -> str:
    return "–" if value is None else f"{value:.1f} %"


def main() -> int:
    thresholds = json.loads(Path(sys.argv[1]).read_text())
    out = Path(sys.argv[2])
    sources = {
        "gcovr": gcovr_files(out / "gcovr" / "summary.json"),
        "kcov": cobertura_files(out / "kcov" / "cobertura.xml"),
        "python": coveragepy_files(out / "python" / "coverage.json"),
    }
    rows = []
    failures = []
    for area in thresholds["areas"]:
        files = sources[area["source"]]
        matched = {
            name: numbers
            for name, numbers in files.items()
            if any(fnmatch.fnmatch(name, pattern) for pattern in area["files"])
        }
        totals = {key: sum(n[key] for n in matched.values()) for key in
                  ("lines", "lines_hit", "branches", "branches_hit")}
        line = percent(totals["lines_hit"], totals["lines"])
        branch = percent(totals["branches_hit"], totals["branches"])
        rows.append((area["name"], len(matched), line, branch,
                     area.get("min_line"), area.get("min_branch")))
        if not matched:
            failures.append(f"{area['name']}: no coverage data for {area['files']}")
            continue
        if area.get("min_line") is not None and (line or 0) + 1e-9 < area["min_line"]:
            failures.append(f"{area['name']}: line {fmt(line)} < {area['min_line']} %")
        if area.get("min_branch") is not None and branch is not None and branch + 1e-9 < area["min_branch"]:
            failures.append(f"{area['name']}: branch {fmt(branch)} < {area['min_branch']} %")

    lines = [
        "## Samba VFS component coverage",
        "",
        "| Area | Files | Lines | Branches | Min lines | Min branches |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for name, count, line, branch, min_line, min_branch in rows:
        lines.append(
            f"| {name} | {count} | {fmt(line)} | {fmt(branch)} | "
            f"{'' if min_line is None else f'{min_line} %'} | "
            f"{'' if min_branch is None else f'{min_branch} %'} |"
        )
    lines.append("")
    lines.append("### Per file")
    lines.append("")
    lines.append("| File | Lines | Branches |")
    lines.append("|---|---:|---:|")
    for source in sources.values():
        for name, n in sorted(source.items()):
            lines.append(
                f"| `{name}` | {fmt(percent(n['lines_hit'], n['lines']))} | "
                f"{fmt(percent(n['branches_hit'], n['branches']))} |"
            )
    if failures:
        lines += ["", "### Threshold failures", ""] + [f"- {f}" for f in failures]
    report = "\n".join(lines) + "\n"
    (out / "summary.md").write_text(report)
    print(report)
    if failures and not thresholds.get("report_only", False):
        print("coverage thresholds not met:\n  " + "\n  ".join(failures), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
