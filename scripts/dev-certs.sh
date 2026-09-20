#!/usr/bin/env bash
# A self-signed TLS certificate for the server, so that a stack can come up
# before anybody issues a real one. Every name given becomes a subject
# alternative name; localhost and 127.0.0.1 are always included.
#
#   ./scripts/dev-certs.sh blinkylite.corp.example 10.0.0.5
#
# In production, replace certs/blinkylite.crt and certs/blinkylite.key with a
# certificate from your own CA - the clients check it like any other.
set -euo pipefail

# Git Bash on Windows rewrites an argument that looks like a path, turning
# /CN=host into C:/Program Files/Git/CN=host and making openssl fail with a
# message nobody reads. Harmless everywhere else.
export MSYS_NO_PATHCONV=1

cd "$(dirname "$0")/.."
mkdir -p certs
chmod 700 certs

if [ -f certs/blinkylite.crt ] && [ "${1:-}" != "--force" ]; then
    echo "certs/blinkylite.crt exists; pass --force to replace it." >&2
    exit 1
fi

if [ "${1:-}" = "--force" ]; then
    shift
fi

names=("$@")
alt="DNS:localhost,IP:127.0.0.1"
for name in "${names[@]}"; do
    if [[ "$name" =~ ^[0-9.]+$ ]]; then
        alt="$alt,IP:$name"
    else
        alt="$alt,DNS:$name"
    fi
done

subject="/CN=${names[0]:-localhost}"

# 397 days: what public CAs and Apple's clients accept, and a habit worth
# keeping even for a self-signed one.
openssl req -x509 -newkey rsa:3072 -sha256 -days 397 -nodes \
    -keyout certs/blinkylite.key -out certs/blinkylite.crt \
    -subj "$subject" -addext "subjectAltName=$alt" \
    -addext "extendedKeyUsage=serverAuth" >/dev/null

chmod 600 certs/blinkylite.key
chmod 644 certs/blinkylite.crt

# The server reads the key as uid 1654; a root-owned 0600 file is invisible to
# it inside the container.
if [ "$(id -u)" = "0" ]; then
    chown 1654:1654 certs/blinkylite.key certs/blinkylite.crt
fi

echo "wrote certs/blinkylite.crt and certs/blinkylite.key"
echo "names: $alt"
