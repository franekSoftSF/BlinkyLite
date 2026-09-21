#!/bin/bash
# Runs once, inside the postgres container, after 10-roles.sql has created the
# three roles. The passwords come from Docker secrets, so they are never in the
# compose file, the image, or an environment variable that "docker inspect"
# would print.
set -euo pipefail

set_password() {
    role="$1"
    file="$2"

    if [ ! -r "$file" ]; then
        # This ran as postgres (uid 999) against files owned by the server's
        # user (uid 1654) and could not read them - and still said "password
        # set", because a failed command substitution is not a failed command.
        # The roles ended up with empty passwords and the server could not log
        # in. Readability is checked, and so is what came out of the file.
        echo "20-passwords.sh: cannot read $file (running as $(id -un), file is $(stat -c '%U:%G %a' "$file" 2>/dev/null || echo missing))." >&2
        echo "20-passwords.sh: the database password files must be readable by this user - see scripts/dev-secrets.sh." >&2
        exit 1
    fi

    password="$(cat "$file")"
    if [ -z "$password" ]; then
        echo "20-passwords.sh: $file is empty; refusing to set an empty password for $role." >&2
        exit 1
    fi

    # The statement goes in on standard input, not with -c: psql expands :vars
    # in input, never in -c, and the first version of this script set no
    # password at all while looking like it worked. Variables rather than
    # string interpolation, because a password may contain a quote.
    psql -v ON_ERROR_STOP=1 \
        --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
        -v role="$role" -v password="$password" <<'SQL'
ALTER ROLE :"role" PASSWORD :'password';
SQL

    echo "20-passwords.sh: password set for $role"
}

set_password blinkylite_owner    /run/secrets/blinkylite-db-owner-password
set_password blinkylite_app      /run/secrets/blinkylite-db-app-password
set_password blinkylite_readonly /run/secrets/blinkylite-db-readonly-password
