#!/bin/sh
# Generates a dedicated local CA, one server identity, and least-privilege
# client identities for the Samba control plane. Private keys are never
# committed; production may point KAIMO_SMB_CONTROL_PKI at externally managed
# certificates with the same subjects and EKUs.
set -eu

output="${1:-./secrets/smb-control-plane}"
umask 077
mkdir -p "$output"
chmod 700 "$output"

for required in openssl; do
    command -v "$required" >/dev/null 2>&1 || {
        echo "missing required command: $required" >&2
        exit 1
    }
done

if [ -e "$output/ca.key" ]; then
    echo "refusing to overwrite existing PKI in $output" >&2
    exit 1
fi

openssl req -x509 -newkey rsa:3072 -sha256 -nodes \
    -days 3650 \
    -subj "/CN=Kaimo SMB Control Plane CA" \
    -keyout "$output/ca.key" \
    -out "$output/ca.crt"

issue_certificate() {
    name="$1"
    common_name="$2"
    extended_key_usage="$3"
    subject_alt_name="${4:-}"

    openssl req -new -newkey rsa:3072 -sha256 -nodes \
        -subj "/CN=$common_name" \
        -keyout "$output/$name.key" \
        -out "$output/$name.csr"

    {
        echo "basicConstraints=critical,CA:FALSE"
        echo "keyUsage=critical,digitalSignature,keyEncipherment"
        echo "extendedKeyUsage=$extended_key_usage"
        [ -z "$subject_alt_name" ] || echo "subjectAltName=$subject_alt_name"
    } > "$output/$name.ext"

    openssl x509 -req -sha256 -days 825 \
        -in "$output/$name.csr" \
        -CA "$output/ca.crt" \
        -CAkey "$output/ca.key" \
        -CAcreateserial \
        -extfile "$output/$name.ext" \
        -out "$output/$name.crt"

    rm -f "$output/$name.csr" "$output/$name.ext"
}

issue_certificate server kaimo_smb_bridge serverAuth DNS:kaimo_smb_bridge
issue_certificate auth-sync kaimo-auth-sync clientAuth
issue_certificate share-sync kaimo-share-sync clientAuth
issue_certificate config-sync kaimo-config-sync clientAuth
issue_certificate runtime kaimo-samba-runtime clientAuth

# The output directory itself is 0700. Leaf keys are 0644 so an unprivileged
# process inside the narrowly scoped read-only container mount can read its own
# key even when the host UID differs from the container UID. The CA key is never
# mounted and remains owner-only.
chmod 600 "$output/ca.key"
chmod 644 "$output/server.key" "$output/auth-sync.key" \
    "$output/share-sync.key" "$output/config-sync.key" "$output/runtime.key"
chmod 644 "$output"/*.crt
echo "SMB control-plane PKI generated in $output"
