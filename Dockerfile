# syntax=docker/dockerfile:1

# dotNotes container image
#
# Multi-stage build: the SDK image only exists to restore/build/publish
# the app; the final runtime image is the much smaller ASP.NET Core
# runtime image with nothing but the published output and a non-root
# user. Per CLAUDE.md's hard constraints, the vault directory is never
# baked into this image — it is always supplied at run time via a
# mounted volume at /data/vault (see docker-compose.yml). A container
# rebuild/recreate therefore never touches note data.

# ---- Build stage -----------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the two shipped projects' csproj files first (not the test
# projects - they aren't part of the published app) so `dotnet restore`
# layer-caches across rebuilds that only change application code.
COPY src/DotNotes.Api/DotNotes.Api.csproj src/DotNotes.Api/
COPY src/DotNotes.Core/DotNotes.Core.csproj src/DotNotes.Core/

RUN dotnet restore src/DotNotes.Api/DotNotes.Api.csproj

# Now copy the rest of the source and publish the API project (Release
# config). This only builds/publishes DotNotes.Api and its
# DotNotes.Core project reference - the two test projects are never
# built as part of the image.
COPY src/DotNotes.Api/ src/DotNotes.Api/
COPY src/DotNotes.Core/ src/DotNotes.Core/
RUN dotnet publish src/DotNotes.Api/DotNotes.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---- Runtime stage -----------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The aspnet base image ships a low-privilege "app" user/group
# (uid/gid 1654 as of the 10.0 image) for exactly this purpose,
# starting with the .NET 8+ images - the actual dotnet process runs as
# that user, never as root (see docker-entrypoint.sh). /data is created
# and pre-owned by "app" here so a *named volume* or a bind mount onto
# an *already-existing, already-owned* host directory works with zero
# extra steps; docker-entrypoint.sh additionally re-chowns whatever the
# resolved vault root is at container start, which is what actually
# matters for the common case of a *bind mount whose host directory
# doesn't exist yet* (Docker auto-creates that as root:root, which
# would otherwise deny the app user's very first write).
RUN mkdir -p /data/vault && chown -R app:app /data

COPY --from=build --chown=app:app /app/publish .
COPY --chmod=755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

# Bind Kestrel to all interfaces inside the container, not just
# localhost - the host/other containers can only reach a container's
# process via a non-loopback bind. Port matches Server:Port in
# appsettings.json (informational there; this is what actually
# controls the bound address/port).
ENV ASPNETCORE_URLS=http://+:5175

# Absolute, unambiguous in-container path so the compose file's bind
# mount target lines up exactly. Overridable via Vault__RootPath if a
# user wants a different in-container path for some reason, but the
# compose file should not need to.
ENV Vault__RootPath=/data/vault

EXPOSE 5175

# The aspnet base image has neither curl nor wget, and this image
# deliberately doesn't install either (keeping the runtime image small
# and dependency-free, per the packaging brief) - bash is present
# though, so use its /dev/tcp pseudo-device to issue a minimal raw HTTP
# GET against /healthz (Phase 0's health check endpoint) and check for
# a "200" status line.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/5175 && printf "GET /healthz HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3 && head -n 1 <&3 | grep -q "200"' || exit 1

ENTRYPOINT ["docker-entrypoint.sh"]
