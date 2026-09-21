#!/bin/sh
set -e

# LDAPS is verified against the operating system's trust store, and a corporate
# CA is not in it. Mount the CA at /certs/ca and OpenLDAP will use it - the
# container runs unprivileged, so update-ca-certificates is not an option and
# LDAPTLS_CACERT is.
if [ -f /certs/ca/ldap-ca.crt ]; then
    export LDAPTLS_CACERT=/certs/ca/ldap-ca.crt
fi

exec dotnet /app/BlinkyLite.Server.dll "$@"
