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

# Kerberos (0025): the keytab comes from AD (tools/ad/INSTRUKCJA-KERBEROS.md),
# never from here. An empty file satisfies the compose secret and means "no
# Windows sign-in" - the server says so instead of answering 401.
write blinkylite-http.keytab               ""

# Two containers, two users, and a bind-mounted secret keeps the host's owner:
#   - the server runs as uid 1654 (the .NET image's "app"),
#   - PostgreSQL runs its init scripts as uid 999 ("postgres").
# The database passwords are read by both, so they are owned by postgres and
# readable by the server's group. Everything else belongs to the server alone.
# Getting this wrong is not loud: the first deployment set empty passwords and
# said it had set them.
if [ "$(id -u)" = "0" ]; then
    for name in postgres-superuser-password blinkylite-db-owner-password                 blinkylite-db-app-password blinkylite-db-readonly-password; do
        chown 999:1654 "secrets/$name"
        chmod 640 "secrets/$name"
    done
    for name in blinkylite-jwt-signing-key blinkylite-kek-1 blinkylite-ldap-service-password blinkylite-http.keytab; do
        chown 1654:1654 "secrets/$name"
        chmod 600 "secrets/$name"
    done
    echo "owner    database passwords -> 999:1654 (postgres reads, server reads via group)"
    echo "owner    other secrets      -> 1654:1654"
else
    echo
    echo "Not running as root: fix the owners before starting the stack, or the"
    echo "database will be set up with empty passwords:"
    echo "    sudo ./scripts/dev-secrets.sh"
fi

cat <<'EOF'

The LDAP service account password is a placeholder: put the real one in
secrets/blinkylite-ldap-service-password.

Back up secrets/blinkylite-kek-1 somewhere else, today. A database without its
KEK is a database without PUKs (docs/04-security.md).
EOF
