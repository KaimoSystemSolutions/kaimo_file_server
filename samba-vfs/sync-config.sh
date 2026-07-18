#!/bin/bash
# Phase 4 - Protokoll-Settings: spiegelt die SMB-Protokoll/Sicherheits-Optionen
# (Dialekt-Range, Signing, Encryption) aus der Kaimo-DB (ISmbConfigStore, per
# Web-UI gepflegt) in Sambas globale Registry-Config (net conf setparm global).
# Entspricht dem, was SmbServer.LoadProtocolSettings() beim (Re)Start in den
# alten .NET-SMB-Server einspeiste.
#
# smbd uebernimmt geaenderte Globals nach `smbcontrol smbd reload-config` (nur
# neue Verbindungen; bestehende bleiben). Idempotent; reload NUR bei tatsaechlicher
# Aenderung und nur wenn smbd laeuft (beim initialen Sync vor dem smbd-Start liest
# smbd die Registry ohnehin frisch beim Start).
#
# Exit 0 nur bei erfolgreichem Abruf von der Bridge (fuer den Retry-Loop im Entrypoint).
set -uo pipefail
export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

OUT="$(kaimo_configsync 2>>/tmp/configsync.err)"
rc=$?
if [ $rc -ne 0 ]; then
    echo "[sync-config] Bridge nicht erreichbar (rc=$rc) - siehe /tmp/configsync.err"
    exit 1
fi

IFS=$'\t' read -r min_proto max_proto req_sign req_enc <<< "$OUT"
if [ -z "${min_proto:-}" ] || [ -z "${max_proto:-}" ]; then
    echo "[sync-config] leere/unvollstaendige Antwort, uebersprungen."
    exit 0
fi

# Bool -> Samba-Semantik.
[ "${req_sign:-0}" = "1" ] && signing="mandatory" || signing="auto"
[ "${req_enc:-0}"  = "1" ] && encrypt="required"  || encrypt="default"

changed=0
# apply <param> <value>: setzt einen globalen Registry-Parameter nur, wenn er sich
# vom Ist unterscheidet (net conf getparm gibt bei unset einen Fehler -> curr leer).
apply() {
    local param="$1" value="$2" curr
    curr="$(net conf getparm global "$param" 2>/dev/null)"
    if [ "${curr:-}" != "$value" ]; then
        if net conf setparm global "$param" "$value" >/dev/null 2>&1; then
            echo "[sync-config] $param: '${curr:-<unset>}' -> '$value'"
            changed=1
        fi
    fi
}

apply "server min protocol" "$min_proto"
apply "server max protocol" "$max_proto"
apply "server signing"      "$signing"
apply "smb encrypt"         "$encrypt"

if [ "$changed" = "1" ]; then
    if pidof smbd >/dev/null 2>&1; then
        if smbcontrol smbd reload-config >/dev/null 2>&1; then
            echo "[sync-config] smbd reload-config ausgeloest."
        else
            echo "[sync-config] reload-config fehlgeschlagen (smbd nicht bereit?)."
        fi
    else
        echo "[sync-config] smbd laeuft noch nicht - Registry wird beim Start gelesen."
    fi
else
    echo "[sync-config] keine Aenderung."
fi
exit 0
