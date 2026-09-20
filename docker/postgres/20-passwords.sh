#!/bin/bash
# Runs once, inside the postgres container, after 10-roles.sql has created the
# three roles. The passwords come from Docker secrets, so they are never in the
# compose file, the image, or an environment variable that "docker inspect"
# would print.
set -euo pipefail

set_password() {
    role="$1"
    file="$2"

    if [ ! -f "$file" ]; then
        echo "20-passwords.sh: $file is missing; run scripts/dev-secrets.sh first." >&2
        exit 1
    fi

    # The statement goes in on standard input, not with -c: psql expands :vars
    # in input, never in -c, and the first version of this script set no
    # password at all while looking like it worked. Variables rather than
    # string interpolation, because a password may contain a quote.
    psql -v ON_ERROR_STOP=1 \
        --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
        -v role="$role" -v password="$(cat "$file")" <<'SQL'
ALTER ROLE :"role" PASSWORD :'password';
SQL

    echo "20-passwords.sh: password set for $role"
}

set_password blinkylite_owner    /run/secrets/blinkylite-db-owner-password
set_password blinkylite_app      /run/secrets/blinkylite-db-app-password
set_password blinkylite_readonly /run/secrets/blinkylite-db-readonly-password
