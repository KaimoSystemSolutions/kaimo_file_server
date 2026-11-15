#!/bin/sh
# Idempotently bootstraps a dedicated local CA, one bridge server identity, and
# one shared Samba workload identity. The output is split so the bridge never
# sees client/CA private keys and Samba never sees server/CA private keys.
# Production may pre-populate the same layout with externally managed PKI.
set -eu

output="${1:-./data/smb-control-plane}"
umask 077
mkdir -p "$output"
bridge_output="$output/bridge"
samba_output="$output/samba"
authority_output="$output/authority"
mkdir -p "$bridge_output" "$samba_output" "$authority_output"

for required in openssl; do
    command -v "$required" >/dev/null 2>&1 || {
        echo "missing required command: $required" >&2
        exit 1
    }
done

ca_subject="/CN=Kaimo SMB Control Plane CA"
ca_days=3650
leaf_days=825
# Every start renews a leaf that expires within this many days, and the CA once
# it expires within one leaf lifetime. Renewal keeps the keys, so a bridge or
# Samba process still holding the old certificate or CA keeps working with the
# renewed files until it restarts.
# ponytail: renewal runs only at Host start and the bridge/Samba load the files
# once per process, so a stack running a whole year without any restart still
# expires; upgrade path: periodic re-run plus hot reload (Kestrel
# ServerCertificateSelector, gRPC FileWatcherCertificateProvider).
renew_days=365

issue_certificate() {
    name="$1"
    common_name="$2"
    extended_key_usage="$3"
    subject_alt_name="${4:-}"
    destination="$5"

    if [ -s "$destination/$name.key" ]; then
        # Renewal: keep the key so swapping the certificate is the only change.
        openssl req -new -sha256 -key "$destination/$name.key" \
            -subj "/CN=$common_name" \
            -out "$authority_output/$name.csr"
    else
        openssl req -new -newkey rsa:3072 -sha256 -nodes \
            -subj "/CN=$common_name" \
            -keyout "$destination/$name.key" \
            -out "$authority_output/$name.csr"
    fi

    {
        echo "basicConstraints=critical,CA:FALSE"
        echo "keyUsage=critical,digitalSignature,keyEncipherment"
        echo "extendedKeyUsage=$extended_key_usage"
        [ -z "$subject_alt_name" ] || echo "subjectAltName=$subject_alt_name"
    } > "$authority_output/$name.ext"

    openssl x509 -req -sha256 -days "$leaf_days" \
        -in "$authority_output/$name.csr" \
        -CA "$authority_output/ca.crt" \
        -CAkey "$authority_output/ca.key" \
        -CAcreateserial \
        -extfile "$authority_output/$name.ext" \
        -out "$destination/$name.crt.new"
    mv -f "$destination/$name.crt.new" "$destination/$name.crt"

    rm -f "$authority_output/$name.csr" "$authority_output/$name.ext"
}

issue_bridge_certificate() {
    issue_certificate server kaimo_smb_bridge serverAuth DNS:kaimo_smb_bridge "$bridge_output"
}

issue_samba_certificate() {
    issue_certificate samba kaimo-samba clientAuth "" "$samba_output"
}

# Atomic per file: a consumer never reads a half-written CA.
publish_ca() {
    for publish_dir in "$bridge_output" "$samba_output"; do
        cp "$authority_output/ca.crt" "$publish_dir/ca.crt.new"
        mv -f "$publish_dir/ca.crt.new" "$publish_dir/ca.crt"
    done
}

# True when certificate $1 is unreadable or expires within $2 days.
expires_within() {
    ! openssl x509 -checkend "$(($2 * 86400))" -noout -in "$1" >/dev/null 2>&1
}

renew_expiring() {
    # Externally managed PKI, or one the former root-run pki-init created and
    # this UID cannot read.
    if [ ! -r "$authority_output/ca.key" ] || [ ! -s "$authority_output/ca.key" ]; then
        for renew_cert in "$bridge_output/ca.crt" "$bridge_output/server.crt" "$samba_output/samba.crt"; do
            if expires_within "$renew_cert" "$renew_days"; then
                echo "warning: $renew_cert expires within $renew_days days and cannot be renewed here: no readable CA key in $authority_output" >&2
            fi
        done
        return 0
    fi

    renewed=""
    if expires_within "$authority_output/ca.crt" "$leaf_days"; then
        # Same key and subject: certificates issued before still chain to it.
        openssl req -x509 -new -sha256 -days "$ca_days" \
            -key "$authority_output/ca.key" \
            -subj "$ca_subject" \
            -out "$authority_output/ca.crt.new"
        mv -f "$authority_output/ca.crt.new" "$authority_output/ca.crt"
        renewed="$renewed CA"
    fi
    # Also repairs copies left stale by a run stopped before publishing.
    if ! cmp -s "$authority_output/ca.crt" "$bridge_output/ca.crt" \
        || ! cmp -s "$authority_output/ca.crt" "$samba_output/ca.crt"; then
        publish_ca
    fi
    if expires_within "$bridge_output/server.crt" "$renew_days"; then
        issue_bridge_certificate
        renewed="$renewed bridge"
    fi
    if expires_within "$samba_output/samba.crt" "$renew_days"; then
        issue_samba_certificate
        renewed="$renewed samba"
    fi
    if [ -n "$renewed" ]; then
        echo "SMB control-plane certificates renewed:$renewed; restart kaimo_smb_bridge and kaimo_samba to load them"
    fi
}

required_files="
$bridge_output/ca.crt
$bridge_output/server.crt
$bridge_output/server.key
$samba_output/ca.crt
$samba_output/samba.crt
$samba_output/samba.key
"

# Leaf keys are readable only by their consumer: Samba's control-plane clients
# (authd and the *sync helpers) run as root; the bridge runs as the .NET app UID.
# Applied on every run so installations created with the former world-readable
# mode (0644) are tightened too. If the bridge key cannot be handed to its UID
# (script not run as root), fall back to the previous mode rather than breaking
# the bridge, and say so.
# The Host runs this script as the .NET app UID; a PKI created earlier by the
# former root-run pki-init container stays root-owned and was already tightened
# then, so files this UID does not own are left as they are.
restrict() {
    restrict_mode="$1"
    shift
    for restrict_path in "$@"; do
        # Missing: an externally managed PKI may come without the authority.
        [ -e "$restrict_path" ] || continue
        if [ "$(id -u)" -eq 0 ] || [ -O "$restrict_path" ]; then
            chmod "$restrict_mode" "$restrict_path"
        fi
    done
}

restrict_leaf_keys() {
    restrict 700 "$authority_output"
    restrict 755 "$bridge_output" "$samba_output"
    restrict 600 "$authority_output/ca.key"
    restrict 644 "$bridge_output"/*.crt "$samba_output"/*.crt
    restrict 600 "$samba_output/samba.key"
    if [ "$(id -u)" -ne 0 ] && [ ! -O "$bridge_output/server.key" ]; then
        : # owned by another UID: a root run already handed it over
    elif chown "${KAIMO_BRIDGE_UID:-1654}" "$bridge_output/server.key" 2>/dev/null; then
        chmod 600 "$bridge_output/server.key"
    else
        chmod 644 "$bridge_output/server.key"
        echo "warning: could not assign $bridge_output/server.key to UID ${KAIMO_BRIDGE_UID:-1654}; left world-readable" >&2
    fi
}

missing=0
present=0
for path in $required_files; do
    if [ -f "$path" ] && [ -s "$path" ]; then
        present=$((present + 1))
    else
        missing=$((missing + 1))
    fi
done

# Present only while a fresh generation runs. The Host can be stopped or time
# out mid-run; the next run then starts over instead of refusing the incomplete
# PKI forever. Nothing can trust a PKI whose generation never finished.
generating="$authority_output/.generating"

if [ "$missing" -eq 0 ]; then
    # Stopped after the last file but before the marker was removed: complete.
    [ ! -e "$generating" ] || rm -f "$generating"
    renew_expiring
    restrict_leaf_keys
    echo "SMB control-plane PKI already present in $output"
    exit 0
fi

if [ -e "$generating" ]; then
    echo "previous PKI generation in $output was interrupted; starting over" >&2
    rm -f "$authority_output"/ca.* "$authority_output"/*.csr "$authority_output"/*.ext \
        "$bridge_output/ca.crt" "$bridge_output"/server.* \
        "$samba_output/ca.crt" "$samba_output"/samba.*
    present=0
fi

# Migrate PKI produced by the former four-client layout without rotating the
# bridge identity or CA. The old client keys are removed after replacement.
if [ -f "$bridge_output/ca.crt" ] && [ -s "$bridge_output/ca.crt" ] \
    && [ -f "$bridge_output/server.crt" ] && [ -s "$bridge_output/server.crt" ] \
    && [ -f "$bridge_output/server.key" ] && [ -s "$bridge_output/server.key" ] \
    && [ -f "$authority_output/ca.crt" ] && [ -s "$authority_output/ca.crt" ] \
    && [ -f "$authority_output/ca.key" ] && [ -s "$authority_output/ca.key" ] \
    && [ ! -e "$samba_output/samba.crt" ] \
    && [ ! -e "$samba_output/samba.key" ]; then
    issue_samba_certificate
    cp "$authority_output/ca.crt" "$samba_output/ca.crt"
else
    if [ "$present" -ne 0 ]; then
        echo "refusing to overwrite incomplete PKI in $output" >&2
        exit 1
    fi

    : > "$generating"
    openssl req -x509 -newkey rsa:3072 -sha256 -nodes \
        -days "$ca_days" \
        -subj "$ca_subject" \
        -keyout "$authority_output/ca.key" \
        -out "$authority_output/ca.crt"

    issue_bridge_certificate
    issue_samba_certificate

    cp "$authority_output/ca.crt" "$bridge_output/ca.crt"
    cp "$authority_output/ca.crt" "$samba_output/ca.crt"
    rm -f "$generating"
fi

# Remove obsolete credentials from the old layout after successful migration.
rm -f \
    "$samba_output/auth-sync.crt" "$samba_output/auth-sync.key" \
    "$samba_output/share-sync.crt" "$samba_output/share-sync.key" \
    "$samba_output/config-sync.crt" "$samba_output/config-sync.key" \
    "$samba_output/runtime.crt" "$samba_output/runtime.key"

restrict_leaf_keys
echo "SMB control-plane PKI generated in $output"
