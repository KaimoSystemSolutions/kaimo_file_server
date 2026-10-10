#!/usr/bin/env python3
"""Regenerates THIRD_PARTY_LICENSES.txt for the restic binary shipped in the Web image.

restic is a statically linked Go program: besides restic itself (LICENSE, BSD-2-Clause) the
binary contains the compiled code of every Go module it depends on and of the Go standard
library. Their licenses (MIT, BSD, Apache-2.0 incl. NOTICE files, MPL-2.0) require the
license texts to accompany a binary distribution, so they are collected here.

The module list is read from the build info Go embeds in every binary, i.e. exactly what is
shipped. Only LICENSE/COPYING/NOTICE/PATENTS files are downloaded (raw.githubusercontent.com).

Run after every change of RESTIC_VERSION in src/Kaimo_File_Server.Web/Dockerfile:

    docker cp kaimo_file_server_web:/usr/local/bin/restic /tmp/restic
    python3 third-party/restic/update-licenses.py /tmp/restic 0.19.1

The Docker build fails while the file was generated for a different restic version.
"""
import os
import re
import sys
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
TARGET = os.path.join(HERE, "THIRD_PARTY_LICENSES.txt")
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
    """(go version, [(module, version)]) from the build info embedded in a Go binary."""
    data = open(binary, "rb").read()
    start = data.find(b"mod\tgithub.com/restic/restic")
    if start < 0:
        sys.exit("no Go build info for restic found in " + binary)
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
    for line in data[start:start + 64 * 1024].decode("utf-8", "replace").split("\n")[1:]:
        fields = line.split("\t")
        if fields[0] != "dep":
            break
        deps.append((fields[1], fields[2]))
    return go, deps


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
    sys.exit("unknown module host, extend VANITY: " + module)


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
                return ref, d, texts
    sys.exit(f"no license file found for {repo} {subdir} {version}")


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
    return " AND ".join(found) or "see text"


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    binary, restic_version = sys.argv[1], sys.argv[2]
    go_version, deps = build_info(binary)

    components = [("Go standard library", go_version, "golang/go", go_version, "")]
    components += [(m, v, *locate(m)) for m, v in deps]
    sections, index = [], []
    for name, version, repo, *rest in components:
        if name == "Go standard library":
            ref, d = rest[0], ""
            texts = {n: get(f"https://raw.githubusercontent.com/{repo}/{ref}/{n}") for n in ("LICENSE", "PATENTS")}
        else:
            ref, d, texts = fetch(repo, rest[0], version)
        source = f"https://github.com/{repo}/tree/{ref}" + (f"/{d}" if d else "")
        license_kind = kind(next(iter(texts.values())))
        index.append(f"  {name} {version}  [{license_kind}]")
        body = [f"{'=' * 79}", f"{name} {version}", f"License: {license_kind}", f"Source:  {source}", "=" * 79]
        if "MPL-2.0" in license_kind:
            body.append("This component is used unmodified. Its source code is available at the URL above.\n")
        for file_name, text in texts.items():
            body += [f"--- {file_name} ---", text.rstrip() + "\n"]
        sections.append("\n".join(body))
        print("ok", name, version, license_kind, file=sys.stderr)

    header = f"""THIRD-PARTY LICENSES OF THE BUNDLED restic BINARY
restic {restic_version} (built with {go_version}), installed as /usr/local/bin/restic

The Kaimo File Server Web image ships the unmodified, checksum-pinned official restic
release binary. restic itself is licensed under the BSD 2-Clause License (see LICENSE in
this directory). As a statically linked Go program, the binary also contains the
following components, whose license texts and notices are reproduced below.

Generated by third-party/restic/update-licenses.py from the build information embedded
in the binary. Do not edit by hand.

Components:
""" + "\n".join(index) + "\n\n"
    with open(TARGET, "w", encoding="utf-8", newline="\n") as out:
        out.write(header + "\n\n".join(sections))
    print(f"wrote {TARGET} ({len(components)} components)", file=sys.stderr)


main()
