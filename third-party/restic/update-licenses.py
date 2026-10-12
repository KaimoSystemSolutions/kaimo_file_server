#!/usr/bin/env python3
"""Regenerates THIRD_PARTY_LICENSES.txt for a Go binary shipped in the Web image (restic, rclone).

restic and rclone are statically linked Go programs: besides the program itself (LICENSE in
third-party/<name>/) the binary contains the compiled code of every Go module it depends on
and of the Go standard library. Their licenses (MIT, BSD, Apache-2.0 incl. NOTICE files,
MPL-2.0) require the license texts to accompany a binary distribution, so they are collected
into third-party/<name>/THIRD_PARTY_LICENSES.txt.

The module list is read from the build info Go embeds in every binary, i.e. exactly what is
shipped. Only LICENSE/COPYING/NOTICE/PATENTS files are downloaded (raw.githubusercontent.com;
modules hosted elsewhere from the module zip on proxy.golang.org).

Run after every change of RESTIC_VERSION / KAIMO_RCLONE_VERSION in src/Kaimo_File_Server.Web/Dockerfile:

    docker cp kaimo_file_server_web:/usr/local/bin/restic /tmp/restic
    python3 third-party/restic/update-licenses.py /tmp/restic 0.19.1
    docker cp kaimo_file_server_web:/usr/local/bin/rclone /tmp/rclone
    python3 third-party/restic/update-licenses.py /tmp/rclone 1.75.2

The Docker build fails while a file was generated for a different version.
"""
import io
import os
import re
import sys
import urllib.error
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
THIRD_PARTY = os.path.dirname(HERE)
# License of the program itself, kept as third-party/<name>/LICENSE.
OWN_LICENSE = {"restic": "BSD 2-Clause License", "rclone": "MIT License"}
LICENSE_NAMES = ["LICENSE", "LICENSE.txt", "LICENSE.md", "LICENCE", "COPYING", "License", "license", "LICENSE-2.0.txt", "MIT-LICENSE"]
NOTICE_NAMES = ["NOTICE", "NOTICE.txt", "NOTICE.md", "PATENTS"]

# Vanity import paths -> GitHub repository (subdirectory = rest of the module path).
VANITY = [
    ("cloud.google.com/go", "googleapis/google-cloud-go"),
    ("google.golang.org/api", "googleapis/google-api-go-client"),
    ("google.golang.org/genproto", "googleapis/go-genproto"),
    ("google.golang.org/grpc", "grpc/grpc-go"),
    ("google.golang.org/protobuf", "protocolbuffers/protobuf-go"),
    ("go.opentelemetry.io/otel", "open-telemetry/opentelemetry-go"),
    ("go.opentelemetry.io/contrib", "open-telemetry/opentelemetry-go-contrib"),
    ("go.opentelemetry.io/auto", "open-telemetry/opentelemetry-go-instrumentation"),
    ("go.uber.org/automaxprocs", "uber-go/automaxprocs"),
    ("go.yaml.in/yaml/v3", "yaml/go-yaml"),
]


def build_info(binary):
    """(go version, main module, [(module, version)]) from the build info embedded in a Go binary."""
    data = open(binary, "rb").read()
    main_module = re.search(rb"path\t\S+\nmod\t(\S+)\t", data)
    if not main_module:
        sys.exit("no Go build info found in " + binary)
    start = main_module.start() + main_module.group(0).index(b"mod\t")
    # Header: 16-byte magic + pointer size + flags, padded to 32 bytes; then (Go >= 1.18)
    # the varint-length-prefixed Go version string.
    header = data.find(b"\xff Go buildinf:")
    pos, length, shift = header + 32, 0, 0
    while True:
        byte = data[pos]
        pos += 1
        length |= (byte & 0x7F) << shift
        shift += 7
        if byte < 0x80:
            break
    go = data[pos:pos + length].decode()
    if header < 0 or not go.startswith("go1."):
        sys.exit("could not read the Go version from " + binary)
    deps = []
    for line in data[start:start + 256 * 1024].decode("utf-8", "replace").split("\n")[1:]:
        fields = line.split("\t")
        if fields[0] == "=>":  # replace directive: the replacement is what was compiled in
            deps[-1] = (fields[1], fields[2])
            continue
        if fields[0] != "dep":
            break
        deps.append((fields[1], fields[2]))
    return go, main_module.group(1).decode(), deps


def locate(module):
    if module.startswith("github.com/"):
        parts = module.split("/")
        return "/".join(parts[1:3]), "/".join(parts[3:])
    if module.startswith("golang.org/x/"):
        parts = module.split("/")
        return f"golang/{parts[2]}", "/".join(parts[3:])
    for prefix, repo in VANITY:
        if module == prefix or module.startswith(prefix + "/"):
            return repo, "" if prefix.startswith("go.yaml.in") else module[len(prefix):].strip("/")
    return None  # fetched from the module proxy instead


def refs(subdir, version):
    pseudo = re.match(r"v\d+\.\d+\.\d+-(?:\d+\.)?\d{14}-([0-9a-f]{12})$", version)
    if pseudo:
        return [pseudo.group(1)]
    base = re.sub(r"/v\d+$", "", subdir)
    return list(dict.fromkeys([f"{d}/{version}" for d in (subdir, base) if d] + [version]))


def get(url):
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            return response.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise


def fetch(repo, subdir, version):
    """(ref, directory, {file name: text}) of the first directory holding a license file."""
    for ref in refs(subdir, version):
        for d in dict.fromkeys([subdir, re.sub(r"(^|/)v\d+$", "", subdir), ""]):
            base = f"https://raw.githubusercontent.com/{repo}/{ref}/{d + '/' if d else ''}"
            texts = {}
            for name in LICENSE_NAMES:
                if (text := get(base + name)) is not None:
                    texts[name] = text
                    break
            if texts:
                for name in NOTICE_NAMES:
                    if (text := get(base + name)) is not None:
                        texts[name] = text
                # Apache-2.0 4(d): a NOTICE of the enclosing repository applies to its sub-modules too.
                parts = d.split("/") if d else []
                for depth in range(len(parts) - 1, -1, -1):
                    parent = "/".join(parts[:depth])
                    for name in NOTICE_NAMES:
                        url = f"https://raw.githubusercontent.com/{repo}/{ref}/{parent + '/' if parent else ''}{name}"
                        if (text := get(url)) is not None:
                            texts[f"{name} ({parent or 'repository root'})"] = text
                return ref, d, texts
    return None


def fetch_proxy(module, version):
    """{file name: text} from the module zip on proxy.golang.org (license in the module root)."""
    escape = lambda s: re.sub(r"[A-Z]", lambda m: "!" + m.group(0).lower(), s)
    url = f"https://proxy.golang.org/{escape(module)}/@v/{escape(version)}.zip"
    with urllib.request.urlopen(url, timeout=300) as response:
        archive = zipfile.ZipFile(io.BytesIO(response.read()))
    root = f"{module}@{version}/"
    top = sorted(n[len(root):] for n in archive.namelist() if n.startswith(root) and "/" not in n[len(root):])
    # Any naming (LICENSE_BSD-2.txt, LICENSE-APACHE, …): all license files, then notices.
    licenses = [n for n in top if re.match(r"(licen[cs]e|copying)", n, re.I)]
    notices = [n for n in top if re.match(r"(notice|patents)", n, re.I)]
    texts = {n: archive.read(root + n).decode("utf-8", "replace") for n in licenses + notices}
    return texts if licenses else None


def kind(text):
    """License families found in a license file (some modules bundle several)."""
    found = []
    if "Mozilla Public License" in text:
        found.append("MPL-2.0")
    if "Apache License" in text and "Version 2.0" in text:
        found.append("Apache-2.0")
    if "Permission is hereby granted, free of charge" in text:
        found.append("MIT")
    if "Neither the name" in text or "names of its contributors" in text:
        found.append("BSD-3-Clause")
    elif "Redistributions in binary form" in text:
        found.append("BSD-2-Clause")
    # Upper-case title only: the MPL text itself mentions the LGPL.
    if "GNU LESSER GENERAL PUBLIC LICENSE" in text and "Version 3" in text:
        found.append("LGPL-3.0")
    if re.search(r"Permission to use, copy, modify, and(/or)?\s+distribute this software for any\s+purpose with or without fee", text):
        found.append("ISC" if "provided that the above" in text else "0BSD")
    if "CC0 1.0" in text:
        found.append("CC0-1.0")
    return " AND ".join(found) or "see text"


def section(component):
    """(index line, license section) of one component."""
    name, version = component
    if name == "Go standard library":
        texts = {n: get(f"https://raw.githubusercontent.com/golang/go/{version}/{n}") for n in ("LICENSE", "PATENTS")}
        source = f"https://github.com/golang/go/tree/{version}"
    elif (located := locate(name)) and (found := fetch(*located, version)):
        ref, d, texts = found
        source = f"https://github.com/{located[0]}/tree/{ref}" + (f"/{d}" if d else "")
    else:
        texts = fetch_proxy(name, version) or sys.exit(f"no license file found for {name} {version}")
        source = f"https://pkg.go.dev/{name}@{version}"
    license_kind = kind(next(iter(texts.values())))
    body = [f"{'=' * 79}", f"{name} {version}", f"License: {license_kind}", f"Source:  {source}", "=" * 79]
    if "MPL-2.0" in license_kind:
        body.append("This component is used unmodified. Its source code is available at the URL above.\n")
    if "LGPL-3.0" in license_kind:
        body.append("This component is used unmodified. Its source code is shipped in the source/ directory\n"
                    "next to this file; see \"SOURCE CODE AND RELINKING\" at the top of this file. The GNU GPL\n"
                    "version 3, which this license incorporates, is reproduced at the end of this file.\n")
    for file_name, text in texts.items():
        body += [f"--- {file_name} ---", text.rstrip() + "\n"]
    print("ok", name, version, license_kind, file=sys.stderr)
    return f"  {name} {version}  [{license_kind}]", "\n".join(body)


def lgpl_obligations(program, version, main_module, lgpl):
    """Stores the LGPL libraries' source next to the license file; returns the relinking notice."""
    source_dir = os.path.join(THIRD_PARTY, program, "source")
    os.makedirs(source_dir, exist_ok=True)
    for old in os.listdir(source_dir):
        os.remove(os.path.join(source_dir, old))
    escape = lambda s: re.sub(r"[A-Z]", lambda m: "!" + m.group(0).lower(), s)
    files = []
    for module, module_version in lgpl:
        name = f"{module.replace('/', '_')}@{module_version}.zip"
        url = f"https://proxy.golang.org/{escape(module)}/@v/{escape(module_version)}.zip"
        with urllib.request.urlopen(url, timeout=300) as response, open(os.path.join(source_dir, name), "wb") as out:
            out.write(response.read())
        files.append(f"  source/{name}\n    {module} {module_version}, unmodified, from {url}")
    repo = main_module.removeprefix("github.com/")
    return f"""SOURCE CODE AND RELINKING (GNU LGPL v3)
---------------------------------------
The binary contains the following libraries licensed under the GNU Lesser General Public
License v3 (LGPL-3.0). They are used unmodified. As required by LGPL-3.0 section 4, their
complete source code (the Minimal Corresponding Source) is shipped next to this file
(/usr/share/doc/{program}/source/ in the image, third-party/{program}/source/ in the Kaimo
File Server repository):
""" + "\n".join(files) + f"""

The Corresponding Application Code needed to relink {program} with a modified version of
these libraries is the {program} source of the exact release, published by its authors under
the {OWN_LICENSE[program]} at https://github.com/{repo}/tree/v{version} (go.mod and go.sum
pin every module; all of them are available from https://proxy.golang.org). To relink:
  git clone --branch v{version} https://github.com/{repo}.git
  cd {program} && go mod edit -replace <library module>=<path to the modified copy>
  go build -trimpath -o {program} .
and replace /usr/local/bin/{program} in the container (e.g. with a bind mount or a derived
image). The texts of the LGPL-3.0 and of the GNU GPL v3 it incorporates are reproduced below.

"""


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    binary, program_version = sys.argv[1], sys.argv[2]
    go_version, main_module, deps = build_info(binary)
    program = main_module.rsplit("/", 1)[-1]
    if program not in OWN_LICENSE:
        sys.exit(f"unknown program {main_module}: extend OWN_LICENSE")
    target = os.path.join(THIRD_PARTY, program, "THIRD_PARTY_LICENSES.txt")

    components = [("Go standard library", go_version)] + deps
    # Downloads dominate (several 404 probes per module): fetch in parallel, keep the order.
    with ThreadPoolExecutor(max_workers=16) as pool:
        results = list(pool.map(section, components))
    index = [line for line, _ in results]
    sections = [body for _, body in results]
    lgpl = [c for c, (line, _) in zip(components, results) if "LGPL-3.0" in line]
    relinking = ""
    if lgpl:
        relinking = lgpl_obligations(program, program_version, main_module, lgpl)
        gpl = get("https://www.gnu.org/licenses/gpl-3.0.txt") or sys.exit("could not download the GNU GPL v3")
        sections.append("\n".join(["=" * 79, "GNU GENERAL PUBLIC LICENSE, Version 3",
                                    "Incorporated by the GNU Lesser General Public License v3 (LGPL-3.0 4(b)).",
                                    "=" * 79, gpl.rstrip() + "\n"]))

    header = f"""THIRD-PARTY LICENSES OF THE BUNDLED {program} BINARY
{program} {program_version} (built with {go_version}), installed as /usr/local/bin/{program}

The Kaimo File Server Web image ships the unmodified, checksum-pinned official {program}
release binary. {program} itself is licensed under the {OWN_LICENSE[program]} (see LICENSE in
this directory). As a statically linked Go program, the binary also contains the
following components, whose license texts and notices are reproduced below.

Generated by third-party/restic/update-licenses.py from the build information embedded
in the binary. Do not edit by hand.

""" + relinking + """Components:
""" + "\n".join(index) + "\n\n"
    with open(target, "w", encoding="utf-8", newline="\n") as out:
        out.write(header + "\n\n".join(sections))
    print(f"wrote {target} ({len(components)} components)", file=sys.stderr)


main()
