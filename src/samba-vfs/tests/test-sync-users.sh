#!/bin/bash
# Unit test for sync-users.sh — runs WITHOUT Samba/Docker against stub binaries.
#
# Catches the regression that once broke SMB access: if all Kaimo users get
# the same UID (formerly "force user"/shared UID), Samba's per-user identity
# collapses (SID derived from UID + getpwuid lookup) and auth/connect breaks.
# Core assertion here: EACH user gets their OWN UID.
#
# Usage:  bash src/samba-vfs/tests/test-sync-users.sh   (Exit 0 = OK, 1 = FAIL)
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Default: test the real script; overridable via SYNC_USERS_SUT (regression demo).
SUT="${SYNC_USERS_SUT:-$HERE/../sync-users.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

export PASSWD_FILE="$WORK/passwd"
export GROUP_LOG="$WORK/groups"
export IMPORT_CAPTURE="$WORK/imported.smbpasswd"
export IMPORT_PATH_LOG="$WORK/import-path"
export IMPORT_MODE_LOG="$WORK/import-mode"
export AUTH_CALL_LOG="$WORK/auth-calls"
export KAIMO_SYNC_RUNTIME_DIR="$WORK/runtime"
export KAIMO_SAMBA_PATH_PREFIX=""
: > "$PASSWD_FILE"
: > "$GROUP_LOG"
: > "$AUTH_CALL_LOG"
echo 1001 > "$PASSWD_FILE.cnt"     # Starting UID for auto-assignment

mkdir -p "$WORK/bin"

# --- Stub: kaimo_authsync (3 demo users with valid 16-byte NT-Hashes hex) ---
cat > "$WORK/bin/kaimo_authsync" <<'EOF'
#!/bin/bash
printf 'called\n' >> "$AUTH_CALL_LOG"
printf 'admin\t%s\n'         '31D6CFE0D16AE931B73C59D7E0C089C0'
printf 'marco.hanisch\t%s\n' 'AABBCCDDEEFF00112233445566778899'
printf 'anna.weber\t%s\n'    '00112233445566778899AABBCCDDEEFF'
EOF

# --- Stub: id  (existence check + `id -u NAME` from fake passwd) ---
cat > "$WORK/bin/id" <<'EOF'
#!/bin/bash
P="$PASSWD_FILE"
if [ "${1:-}" = "-u" ]; then
    [ "$#" -eq 1 ] && exec /usr/bin/id -u
    line="$(grep -F "$2:" "$P" 2>/dev/null | head -1)"
    [ -n "$line" ] && { printf '%s\n' "${line#*:}"; exit 0; } || exit 1
fi
grep -qF "$1:" "$P" 2>/dev/null && exit 0 || exit 1
EOF

# --- Stub: useradd  (respects -u N, otherwise auto-increment UID) ---
cat > "$WORK/bin/useradd" <<'EOF'
#!/bin/bash
P="$PASSWD_FILE"; C="$PASSWD_FILE.cnt"
uid=""; name=""; args=("$@")
for ((i=0; i<${#args[@]}; i++)); do
    case "${args[i]}" in
        -u) uid="${args[i+1]}"; ((i++)) ;;
        -*) : ;;                         # flag without relevant value for us
        *)  name="${args[i]}" ;;         # last non-flag value = username
    esac
done
[ -z "$name" ] && exit 1
if [ -z "$uid" ]; then n="$(cat "$C" 2>/dev/null || echo 1001)"; n=$((n+1)); echo "$n" > "$C"; uid="$n"; fi
echo "$name:$uid" >> "$P"
EOF

# --- Stub: usermod (-u N NAME -> change UID; should NOT be called in healthy path) ---
cat > "$WORK/bin/usermod" <<'EOF'
#!/bin/bash
P="$PASSWD_FILE"; uid=""; name=""; args=("$@")
if [ "${1:-}" = "-aG" ]; then
    printf '%s:%s\n' "$2" "$3" >> "$GROUP_LOG"
    exit 0
fi
for ((i=0; i<${#args[@]}; i++)); do
    case "${args[i]}" in
        -u) uid="${args[i+1]}"; ((i++)) ;;
        -*) : ;;
        *)  name="${args[i]}" ;;
    esac
done
grep -vF "$name:" "$P" > "$P.t" 2>/dev/null; mv "$P.t" "$P"
echo "$name:$uid" >> "$P"
EOF

cat > "$WORK/bin/pdbedit" <<'EOF'
#!/bin/bash
for arg in "$@"; do
    case "$arg" in
        smbpasswd:*) import_path="${arg#smbpasswd:}" ;;
    esac
done
[ -n "${import_path:-}" ] || exit 2
cp -- "$import_path" "$IMPORT_CAPTURE"
printf '%s\n' "$import_path" > "$IMPORT_PATH_LOG"
stat -c '%a' -- "$import_path" > "$IMPORT_MODE_LOG"
exit "${PDBEDIT_EXIT_CODE:-0}"
EOF
chmod +x "$WORK/bin"/*

export PATH="$WORK/bin:$PATH"

# ─────────────────────────── Run test ───────────────────────────
if ! bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: sync-users.sh returned failure"; exit 1
fi

fail=0
note() { printf '  %s\n' "$1"; }

# 1) Exactly 3 smbpasswd lines were written.
lines="$(grep -c ':' "$IMPORT_CAPTURE" 2>/dev/null || echo 0)"
if [ "$lines" -ne 3 ]; then
    echo "FAIL: expected 3 smbpasswd lines, got $lines"; fail=1
else
    note "ok: 3 smbpasswd lines"
fi

# 2) CORE ASSERTION: all UIDs (field 2) are distinct.
uids="$(cut -d: -f2 "$IMPORT_CAPTURE")"
total="$(printf '%s\n' "$uids" | grep -c .)"
uniq="$(printf '%s\n' "$uids" | sort -u | grep -c .)"
if [ "$total" != "$uniq" ]; then
    echo "FAIL: UIDs not distinct ($total total, $uniq unique) -> identity collapse!"
    printf '%s\n' "$uids" | sed 's/^/    uid=/'
    fail=1
else
    note "ok: $uniq distinct UIDs (no identity collapse)"
fi

# 3) Each smbpasswd line bears the expected username (field 1).
for u in admin marco.hanisch anna.weber; do
    if ! cut -d: -f1 "$IMPORT_CAPTURE" | grep -qx "$u"; then
        echo "FAIL: user '$u' missing from smbpasswd"; fail=1
    fi
done
[ "$fail" = 0 ] && note "ok: all 3 usernames present"

# 4) Every synchronized user is admitted to both the storage group and the
# private authd socket group without changing its primary UID.
for u in admin marco.hanisch anna.weber; do
    for g in kaimo kaimo-authd; do
        if ! grep -qx "$g:$u" "$GROUP_LOG"; then
            echo "FAIL: user '$u' missing secondary group '$g'"; fail=1
        fi
    done
done
[ "$fail" = 0 ] && note "ok: storage + authd secondary groups assigned"

# 5) The reusable hashes existed only in an unpredictable mode-0600 file
# inside the private runtime directory and the file was removed after import.
import_path="$(cat "$IMPORT_PATH_LOG" 2>/dev/null)"
case "$import_path" in
    "$KAIMO_SYNC_RUNTIME_DIR"/smbpasswd.*) : ;;
    *) echo "FAIL: import used an unexpected path: $import_path"; fail=1 ;;
esac
if [ "$(cat "$IMPORT_MODE_LOG" 2>/dev/null)" != "600" ]; then
    echo "FAIL: smbpasswd import file was not mode 0600"; fail=1
elif [ -e "$import_path" ]; then
    echo "FAIL: smbpasswd import file survived successful import"; fail=1
elif find "$KAIMO_SYNC_RUNTIME_DIR" -type f ! -name 'sync-users.lock' | grep -q .; then
    echo "FAIL: temporary sync files survived successful import"; fail=1
else
    note "ok: private unpredictable import file was mode 0600 and cleaned"
fi
if [ "$(stat -c '%a' "$KAIMO_SYNC_RUNTIME_DIR" 2>/dev/null)" != "700" ]; then
    echo "FAIL: runtime directory was not mode 0700"; fail=1
fi

# 6) A held lock rejects a concurrent invocation before it exports hashes.
before_calls="$(wc -l < "$AUTH_CALL_LOG")"
(
    exec 8>"$KAIMO_SYNC_RUNTIME_DIR/sync-users.lock"
    flock 8
    bash "$SUT" >/dev/null 2>&1
    printf '%s\n' "$?" > "$WORK/locked-exit"
)
locked_rc="$(cat "$WORK/locked-exit")"
after_calls="$(wc -l < "$AUTH_CALL_LOG")"
if [ "$locked_rc" -eq 0 ] || [ "$before_calls" != "$after_calls" ]; then
    echo "FAIL: concurrent sync was not rejected before hash export"; fail=1
else
    note "ok: synchronization lock rejects concurrent hash export"
fi

# 7) Cleanup also runs when pdbedit reports an import failure.
export PDBEDIT_EXIT_CODE=7
bash "$SUT" >/dev/null 2>&1
failed_import_path="$(cat "$IMPORT_PATH_LOG" 2>/dev/null)"
if [ -e "$failed_import_path" ] \
    || find "$KAIMO_SYNC_RUNTIME_DIR" -type f ! -name 'sync-users.lock' | grep -q .; then
    echo "FAIL: temporary sync files survived failed import"; fail=1
else
    note "ok: failed import leaves no temporary hash or log files"
fi

if [ "$fail" = 0 ]; then
    echo "PASS: sync-users.sh preserves identity and handles NT hashes privately."
    exit 0
else
    echo "FAILED."
    exit 1
fi
