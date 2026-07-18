#!/bin/bash
# Unit-Test fuer sync-users.sh — laeuft OHNE Samba/Docker gegen Stub-Binaries.
#
# Faengt die Regression ab, die einmal den SMB-Zugang lahmgelegt hat: Wenn alle
# Kaimo-User dieselbe UID bekommen (frueher "force user"/geteilte UID), kollabiert
# Sambas Per-User-Identitaet (SID aus UID abgeleitet + getpwuid-Ruecklookup) und
# Auth/Connect bricht. Kern-Assertion hier: JEDER User bekommt eine EIGENE UID.
#
# Aufruf:  bash samba-vfs/tests/test-sync-users.sh   (Exit 0 = OK, 1 = FAIL)
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Standardmaessig das echte Skript testen; per SYNC_USERS_SUT ueberschreibbar (Regressions-Demo).
SUT="${SYNC_USERS_SUT:-$HERE/../sync-users.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

export PASSWD_FILE="$WORK/passwd"
: > "$PASSWD_FILE"
echo 1001 > "$PASSWD_FILE.cnt"     # Start-UID fuer Auto-Vergabe
SMBPASSWD_OUT="/tmp/kaimo.smbpasswd"
: > "$SMBPASSWD_OUT"

mkdir -p "$WORK/bin"

# --- Stub: kaimo_authsync (3 Demo-User mit gueltigen 16-Byte-NT-Hashes hex) ---
cat > "$WORK/bin/kaimo_authsync" <<'EOF'
#!/bin/bash
printf 'admin\t%s\n'         '31D6CFE0D16AE931B73C59D7E0C089C0'
printf 'marco.hanisch\t%s\n' 'AABBCCDDEEFF00112233445566778899'
printf 'anna.weber\t%s\n'    '00112233445566778899AABBCCDDEEFF'
EOF

# --- Stub: id  (Existenz-Check + `id -u NAME` aus dem Fake-Passwd) ---
cat > "$WORK/bin/id" <<'EOF'
#!/bin/bash
P="$PASSWD_FILE"
if [ "${1:-}" = "-u" ]; then
    line="$(grep -F "$2:" "$P" 2>/dev/null | head -1)"
    [ -n "$line" ] && { printf '%s\n' "${line#*:}"; exit 0; } || exit 1
fi
grep -qF "$1:" "$P" 2>/dev/null && exit 0 || exit 1
EOF

# --- Stub: useradd  (respektiert -u N, sonst Auto-UID hochzaehlen) ---
cat > "$WORK/bin/useradd" <<'EOF'
#!/bin/bash
P="$PASSWD_FILE"; C="$PASSWD_FILE.cnt"
uid=""; name=""; args=("$@")
for ((i=0; i<${#args[@]}; i++)); do
    case "${args[i]}" in
        -u) uid="${args[i+1]}"; ((i++)) ;;
        -*) : ;;                         # Flag ohne fuer uns relevanten Wert
        *)  name="${args[i]}" ;;         # letzter Nicht-Flag-Wert = Username
    esac
done
[ -z "$name" ] && exit 1
if [ -z "$uid" ]; then n="$(cat "$C" 2>/dev/null || echo 1001)"; n=$((n+1)); echo "$n" > "$C"; uid="$n"; fi
echo "$name:$uid" >> "$P"
EOF

# --- Stub: usermod (-u N NAME -> UID aendern; sollte im gesunden Pfad NICHT gerufen werden) ---
cat > "$WORK/bin/usermod" <<'EOF'
#!/bin/bash
P="$PASSWD_FILE"; uid=""; name=""; args=("$@")
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

printf '#!/bin/bash\nexit 0\n' > "$WORK/bin/pdbedit"   # Import-No-op
chmod +x "$WORK/bin"/*

export PATH="$WORK/bin:$PATH"

# ─────────────────────────── Test ausfuehren ───────────────────────────
bash "$SUT" >/dev/null 2>&1

fail=0
note() { printf '  %s\n' "$1"; }

# 1) Es wurden genau 3 smbpasswd-Zeilen geschrieben.
lines="$(grep -c ':' "$SMBPASSWD_OUT" 2>/dev/null || echo 0)"
if [ "$lines" -ne 3 ]; then
    echo "FAIL: erwartet 3 smbpasswd-Zeilen, bekommen $lines"; fail=1
else
    note "ok: 3 smbpasswd-Zeilen"
fi

# 2) KERN-ASSERTION: alle UIDs (Feld 2) sind distinkt.
uids="$(cut -d: -f2 "$SMBPASSWD_OUT")"
total="$(printf '%s\n' "$uids" | grep -c .)"
uniq="$(printf '%s\n' "$uids" | sort -u | grep -c .)"
if [ "$total" != "$uniq" ]; then
    echo "FAIL: UIDs nicht distinkt ($total gesamt, $uniq eindeutig) -> Identitaetskollaps!"
    printf '%s\n' "$uids" | sed 's/^/    uid=/'
    fail=1
else
    note "ok: $uniq distinkte UIDs (kein Identitaetskollaps)"
fi

# 3) Jede smbpasswd-Zeile traegt den erwarteten Usernamen (Feld 1).
for u in admin marco.hanisch anna.weber; do
    if ! cut -d: -f1 "$SMBPASSWD_OUT" | grep -qx "$u"; then
        echo "FAIL: User '$u' fehlt in smbpasswd"; fail=1
    fi
done
[ "$fail" = 0 ] && note "ok: alle 3 Usernamen vorhanden"

if [ "$fail" = 0 ]; then
    echo "PASS: sync-users.sh vergibt distinkte UIDs pro User."
    exit 0
else
    echo "FAILED."
    exit 1
fi
