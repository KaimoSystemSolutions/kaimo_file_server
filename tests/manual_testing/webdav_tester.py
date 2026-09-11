#!/usr/bin/env python3
"""
╔══════════════════════════════════════════════════════════════╗
║                   WEBDAV SERVER TESTER                       ║
║        End-to-end smoke test of the /dav transport           ║
║                                                              ║
║  Dependency:  none (Python standard library only)            ║
║  Usage:       python webdav_tester.py --config config.json   ║
║               python webdav_tester.py --url https://host:8443 \
║                        --user admin --password admin1234 \
║                        --share Projekte                      ║
╚══════════════════════════════════════════════════════════════╝

Exercises the RFC 4918 method surface against a live server:
OPTIONS, PROPFIND (Depth 0 and 1), PUT, GET, MKCOL, MOVE, COPY, LOCK/UNLOCK
and DELETE — checking status codes and, where relevant, response bodies.

The server refuses HTTP Basic over plain HTTP by default (426), so point this
at the HTTPS endpoint. Self-signed certificates are accepted (--insecure, the
default) since that is the out-of-the-box deployment.
"""

import argparse
import base64
import http.client
import json
import ssl
import sys
import uuid
from urllib.parse import urlsplit, quote


class WebDavClient:
    def __init__(self, url, user, password, insecure=True):
        parts = urlsplit(url)
        self.https = parts.scheme == "https"
        self.host = parts.hostname
        self.port = parts.port or (443 if self.https else 80)
        self.auth = base64.b64encode(f"{user}:{password}".encode()).decode()
        self.insecure = insecure

    def _conn(self):
        if self.https:
            ctx = ssl.create_default_context()
            if self.insecure:
                ctx.check_hostname = False
                ctx.verify_mode = ssl.CERT_NONE
            return http.client.HTTPSConnection(self.host, self.port, context=ctx, timeout=30)
        return http.client.HTTPConnection(self.host, self.port, timeout=30)

    def request(self, method, path, body=None, headers=None):
        conn = self._conn()
        h = {"Authorization": f"Basic {self.auth}"}
        if headers:
            h.update(headers)
        conn.request(method, path, body=body, headers=h)
        resp = conn.getresponse()
        data = resp.read()
        conn.close()
        return resp.status, dict(resp.getheaders()), data


PASS, FAIL = "\033[92mPASS\033[0m", "\033[91mFAIL\033[0m"


def main():
    ap = argparse.ArgumentParser(description="WebDAV /dav smoke tester")
    ap.add_argument("--config")
    ap.add_argument("--url", default="https://127.0.0.1:8443")
    ap.add_argument("--user", default="admin")
    ap.add_argument("--password", default="admin1234")
    ap.add_argument("--share", default="")
    ap.add_argument("--secure", action="store_true", help="verify TLS certificate")
    args = ap.parse_args()

    if args.config:
        with open(args.config, encoding="utf-8") as f:
            cfg = json.load(f)
        args.url = cfg.get("url", args.url)
        args.user = cfg.get("username", args.user)
        args.password = cfg.get("password", args.password)
        args.share = cfg.get("test_share", args.share)

    client = WebDavClient(args.url, args.user, args.password, insecure=not args.secure)
    failures = 0

    def check(label, ok, detail=""):
        nonlocal failures
        print(f"  [{PASS if ok else FAIL}] {label}{('  ' + detail) if detail else ''}")
        if not ok:
            failures += 1

    print(f"\nWebDAV tester → {args.url}/dav/  (user: {args.user})\n")

    # OPTIONS (anonymous-capable) advertises the DAV classes.
    status, headers, _ = client.request("OPTIONS", "/dav/")
    dav = headers.get("DAV", "")
    check("OPTIONS advertises DAV 1,2,3", status == 200 and "3" in dav, f"DAV: {dav!r}")

    # Discover a share to test against if none was given.
    share = args.share
    if not share:
        status, _, body = client.request(
            "PROPFIND", "/dav/", headers={"Depth": "1"})
        text = body.decode("utf-8", "replace")
        # Naively pull the first child href under /dav/.
        for line in text.replace("><", ">\n<").splitlines():
            if "<D:href>/dav/" in line and line.strip() != "<D:href>/dav/</D:href>":
                share = line.split("/dav/")[1].split("<")[0].strip("/")
                break
        check("PROPFIND root lists a share", bool(share), f"picked: {share!r}")
    if not share:
        print("\nNo share available to test; stopping.\n")
        return 1

    base = f"/dav/{quote(share)}"
    folder = f"{base}/webdav_test_{uuid.uuid4().hex[:8]}"
    file1 = f"{folder}/hello.txt"
    file2 = f"{folder}/renamed.txt"
    copy = f"{folder}/copy.txt"

    status, _, _ = client.request("PROPFIND", base + "/", headers={"Depth": "0"})
    check("PROPFIND share root (Depth 0)", status == 207, f"status {status}")

    status, _, _ = client.request("PROPFIND", base + "/", headers={"Depth": "infinity"})
    check("PROPFIND Depth infinity refused (403)", status == 403, f"status {status}")

    status, _, _ = client.request("MKCOL", folder)
    check("MKCOL creates collection", status in (201, 405), f"status {status}")

    payload = b"hello webdav\n"
    status, headers, _ = client.request("PUT", file1, body=payload)
    check("PUT creates file", status in (201, 204), f"status {status}")

    status, _, data = client.request("GET", file1)
    check("GET returns content", status == 200 and data == payload, f"status {status}")

    # LOCK, then a PUT without the token must be refused (423), with it allowed.
    lock_body = ('<?xml version="1.0"?><D:lockinfo xmlns:D="DAV:">'
                 "<D:lockscope><D:exclusive/></D:lockscope>"
                 "<D:locktype><D:write/></D:locktype>"
                 "<D:owner>webdav_tester</D:owner></D:lockinfo>")
    status, headers, _ = client.request(
        "LOCK", file1, body=lock_body, headers={"Timeout": "Second-120"})
    token = headers.get("Lock-Token", "").strip("<>")
    check("LOCK returns a token", status == 200 and token.startswith("opaquelocktoken:"), f"status {status}")

    if token:
        status, _, _ = client.request("PUT", file1, body=b"blocked")
        check("PUT on locked file refused (423)", status == 423, f"status {status}")

        status, _, _ = client.request(
            "PUT", file1, body=b"allowed", headers={"If": f"(<{token}>)"})
        check("PUT with lock token allowed", status in (200, 204), f"status {status}")

        status, _, _ = client.request("UNLOCK", file1, headers={"Lock-Token": f"<{token}>"})
        check("UNLOCK releases lock", status == 204, f"status {status}")

    status, _, _ = client.request(
        "COPY", file1, headers={"Destination": copy, "Overwrite": "T"})
    check("COPY duplicates file", status in (201, 204), f"status {status}")

    status, _, _ = client.request(
        "MOVE", file1, headers={"Destination": file2, "Overwrite": "T"})
    check("MOVE renames file", status in (201, 204), f"status {status}")

    status, _, _ = client.request("DELETE", folder)
    check("DELETE removes the test collection", status == 204, f"status {status}")

    print()
    if failures:
        print(f"  {failures} check(s) failed.\n")
        return 1
    print("  All checks passed.\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
