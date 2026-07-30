#!/bin/bash
# Unit regression for sync-users.sh. Runs without Samba against stateful stubs.
#
# Verifies distinct POSIX identities, private NT-hash import, desired-state
# credential revocation, safe UID retention/reactivation, bootstrap ownership,
# and single-instance serialization.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUT="${SYNC_USERS_SUT:-$HERE/../sync-users.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

export PASSWD_FILE="$WORK/passwd"
export PASSDB_FILE="$WORK/passdb"
export GROUP_STATE="$WORK/group-state"
export GROUP_LOG="$WORK/group-log"
export LOCK_LOG="$WORK/lock-log"
export DELETE_LOG="$WORK/delete-log"
export AUTH_USERS_FILE="$WORK/auth-users"
export AUTH_CALL_LOG="$WORK/auth-calls"
export IMPORT_CAPTURE="$WORK/imported.smbpasswd"
export IMPORT_PATH_LOG="$WORK/import-path"
export IMPORT_MODE_LOG="$WORK/import-mode"
export IMPORT_RUNTIME_LOG="$WORK/import-runtime"
export SESSION_REVOKE_LOG="$WORK/session-revoke.log"
export KAIMO_SYNC_RUNTIME_DIR="$WORK/runtime"
export KAIMO_SYNC_STATE_DIR="$WORK/state"
export KAIMO_SAMBA_PATH_PREFIX=""
export KAIMO_UNMANAGED_SAMBA_USERS="kaimotest"
export KAIMO_SESSION_REVOKER="$WORK/bin/revoke-samba-sessions"

cat >"$PASSWD_FILE" <<'EOF'
stale.user:1401
kaimotest:1402
EOF
cat >"$PASSDB_FILE" <<'EOF'
stale.user:1401:Stale Kaimo user
kaimotest:1402:Reserved test user
EOF
cat >"$GROUP_STATE" <<'EOF'
kaimo:stale.user
kaimo-authd:stale.user
EOF
cat >"$AUTH_USERS_FILE" <<'EOF'
{"version":1,"users":[
  {"username":"admin","nt_hash":"31D6CFE0D16AE931B73C59D7E0C089C0"},
  {"username":"marco.hanisch","nt_hash":"AABBCCDDEEFF00112233445566778899"},
  {"username":"anna.weber","nt_hash":"00112233445566778899AABBCCDDEEFF"}
]}
EOF
: >"$GROUP_LOG"
: >"$LOCK_LOG"
: >"$DELETE_LOG"
: >"$AUTH_CALL_LOG"
: >"$SESSION_REVOKE_LOG"
echo 1500 >"$PASSWD_FILE.cnt"

mkdir -m 0700 "$KAIMO_SYNC_STATE_DIR"
printf '%s\n' \
    'stale.user' \
    'added interface eth0 ip=172.21.0.4 bcast=172.21.255.255 netmask=255.255.0.0' \
    >"$KAIMO_SYNC_STATE_DIR/managed-users"
chmod 0600 "$KAIMO_SYNC_STATE_DIR/managed-users"
mkdir -p "$WORK/bin"

cat >"$WORK/bin/kaimo_authsync" <<'EOF'
#!/bin/bash
printf 'called\n' >>"$AUTH_CALL_LOG"
cat "$AUTH_USERS_FILE"
EOF

cat >"$WORK/bin/revoke-samba-sessions" <<'EOF'
#!/bin/bash
printf '%s\n' "$*" >>"$SESSION_REVOKE_LOG"
exit "${SESSION_REVOKE_EXIT_CODE:-0}"
EOF

cat >"$WORK/bin/id" <<'EOF'
#!/bin/bash
P="$PASSWD_FILE"
if [ "${1:-}" = "-u" ]; then
    [ "$#" -eq 1 ] && exec /usr/bin/id -u
    line="$(grep -F "$2:" "$P" 2>/dev/null | head -1)"
    [ -n "$line" ] && { printf '%s\n' "${line#*:}"; exit 0; }
    exit 1
fi
if [ "${1:-}" = "-nG" ]; then
    grep -qF "$2:" "$P" 2>/dev/null || exit 1
    printf 'primary'
    while IFS=: read -r group member; do
        [ "$member" = "$2" ] && printf ' %s' "$group"
    done <"$GROUP_STATE"
    printf '\n'
    exit 0
fi
grep -qF "${1:-}:" "$P" 2>/dev/null
EOF

cat >"$WORK/bin/useradd" <<'EOF'
#!/bin/bash
P="$PASSWD_FILE"; C="$PASSWD_FILE.cnt"
name=""
for arg in "$@"; do
    case "$arg" in -*) : ;; *) name="$arg" ;; esac
done
[ -n "$name" ] || exit 1
n="$(cat "$C")"; n=$((n + 1)); printf '%s\n' "$n" >"$C"
printf '%s:%s\n' "$name" "$n" >>"$P"
EOF

cat >"$WORK/bin/usermod" <<'EOF'
#!/bin/bash
if [ "${1:-}" = "-aG" ]; then
    entry="$2:$3"
    grep -Fqx "$entry" "$GROUP_STATE" || printf '%s\n' "$entry" >>"$GROUP_STATE"
    printf 'add:%s\n' "$entry" >>"$GROUP_LOG"
    exit 0
fi
if [ "${1:-}" = "-L" ]; then
    user="${@: -1}"
    printf '%s\n' "$user" >>"$LOCK_LOG"
    exit 0
fi
exit 2
EOF

cat >"$WORK/bin/gpasswd" <<'EOF'
#!/bin/bash
[ "${1:-}" = "-d" ] || exit 2
entry="$3:$2"
grep -Fvx "$entry" "$GROUP_STATE" >"$GROUP_STATE.tmp" || true
mv "$GROUP_STATE.tmp" "$GROUP_STATE"
printf 'remove:%s\n' "$entry" >>"$GROUP_LOG"
EOF

cat >"$WORK/bin/pdbedit" <<'EOF'
#!/bin/bash
if [ "${1:-}" = "-L" ]; then
    printf '%s\n' \
        'added interface eth0 ip=172.21.0.4 bcast=172.21.255.255 netmask=255.255.0.0' \
        >&2
    cat "$PASSDB_FILE"
    [ -z "${PDBEDIT_LIST_EXTRA_STDOUT:-}" ] \
        || printf '%s\n' "$PDBEDIT_LIST_EXTRA_STDOUT"
    exit "${PDBEDIT_LIST_EXIT_CODE:-0}"
fi
if [ "${1:-}" = "-x" ]; then
    user=""
    while [ "$#" -gt 0 ]; do
        [ "$1" = "-u" ] && { user="${2:-}"; break; }
        shift
    done
    [ -n "$user" ] || exit 2
    grep -Fvx "$user:" "$PASSDB_FILE" >/dev/null 2>&1 || true
    awk -F: -v user="$user" '$1 != user' "$PASSDB_FILE" >"$PASSDB_FILE.tmp"
    mv "$PASSDB_FILE.tmp" "$PASSDB_FILE"
    printf '%s\n' "$user" >>"$DELETE_LOG"
    exit "${PDBEDIT_DELETE_EXIT_CODE:-0}"
fi

import_path=""
for arg in "$@"; do
    case "$arg" in smbpasswd:*) import_path="${arg#smbpasswd:}" ;; esac
done
[ -n "$import_path" ] || exit 2
cp -- "$import_path" "$IMPORT_CAPTURE"
printf '%s\n' "$import_path" >"$IMPORT_PATH_LOG"
stat -c '%a' -- "$import_path" >"$IMPORT_MODE_LOG"
find "$KAIMO_SYNC_RUNTIME_DIR" -maxdepth 1 -type f -printf '%f\n' \
    | sort >"$IMPORT_RUNTIME_LOG"
[ "${PDBEDIT_EXIT_CODE:-0}" -eq 0 ] || exit "$PDBEDIT_EXIT_CODE"
while IFS=: read -r user uid _; do
    awk -F: -v user="$user" '$1 != user' "$PASSDB_FILE" >"$PASSDB_FILE.tmp"
    mv "$PASSDB_FILE.tmp" "$PASSDB_FILE"
    printf '%s:%s:Kaimo managed\n' "$user" "$uid" >>"$PASSDB_FILE"
done <"$import_path"
EOF

chmod +x "$WORK/bin"/*
export PATH="$WORK/bin:$PATH"

fail=0
note() { printf '  %s\n' "$1"; }
has_passdb_user() { awk -F: -v user="$1" '$1 == user { found=1 } END { exit !found }' "$PASSDB_FILE"; }

if ! bash "$SUT" >/dev/null 2>&1; then
    echo "FAIL: initial sync-users.sh run returned failure"
    exit 1
fi

# 1) Every desired Samba record has a distinct POSIX UID.
lines="$(grep -c ':' "$IMPORT_CAPTURE" 2>/dev/null || echo 0)"
uids="$(cut -d: -f2 "$IMPORT_CAPTURE")"
unique_uids="$(printf '%s\n' "$uids" | sort -u | grep -c .)"
if [ "$lines" -ne 3 ] || [ "$unique_uids" -ne 3 ]; then
    echo "FAIL: expected 3 imported users with distinct UIDs"; fail=1
else
    note "ok: 3 active users imported with distinct POSIX UIDs"
fi
for user in admin marco.hanisch anna.weber; do
    if ! has_passdb_user "$user"; then
        echo "FAIL: active passdb user '$user' missing"; fail=1
    fi
    for group in kaimo kaimo-authd; do
        if ! grep -Fqx "$group:$user" "$GROUP_STATE"; then
            echo "FAIL: active user '$user' missing group '$group'"; fail=1
        fi
    done
done

# 2) The stale credential and groups are removed, but its POSIX UID remains
# locked; the explicitly unmanaged test credential is untouched.
if has_passdb_user stale.user \
    || ! has_passdb_user kaimotest \
    || grep -Fq ':stale.user' "$GROUP_STATE" \
    || ! grep -Fqx 'stale.user:1401' "$PASSWD_FILE" \
    || ! grep -Fqx 'stale.user' "$LOCK_LOG"; then
    echo "FAIL: stale-user revocation or safe POSIX retention is incorrect"; fail=1
else
    note "ok: stale credential/groups revoked while UID 1401 is retained and locked"
fi
if ! grep -Fqx '1 managed user(s) revoked' "$SESSION_REVOKE_LOG"; then
    echo "FAIL: stale-user revocation did not terminate active Samba sessions"; fail=1
else
    note "ok: user revocation terminates active Samba sessions"
fi

# 3) The new managed-user boundary is private and contains exactly the desired
# identities. A legacy stderr diagnostic previously persisted as a username is
# ignored and healed. Runtime secrets/logs are cleaned after the run.
expected_managed="$WORK/expected-managed"
printf 'admin\nanna.weber\nmarco.hanisch\n' >"$expected_managed"
import_path="$(cat "$IMPORT_PATH_LOG" 2>/dev/null)"
if ! cmp -s "$expected_managed" "$KAIMO_SYNC_STATE_DIR/managed-users" \
    || [ "$(stat -c '%a' "$KAIMO_SYNC_STATE_DIR")" != "700" ] \
    || [ "$(stat -c '%a' "$KAIMO_SYNC_STATE_DIR/managed-users")" != "600" ]; then
    echo "FAIL: managed-user state is not exact/private"; fail=1
elif [ "$(cat "$IMPORT_MODE_LOG" 2>/dev/null)" != "600" ] \
    || grep -Eq '^(auth-users|user-records)\.' "$IMPORT_RUNTIME_LOG" \
    || [ -e "$import_path" ] \
    || find "$KAIMO_SYNC_RUNTIME_DIR" -type f ! -name 'sync-users.lock' | grep -q .; then
    echo "FAIL: private per-run files were not cleaned"; fail=1
else
    note "ok: credential staging is minimized and private files are cleaned"
fi

# 4) A failed active-session close must not publish a completed managed-user
# boundary. The retry still owns the stale identity and repeats revocation.
printf 'revoker.fail:1499\n' >>"$PASSWD_FILE"
printf 'revoker.fail:1499:Revoker failure user\n' >>"$PASSDB_FILE"
printf 'kaimo:revoker.fail\nkaimo-authd:revoker.fail\n' >>"$GROUP_STATE"
printf 'revoker.fail\n' >>"$KAIMO_SYNC_STATE_DIR/managed-users"
sort -u -o "$KAIMO_SYNC_STATE_DIR/managed-users" "$KAIMO_SYNC_STATE_DIR/managed-users"
export SESSION_REVOKE_EXIT_CODE=9
bash "$SUT" >/dev/null 2>&1
revoker_failure_rc=$?
unset SESSION_REVOKE_EXIT_CODE
if [ "$revoker_failure_rc" -eq 0 ] \
    || ! grep -Fqx 'revoker.fail' "$KAIMO_SYNC_STATE_DIR/managed-users"; then
    echo "FAIL: session-close failure published completed user state"; fail=1
elif ! bash "$SUT" >/dev/null 2>&1 \
    || grep -Fqx 'revoker.fail' "$KAIMO_SYNC_STATE_DIR/managed-users"; then
    echo "FAIL: retry did not repeat and complete user-session revocation"; fail=1
else
    note "ok: session-close failure preserves retry ownership"
fi

# 5) A held lock rejects another run before exporting hashes.
before_calls="$(wc -l <"$AUTH_CALL_LOG")"
(
    exec 8>"$KAIMO_SYNC_RUNTIME_DIR/sync-users.lock"
    flock 8
    bash "$SUT" >/dev/null 2>&1
    printf '%s\n' "$?" >"$WORK/locked-exit"
)
after_calls="$(wc -l <"$AUTH_CALL_LOG")"
if [ "$(cat "$WORK/locked-exit")" -eq 0 ] || [ "$before_calls" != "$after_calls" ]; then
    echo "FAIL: concurrent sync was not rejected before export"; fail=1
else
    note "ok: synchronization lock rejects concurrent reconciliation"
fi

# 6) Failed import returns failure, cleans files, and does not publish a new
# managed state (P1-16 must not forget stale identities on a partial run).
cp "$KAIMO_SYNC_STATE_DIR/managed-users" "$WORK/state-before-failure"
export PDBEDIT_EXIT_CODE=7
bash "$SUT" >/dev/null 2>&1
failed_rc=$?
unset PDBEDIT_EXIT_CODE
failed_import_path="$(cat "$IMPORT_PATH_LOG" 2>/dev/null)"
if [ "$failed_rc" -eq 0 ] \
    || ! cmp -s "$WORK/state-before-failure" "$KAIMO_SYNC_STATE_DIR/managed-users" \
    || [ -e "$failed_import_path" ] \
    || find "$KAIMO_SYNC_RUNTIME_DIR" -type f ! -name 'sync-users.lock' | grep -q .; then
    echo "FAIL: failed import changed state or left private files"; fail=1
else
    note "ok: failed import preserves retry state and leaves no hash residue"
fi

# 7) A passdb deletion followed by a reported failure keeps the old ownership
# boundary. Retry must finish group/lock convergence even though passdb no
# longer contains that user.
printf 'partial.user:1551\n' >>"$PASSWD_FILE"
printf 'partial.user:1551:Partial user\n' >>"$PASSDB_FILE"
printf 'kaimo:partial.user\nkaimo-authd:partial.user\n' >>"$GROUP_STATE"
printf 'partial.user\n' >>"$KAIMO_SYNC_STATE_DIR/managed-users"
sort -u -o "$KAIMO_SYNC_STATE_DIR/managed-users" "$KAIMO_SYNC_STATE_DIR/managed-users"
export PDBEDIT_DELETE_EXIT_CODE=9
bash "$SUT" >/dev/null 2>&1
partial_rc=$?
unset PDBEDIT_DELETE_EXIT_CODE
if [ "$partial_rc" -eq 0 ] \
    || ! grep -Fqx 'partial.user' "$KAIMO_SYNC_STATE_DIR/managed-users" \
    || has_passdb_user partial.user; then
    echo "FAIL: partial delete did not preserve retry ownership"; fail=1
elif ! bash "$SUT" >/dev/null 2>&1 \
    || grep -Fq ':partial.user' "$GROUP_STATE" \
    || ! grep -Fqx 'partial.user' "$LOCK_LOG" \
    || grep -Fqx 'partial.user' "$KAIMO_SYNC_STATE_DIR/managed-users"; then
    echo "FAIL: retry did not finish partial stale-user convergence"; fail=1
else
    note "ok: partial passdb deletion remains owned and converges on retry"
fi

# 8) Reactivation restores passdb/groups while preserving the retained UID.
jq '.users += [{"username":"stale.user","nt_hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]' \
    "$AUTH_USERS_FILE" >"$AUTH_USERS_FILE.tmp"
mv "$AUTH_USERS_FILE.tmp" "$AUTH_USERS_FILE"
if ! bash "$SUT" >/dev/null 2>&1 \
    || ! has_passdb_user stale.user \
    || ! grep -Fqx 'stale.user:1401' "$PASSWD_FILE" \
    || ! grep -Fqx 'kaimo:stale.user' "$GROUP_STATE" \
    || ! grep -Fqx 'kaimo-authd:stale.user' "$GROUP_STATE"; then
    echo "FAIL: reactivated user did not recover with retained UID"; fail=1
else
    note "ok: reactivation restores access with the original retained UID"
fi

# 9) First-run bootstrap adopts existing passdb identities, excludes reserved
# users, and revokes an absent legacy Kaimo credential.
printf 'legacy.user:1601\n' >>"$PASSWD_FILE"
printf 'legacy.user:1601:Legacy user\n' >>"$PASSDB_FILE"
printf 'kaimo:legacy.user\nkaimo-authd:legacy.user\n' >>"$GROUP_STATE"
rm "$KAIMO_SYNC_STATE_DIR/managed-users"
if ! bash "$SUT" >/dev/null 2>&1 \
    || has_passdb_user legacy.user \
    || ! has_passdb_user kaimotest \
    || ! grep -Fqx 'legacy.user:1601' "$PASSWD_FILE"; then
    echo "FAIL: bootstrap ownership did not safely reconcile legacy users"; fail=1
else
    note "ok: bootstrap adopts legacy passdb users and excludes reserved accounts"
fi

# 10) Structured records fail before any local mutation when their schema,
# hash, identity, or uniqueness is unsafe.
cp "$AUTH_USERS_FILE" "$WORK/auth-users-valid"
cp "$PASSDB_FILE" "$WORK/passdb-before-invalid"
for invalid_case in \
    '{"version":1,"users":[{"username":"bad\tname","nt_hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]}' \
    '{"version":1,"users":[{"username":"root","nt_hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]}' \
    '{"version":1,"users":[{"username":"valid","nt_hash":"SHORT"}]}' \
    '{"version":1,"users":[{"username":"Duplicate","nt_hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},{"username":"duplicate","nt_hash":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"}]}' \
    '{"version":2,"users":[]}' \
    '{"version":1,"users":[]} {"version":1,"users":[]}'; do
    printf '%s\n' "$invalid_case" >"$AUTH_USERS_FILE"
    if bash "$SUT" >/dev/null 2>&1 \
        || ! cmp -s "$WORK/passdb-before-invalid" "$PASSDB_FILE"; then
        echo "FAIL: invalid structured user response mutated state"; fail=1
        break
    fi
done
export KAIMO_USER_SYNC_MAX_JSON_BYTES=invalid
if bash "$SUT" >/dev/null 2>&1 \
    || ! cmp -s "$WORK/passdb-before-invalid" "$PASSDB_FILE"; then
    echo "FAIL: invalid user JSON limit mutated state"; fail=1
fi
unset KAIMO_USER_SYNC_MAX_JSON_BYTES
cp "$WORK/auth-users-valid" "$AUTH_USERS_FILE"
if [ "$fail" -eq 0 ]; then
    note "ok: invalid structured user records fail before passdb mutation"
fi

# 11) Unexpected stdout from `pdbedit -L` is rejected as malformed machine
# data. Diagnostics on stderr are allowed and already covered by every run.
cp "$PASSDB_FILE" "$WORK/passdb-before-malformed-list"
cp "$KAIMO_SYNC_STATE_DIR/managed-users" "$WORK/state-before-malformed-list"
export PDBEDIT_LIST_EXTRA_STDOUT='unexpected diagnostic without fields'
if bash "$SUT" >/dev/null 2>&1 \
    || ! cmp -s "$WORK/passdb-before-malformed-list" "$PASSDB_FILE" \
    || ! cmp -s "$WORK/state-before-malformed-list" \
        "$KAIMO_SYNC_STATE_DIR/managed-users"; then
    echo "FAIL: malformed pdbedit list output was accepted or mutated state"; fail=1
else
    note "ok: malformed pdbedit data output fails before local mutation"
fi
unset PDBEDIT_LIST_EXTRA_STDOUT

if [ "$fail" -eq 0 ]; then
    echo "PASS: sync-users.sh securely reconciles Kaimo users to desired state."
    exit 0
fi
echo "FAILED."
exit 1
