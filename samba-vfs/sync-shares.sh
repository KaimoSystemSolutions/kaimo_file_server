#!/bin/bash
# Phase 4 - Share-Provisioning: spiegelt die aktivierten Kaimo-Shares live in
# Sambas Registry (net conf). smbd liest die Registry ohne Neustart -> Shares
# erscheinen/verschwinden sofort. Ersetzt den FileSystemWatcher/SyncFromDb-
# Mechanismus aus src/Kaimo_File_Server.Smb/SmbServer.cs. Idempotent.
#
# Sichtbarkeit (ABE): NUR das Hidden-Flag. IsShareHidden -> browseable = no
# (der Share bleibt per \\host\share direkt erreichbar). Der harte Zugriff wird
# weiter vom VFS-connect-Hook nach echten Kaimo-ACLs entschieden.
#
# Exit 0 nur bei erfolgreichem Abruf von der Bridge (fuer den Retry-Loop im Entrypoint).
set -uo pipefail
export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH

OUT="$(kaimo_sharesync 2>>/tmp/sharesync.err)"
rc=$?
if [ $rc -ne 0 ]; then
    echo "[sync-shares] Bridge nicht erreichbar (rc=$rc) - siehe /tmp/sharesync.err"
    exit 1
fi

# --- Soll-Zustand von der Bridge einlesen (name -> path / hidden) ---
# Mit leerem `=()`-Initialisierer, damit die Maps auch OHNE Elemente als "gesetzt"
# gelten -> Referenzen wie ${want_path[k]+x} / "${!want_path[@]}" brechen sonst
# unter `set -u` mit "unbound variable" ab (Fall: keine/alle Shares deaktiviert).
declare -A want_path=()
declare -A want_hidden=()
while IFS=$'\t' read -r name path hidden; do
    [ -z "${name:-}" ] && continue
    want_path["$name"]="$path"
    want_hidden["$name"]="${hidden:-0}"
done <<< "$OUT"

# --- Ist-Zustand: aktuell in der Registry vorhandene Shares (eine pro Zeile) ---
mapfile -t current < <(net conf listshares 2>/dev/null | sed '/^[[:space:]]*$/d')

# 1) Nicht mehr gewuenschte Registry-Shares entfernen. Die Registry wird
#    ausschliesslich von diesem Sync verwaltet -> alles, was nicht im Soll steht,
#    ist ein geloeschter/deaktivierter Kaimo-Share. 'global' nie anfassen.
# (Element-Zaehlung ${#arr[@]} ist bei leerem Array gefahrlos 0; die Key-/Wert-
#  Expansion "${arr[@]}" einer leeren, nur `declare -A`-ten Map wuerde dagegen
#  unter `set -u` als "unbound variable" abbrechen -> Schleifen darum gaten.)
removed=0
if (( ${#current[@]} > 0 )); then
    for name in "${current[@]}"; do
        [ -z "${name:-}" ] && continue
        [ "$name" = "global" ] && continue
        if [ -z "${want_path[$name]+x}" ]; then
            if net conf delshare "$name" 2>/dev/null; then
                echo "[sync-shares] entfernt: $name"
                removed=$((removed + 1))
            fi
        fi
    done
fi

# 2) Gewuenschte Shares anlegen bzw. auf den Soll-Zustand ziehen (idempotent).
added=0; updated=0
if (( ${#want_path[@]} > 0 )); then
    for name in "${!want_path[@]}"; do
        [ -z "${name:-}" ] && continue
        path="${want_path[$name]}"
        hidden="${want_hidden[$name]}"
        [ "$hidden" = "1" ] && browseable="no" || browseable="yes"

        # Samba validiert bei addshare, dass das Zielverzeichnis existiert.
        mkdir -p "$path"
        # Neues Share-Verzeichnis der gemeinsamen Storage-Gruppe geben + setgid + g+w,
        # damit SMB-User (Gruppe kaimo) und Web (uid $KAIMO_STORAGE_GID) darin schreiben
        # koennen und neue Dateien die Gruppe erben. Ergaenzt die create/directory-masks
        # in smb.conf.vfs. Idempotent (bei jedem Sync erzwungen).
        chgrp "${KAIMO_STORAGE_GID:-1654}" "$path" 2>/dev/null || true
        chmod 2775 "$path" 2>/dev/null || true

        # Anlegen, falls noch nicht vorhanden ...
        if net conf showshare "$name" >/dev/null 2>&1; then
            updated=$((updated + 1))
        else
            net conf addshare "$name" "$path" writeable=y guest_ok=n "Kaimo Share" >/dev/null 2>&1
            echo "[sync-shares] angelegt: $name -> $path (browseable=$browseable)"
            added=$((added + 1))
        fi

        # ... und in JEDEM Fall (neu wie bestehend) den Soll-Zustand mit den
        # KANONISCHEN Samba-Parametern erzwingen. Wichtig: `read only = no` statt
        # des Synonyms `writeable` — Sambas Default ist `read only = yes`, sonst
        # sind Shares nur lesbar (Lesen ueber SMB geht, Schreiben scheitert auf
        # der Samba-Ebene, noch vor der ACL). Der harte Zugriff bleibt beim
        # VFS-connect/create_file-Hook; hier nur die Share-Grunddisposition.
        net conf setparm "$name" path         "$path"       >/dev/null 2>&1
        net conf setparm "$name" "read only"  no            >/dev/null 2>&1
        net conf setparm "$name" browseable   "$browseable" >/dev/null 2>&1
        net conf setparm "$name" "guest ok"   no            >/dev/null 2>&1
    done
fi

echo "[sync-shares] fertig: ${#want_path[@]} Soll-Shares (${added} neu, ${updated} aktualisiert, ${removed} entfernt)."
exit 0
