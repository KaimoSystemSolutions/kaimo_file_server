#!/usr/bin/env python3
"""Regenerates the NuGet license notices shipped in a .NET image (Host, Web, SmbBridge).

The published .NET applications contain the assemblies (and, for SkiaSharp, the native
library) of every NuGet package in their runtime closure. Their licenses (MIT, BSD,
Apache-2.0, PostgreSQL, ...) require the copyright and permission notices to accompany a
binary distribution, so they are collected into
third-party/nuget/<application>.THIRD_PARTY_LICENSES.txt and shipped in the image as
/usr/share/doc/kaimo/NUGET_THIRD_PARTY_LICENSES.txt.

The package list is read from the application's <application>.deps.json in the published
output, i.e. exactly what is shipped. License data is read from the restored packages in
the NuGet global packages folder (NUGET_PACKAGES, default ~/.nuget/packages): the .nuspec
(<license>, <copyright>) and the license and notice files inside the package. For packages
that only declare an SPDX license expression, the standard SPDX text is added.

Run after every package change (Directory.Packages.props or a PackageReference):

    docker build -f src/Kaimo_File_Server.Web/Dockerfile --target build -t kaimo-web-build .
    docker run --rm kaimo-web-build cat /app/publish/Kaimo_File_Server.Web.deps.json > /tmp/Kaimo_File_Server.Web.deps.json
    python3 third-party/nuget/update-licenses.py /tmp/Kaimo_File_Server.Web.deps.json

likewise for src/Kaimo_File_Server.Host (Kaimo_File_Server.Host.deps.json) and
src/Kaimo_File_Server.SmbBridge (Kaimo_File_Server.SmbBridge.deps.json). Several deps.json
files may be passed at once. If a package is missing from the global packages folder,
run "dotnet restore <project> -r linux-x64" first.

The Docker build fails while the file was generated for a different package list.
"""
import glob
import hashlib
import os
import re
import sys
import urllib.request
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
PACKAGES = os.environ.get("NUGET_PACKAGES") or os.path.expanduser("~/.nuget/packages")
SPDX_TEXT = "https://raw.githubusercontent.com/spdx/license-list-data/v3.27.0/text/{}.txt"
# Licenses accepted without review: permissive, no copyleft, no field-of-use restriction.
PERMISSIVE = {"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "PostgreSQL", "ISC", "0BSD", "Zlib", "MS-PL"}
# License and notice files reproduced from the package root.
FILES = re.compile(r"^(licen[cs]e|copying|notice|third[-_ ]?party|thirdpartynotices)", re.I)
spdx_cache = {}


def package_paths(deps_json):
    """Sorted "id/version" (lower case) of every package in a deps.json.

    Same extraction as the check in the Dockerfiles (grep '"path": "..."'), so the hash matches.
    """
    with open(deps_json, encoding="utf-8") as f:
        return sorted(set(re.findall(r'"path": "([^"]*)"', f.read())))


def read_text(path):
    with open(path, "rb") as f:
        return f.read().decode("utf-8-sig", "replace").replace("\r\n", "\n").rstrip() + "\n"


def spdx(identifier, copyright):
    """Standard license text with the package's copyright notice in place of the placeholder line
    ("Copyright (c) <year> <owner>"), or in front of the text if it has none (Apache-2.0)."""
    if identifier not in spdx_cache:
        with urllib.request.urlopen(SPDX_TEXT.format(identifier), timeout=30) as response:
            spdx_cache[identifier] = response.read().decode("utf-8")
    if copyright and not re.match(r"(copyright|©|\(c\))", copyright, re.I):
        copyright = "Copyright (c) " + copyright
    lines = spdx_cache[identifier].split("\n")
    placeholder = next((i for i, l in enumerate(lines) if re.search(r"copyright.*<[^>]+>", l, re.I)), None)
    if placeholder is not None:
        lines[placeholder:placeholder + 1] = [copyright] if copyright else []
    elif copyright:
        lines[:0] = [copyright, ""]
    return re.sub(r"\n{3,}", "\n\n", "\n".join(lines)).strip() + "\n"


def metadata(path):
    d = os.path.join(PACKAGES, path)
    nuspecs = glob.glob(os.path.join(d, "*.nuspec"))
    if not nuspecs:
        sys.exit(f"package {path} not found in {PACKAGES}: run dotnet restore <project> -r linux-x64")
    md = ET.parse(nuspecs[0]).getroot().find("{*}metadata")
    get = lambda tag: (md.findtext("{*}" + tag) or "").strip()
    license = md.find("{*}license")
    expression = license_file = None
    if license is not None and license.get("type") == "expression":
        expression = license.text.strip()
    elif license is not None and license.get("type") == "file":
        license_file = license.text.strip().replace("\\", "/")
    elif get("licenseUrl"):
        expression = "see " + get("licenseUrl")
    return d, get("id"), get("version"), expression, license_file, get("copyright"), get("authors"), get("projectUrl")


def permissive(expression):
    ids = re.findall(r"[A-Za-z0-9.+-]+", expression or "")
    ids = [i for i in ids if i not in ("AND", "OR", "WITH")]
    return bool(ids) and all(i in PERMISSIVE for i in ids)


def section(path, seen):
    """(index line, license section) of one package; seen maps text hash -> first package."""
    d, pid, version, expression, license_file, copyright, authors, project_url = metadata(path)
    label = expression or f"see {license_file}"
    if not permissive(expression):
        print(f"REVIEW {pid} {version}: license {label} is not on the permissive allowlist", file=sys.stderr)
    body = ["=" * 79, f"{pid} {version}", f"License:   {label}",
            f"Copyright: {copyright or f'not stated in the package (authors: {authors})'}",
            f"Source:    https://www.nuget.org/packages/{pid}/{version}"]
    if project_url:
        body.append(f"Project:   {project_url}")
    body.append("=" * 79)

    files = sorted(n for n in os.listdir(d) if FILES.match(n) and os.path.isfile(os.path.join(d, n)))
    if license_file and license_file not in files:
        files.insert(0, license_file)
    texts = [(name, read_text(os.path.join(d, name))) for name in files]
    if expression and not any(re.match(r"(licen[cs]e|copying)", name, re.I) for name, _ in texts):
        for identifier in re.findall(r"[A-Za-z0-9.+-]+", expression):
            if identifier in PERMISSIVE:
                texts.insert(0, (f"{identifier} (standard SPDX text; the package includes no license file)",
                                 spdx(identifier, copyright)))
    for name, text in texts:
        digest = hashlib.sha256(text.encode()).hexdigest()
        if digest in seen:
            body.append(f"--- {name}: identical to the text reproduced above for {seen[digest]} ---\n")
        else:
            seen[digest] = f"{pid} {version}"
            body += [f"--- {name} ---", text]
    print("ok", pid, version, label, file=sys.stderr)
    return f"  {pid} {version}  [{label}]", "\n".join(body)


def generate(deps_json):
    application = os.path.basename(deps_json).removesuffix(".deps.json")
    paths = package_paths(deps_json)
    digest = hashlib.sha256("".join(p + "\n" for p in paths).encode()).hexdigest()
    seen = {}
    results = [section(p, seen) for p in sorted(paths, key=lambda p: p.split("/"))]
    header = f"""THIRD-PARTY LICENSES OF THE NUGET PACKAGES IN {application}
Installed as /usr/share/doc/kaimo/NUGET_THIRD_PARTY_LICENSES.txt

The Kaimo File Server image ships the published {application} application in /app.
Besides the Kaimo code (see LICENSE and NOTICE in this directory) it contains the
unmodified assemblies and native libraries of the NuGet packages listed below, as
resolved in {application}.deps.json. Their license texts, copyright notices and any
NOTICE / THIRD-PARTY-NOTICES files included in the packages are reproduced below. For
a package that declares an SPDX license expression but includes no license file, the
standard SPDX text of that license is reproduced, preceded by the copyright notice from
the package metadata.

Generated by third-party/nuget/update-licenses.py from the deps.json of the published
application and the restored packages. Do not edit by hand.
Package list SHA-256: {digest}

Packages:
""" + "\n".join(line for line, _ in results) + "\n\n"
    target = os.path.join(HERE, f"{application}.THIRD_PARTY_LICENSES.txt")
    with open(target, "w", encoding="utf-8", newline="\n") as out:
        out.write(header + "\n".join(body for _, body in results))
    print(f"wrote {target} ({len(paths)} packages)", file=sys.stderr)


if len(sys.argv) < 2:
    sys.exit(__doc__)
for deps in sys.argv[1:]:
    generate(deps)
