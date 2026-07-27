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
: > "$PASSWD_FILE"
: > "$GROUP_LOG"
echo 1001 > "$PASSWD_FILE.cnt"     # Starting UID for auto-assignment
SMBPASSWD_OUT="/tmp/kaimo.smbpasswd"
: > "$SMBPASSWD_OUT"

mkdir -p "$WORK/bin"

# --- Stub: kaimo_authsync (3 demo users with valid 16-byte NT-Hashes hex) ---
cat > "$WORK/bin/kaimo_authsync" <<'EOF'
#!/bin/bash
printf 'admin\t%s\n'         '31D6CFE0D16AE931B73C59D7E0C089C0'
printf 'marco.hanisch\t%s\n' 'AABBCCDDEEFF00112233445566778899'
printf 'anna.weber\t%s\n'    '00112233445566778899AABBCCDDEEFF'
EOF

# --- Stub: id  (existence check + `id -u NAME` from fake passwd) ---
cat > "$WORK/bin/id" <<'EOF'
#!/bin/bash
P="$PASSWD_FILE"
if [ "${1:-}" = "-u" ]; then
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

printf '#!/bin/bash\nexit 0\n' > "$WORK/bin/pdbedit"   # import no-op
chmod +x "$WORK/bin"/*

export PATH="$WORK/bin:$PATH"

# ─────────────────────────── Run test ───────────────────────────
bash "$SUT" >/dev/null 2>&1

fail=0
note() { printf '  %s\n' "$1"; }

# 1) Exactly 3 smbpasswd lines were written.
lines="$(grep -c ':' "$SMBPASSWD_OUT" 2>/dev/null || echo 0)"
if [ "$lines" -ne 3 ]; then
    echo "FAIL: expected 3 smbpasswd lines, got $lines"; fail=1
else
    note "ok: 3 smbpasswd lines"
fi

# 2) CORE ASSERTION: all UIDs (field 2) are distinct.
uids="$(cut -d: -f2 "$SMBPASSWD_OUT")"
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
    if ! cut -d: -f1 "$SMBPASSWD_OUT" | grep -qx "$u"; then
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

if [ "$fail" = 0 ]; then
    echo "PASS: sync-users.sh preserves UID identity and socket membership."
    exit 0
else
    echo "FAILED."
    exit 1
fi
