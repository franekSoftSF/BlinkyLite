# BlinkyLite server. Plain net10.0: it never talks to a card or to a CA, so it
# runs in a Linux container as happily as in a Windows service.
#
# Build from the repository root:
#   docker build -f docker/Dockerfile -t blinkylite-server .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, with only the files that decide what is restored, so that a
# change in the source does not re-download the world.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BlinkyLite.Contracts/BlinkyLite.Contracts.csproj src/BlinkyLite.Contracts/
COPY src/BlinkyLite.Server/BlinkyLite.Server.csproj src/BlinkyLite.Server/
RUN dotnet restore src/BlinkyLite.Server/BlinkyLite.Server.csproj

COPY src/BlinkyLite.Contracts/ src/BlinkyLite.Contracts/
COPY src/BlinkyLite.Server/ src/BlinkyLite.Server/
# The migrations are embedded in the assembly (docs/07): the image carries the
# exact scripts this build was tested with.
COPY db/ db/
RUN dotnet publish src/BlinkyLite.Server/BlinkyLite.Server.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# System.DirectoryServices.Protocols dlopens libldap at run time; without it
# every sign-in would fail with a missing-library error rather than with
# anything about the directory. The base image is Ubuntu 24.04, where the
# package is libldap2 (OpenLDAP 2.6); the Debian names are tried as well, so
# that a change of base image does not break the build silently.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates libldap-common libgssapi-krb5-2 \
    && (apt-get install -y --no-install-recommends libldap2 \
        || apt-get install -y --no-install-recommends libldap-2.6-0 \
        || apt-get install -y --no-install-recommends libldap-2.5-0) \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# The aspnet image defines APP_UID; running as root would also make every
# mounted secret readable by a process that has no reason to be root.
USER $APP_UID
EXPOSE 8443

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
