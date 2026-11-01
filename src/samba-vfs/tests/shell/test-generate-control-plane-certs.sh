#!/bin/bash
# generate-control-plane-certs.sh: fresh PKI, idempotent re-run with key-mode
# repair, migration from the former four-client layout, refusal to overwrite
# an incomplete PKI, restart after an interrupted generation, the missing-openssl / foreign-UID fallbacks, and runs as
# the Host's non-root app UID.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS="${KAIMO_SCRIPTS_DIR:-$HERE/../..}"
SUT="${GENERATE_CERTS_SUT:-$SCRIPTS/generate-control-plane-certs.sh}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
fail=0
check() {
    if eval "$2"; then
        echo "  ok: $1"
    else
        echo "FAIL: $1"
        fail=1
    fi
}
mode() { stat -c %a "$1"; }
digest() { sha256sum "$1" | cut -d" " -f1; }
san() { openssl x509 -in "$1" -noout -ext subjectAltName 2>/dev/null; }
eku() { openssl x509 -in "$1" -noout -ext extendedKeyUsage 2>/dev/null; }

# ---------------------------------------------------------------- fresh
pki="$WORK/pki"
output="$(bash "$SUT" "$pki" 2>&1)"
rc=$?
check "fresh generation succeeds" '[ "$rc" -eq 0 ] && grep -q "PKI generated in $pki" <<<"$output"'
for file in authority/ca.crt authority/ca.key bridge/ca.crt bridge/server.crt bridge/server.key \
    samba/ca.crt samba/samba.crt samba/samba.key; do
    check "creates $file" '[ -s "$pki/$file" ]'
done
check "leaf certificates chain to the CA" \
    'openssl verify -CAfile "$pki/authority/ca.crt" "$pki/bridge/server.crt" "$pki/samba/samba.crt" >/dev/null'
check "bridge identity is a server for kaimo_smb_bridge" \
    'san "$pki/bridge/server.crt" | grep -q "DNS:kaimo_smb_bridge" && eku "$pki/bridge/server.crt" | grep -q "TLS Web Server Authentication"'
check "samba identity is a client" 'eku "$pki/samba/samba.crt" | grep -q "TLS Web Client Authentication"'
check "CA private key never leaves the authority" \
    '[ ! -e "$pki/bridge/ca.key" ] && [ ! -e "$pki/samba/ca.key" ]'
check "authority directory and CA key are private" \
    '[ "$(mode "$pki/authority")" = 700 ] && [ "$(mode "$pki/authority/ca.key")" = 600 ]'
check "samba key is private" '[ "$(mode "$pki/samba/samba.key")" = 600 ]'
check "bridge key is handed to the bridge UID" \
    '[ "$(stat -c %u "$pki/bridge/server.key")" = 1654 ] && [ "$(mode "$pki/bridge/server.key")" = 600 ]'
check "certificates are world readable" '[ "$(mode "$pki/samba/samba.crt")" = 644 ]'
check "no CSR or extension leftovers" '! ls "$pki/authority" | grep -qE "\.(csr|ext)$"'

# ---------------------------------------------------------------- idempotent
before_ca="$(digest "$pki/authority/ca.crt")"
before_client="$(digest "$pki/samba/samba.crt")"
chmod 644 "$pki/samba/samba.key"
output="$(bash "$SUT" "$pki" 2>&1)"
rc=$?
check "re-run reports existing PKI" '[ "$rc" -eq 0 ] && grep -q "already present" <<<"$output"'
check "re-run rotates nothing" \
    '[ "$(digest "$pki/authority/ca.crt")" = "$before_ca" ] && [ "$(digest "$pki/samba/samba.crt")" = "$before_client" ]'
check "re-run tightens a loosened key mode" '[ "$(mode "$pki/samba/samba.key")" = 600 ]'

# ---------------------------------------------------------------- migration
before_server="$(digest "$pki/bridge/server.crt")"
rm "$pki/samba/samba.crt" "$pki/samba/samba.key"
for legacy in auth-sync share-sync config-sync runtime; do
    echo legacy > "$pki/samba/$legacy.crt"
    echo legacy > "$pki/samba/$legacy.key"
done
output="$(bash "$SUT" "$pki" 2>&1)"
rc=$?
check "migration issues the shared samba identity" \
    '[ "$rc" -eq 0 ] && openssl verify -CAfile "$pki/authority/ca.crt" "$pki/samba/samba.crt" >/dev/null'
check "migration keeps the bridge identity and CA" \
    '[ "$(digest "$pki/bridge/server.crt")" = "$before_server" ] && [ "$(digest "$pki/authority/ca.crt")" = "$before_ca" ]'
check "migration removes the four legacy client identities" \
    '! ls "$pki/samba" | grep -qE "^(auth-sync|share-sync|config-sync|runtime)\."'

# ---------------------------------------------------------------- incomplete
rm "$pki/bridge/server.key"
before_samba="$(digest "$pki/samba/samba.crt")"
output="$(bash "$SUT" "$pki" 2>&1)"
rc=$?
check "incomplete PKI is never overwritten" \
    '[ "$rc" -ne 0 ] && grep -q "refusing to overwrite incomplete PKI" <<<"$output" && [ "$(digest "$pki/samba/samba.crt")" = "$before_samba" ]'

# ---------------------------------------------------------------- interrupted
# The Host may be stopped, or time out, in the middle of a fresh generation. The
# marker that run leaves behind lets the next start begin again instead of
# refusing the incomplete PKI forever.
interrupted="$WORK/interrupted"
bash "$SUT" "$interrupted" >/dev/null 2>&1
check "a finished generation leaves no marker" '[ ! -e "$interrupted/authority/.generating" ]'
before_interrupted_ca="$(digest "$interrupted/authority/ca.crt")"
: > "$interrupted/authority/.generating"
rm "$interrupted/samba/ca.crt"
output="$(bash "$SUT" "$interrupted" 2>&1)"
rc=$?
check "interrupted generation starts over" \
    '[ "$rc" -eq 0 ] && grep -q "PKI generated" <<<"$output" && [ "$(digest "$interrupted/authority/ca.crt")" != "$before_interrupted_ca" ]'
check "restarted generation is complete and consistent" \
    'openssl verify -CAfile "$interrupted/samba/ca.crt" "$interrupted/bridge/server.crt" "$interrupted/samba/samba.crt" >/dev/null && [ ! -e "$interrupted/authority/.generating" ]'

# Stopped after the last file was written but before the marker was removed:
# the PKI is complete and may already be in use, so it is kept.
before_interrupted_ca="$(digest "$interrupted/authority/ca.crt")"
: > "$interrupted/authority/.generating"
output="$(bash "$SUT" "$interrupted" 2>&1)"
rc=$?
check "complete PKI with a leftover marker is kept" \
    '[ "$rc" -eq 0 ] && grep -q "already present" <<<"$output" && [ "$(digest "$interrupted/authority/ca.crt")" = "$before_interrupted_ca" ] && [ ! -e "$interrupted/authority/.generating" ]'

# ---------------------------------------------------------------- fallbacks
output="$(KAIMO_BRIDGE_UID=not-a-user bash "$SUT" "$WORK/fallback" 2>&1)"
rc=$?
check "unassignable bridge key falls back with a warning" \
    '[ "$rc" -eq 0 ] && grep -q "could not assign" <<<"$output" && [ "$(mode "$WORK/fallback/bridge/server.key")" = 644 ]'

# ---------------------------------------------------------------- non-root owner
# The Host runs the generator as the .NET app UID. A PKI created earlier by the
# former root-run pki-init container stays root-owned: it must be accepted as
# present without failing on chmod of files the UID does not own.
if [ "$(id -u)" -eq 0 ] && command -v setpriv >/dev/null 2>&1; then
    chmod 711 "$WORK"
    # The pristine source, not a kcov wrapper: kcov cannot write its output as the app UID.
    APP_SUT="${KAIMO_SCRIPT_SOURCES:-$(dirname "$SUT")}/generate-control-plane-certs.sh"
    as_app() { setpriv --reuid=1654 --regid=1654 --clear-groups bash "$APP_SUT" "$@"; }

    # Docker created the bind-mount directory itself as root:root 0755.
    root_pki="$WORK/root-owned"
    install -d -m 755 "$root_pki"
    bash "$SUT" "$root_pki" >/dev/null 2>&1
    before_root_ca="$(digest "$root_pki/authority/ca.crt")"
    output="$(as_app "$root_pki" 2>&1)"
    rc=$?
    check "app UID accepts a root-owned PKI as present" \
        '[ "$rc" -eq 0 ] && grep -q "already present" <<<"$output"'
    check "app UID leaves the root-owned PKI untouched" \
        '[ "$(digest "$root_pki/authority/ca.crt")" = "$before_root_ca" ] && [ "$(mode "$root_pki/samba/samba.key")" = 600 ]'

    app_pki="$WORK/app-owned"
    install -d -o 1654 -g 1654 "$app_pki"
    output="$(as_app "$app_pki" 2>&1)"
    rc=$?
    check "app UID generates a fresh PKI without warnings" \
        '[ "$rc" -eq 0 ] && grep -q "PKI generated" <<<"$output" && ! grep -q "warning" <<<"$output"'
    check "app UID keeps every private key private" \
        '[ "$(mode "$app_pki/samba/samba.key")" = 600 ] && [ "$(mode "$app_pki/bridge/server.key")" = 600 ] && [ "$(mode "$app_pki/authority/ca.key")" = 600 ]'
else
    echo "  skip: non-root owner cases need root and setpriv"
fi

mkdir "$WORK/nossl"
for tool in mkdir chmod; do ln -s "$(command -v "$tool")" "$WORK/nossl/$tool"; done
output="$(PATH="$WORK/nossl" "$(command -v bash)" "$SUT" "$WORK/missing" 2>&1)"
rc=$?
check "missing openssl is reported" '[ "$rc" -ne 0 ] && grep -q "missing required command: openssl" <<<"$output"'

if [ "$fail" -ne 0 ]; then
    exit 1
fi
echo "PASS: control-plane PKI is generated, preserved and migrated safely."
