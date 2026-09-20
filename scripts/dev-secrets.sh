#!/usr/bin/env bash
# Random secrets for a BlinkyLite compose stack, one file per secret in
# ./secrets. Existing files are left alone: regenerating the KEK would make
# every PUK in the database unreadable.
#
#   ./scripts/dev-secrets.sh
set -euo pipefail

cd "$(dirname "$0")/.."
mkdir -p secrets
chmod 700 secrets

# 32 bytes, base64: what the JWT signing key and every KEK must be.
random_key() {
    openssl rand -base64 32
}

# A password that survives being pasted into psql and a connection string.
random_password() {
    openssl rand -base64 24 | tr -d '/+=' | cut -c1-24
}

write() {
    name="$1"
    value="$2"
    path="secrets/$name"

    if [ -f "$path" ]; then
        echo "keeping  $path"
        return
    fi

    printf '%s' "$value" > "$path"
    chmod 600 "$path"
    echo "wrote    $path"
}

write postgres-superuser-password          "$(random_password)"
write blinkylite-db-owner-password         "$(random_password)"
write blinkylite-db-app-password           "$(random_password)"
write blinkylite-db-readonly-password      "$(random_password)"
write blinkylite-ldap-service-password     "${BLINKYLITE_LDAP_SERVICE_PASSWORD:-change-me}"
write blinkylite-jwt-signing-key           "$(random_key)"
write blinkylite-kek-1                     "$(random_key)"

# The server runs unprivileged (uid 1654, from the .NET image). A bind-mounted
# secret keeps its owner from the host, so root-owned 0600 files would be
# unreadable inside the container - which looks like a missing secret.
if [ "$(id -u)" = "0" ]; then
    chown -R 1654:1654 secrets
    echo "owner    secrets/* -> uid 1654 (the container's user)"
else
    echo
    echo "Not running as root: if the stack reports a missing secret, run"
    echo "    sudo chown -R 1654:1654 secrets certs"
fi

cat <<'EOF'

The LDAP service account password is a placeholder: put the real one in
secrets/blinkylite-ldap-service-password.

Back up secrets/blinkylite-kek-1 somewhere else, today. A database without its
KEK is a database without PUKs (docs/04-security.md).
EOF
