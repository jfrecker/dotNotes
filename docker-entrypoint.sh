#!/bin/bash
# Container entrypoint: runs as root just long enough to make sure the
# externally-mounted vault directory is writable by the user the app
# itself actually runs as, then drops privileges and exec's dotnet.
#
# Why this is needed: a bind-mounted host directory that doesn't exist
# yet gets auto-created by the Docker daemon (as root, mode 0755) the
# first time `docker compose up` runs. Without this step the app - which
# per CLAUDE.md's non-root requirement never runs as root - would get a
# permission-denied error on its very first write, and the user would
# have to manually `chown`/`chmod` the host-side vault folder before
# `docker compose up -d` works. That's exactly the kind of manual step
# this phase's brief rules out.
#
# PUID/PGID (optional, unset by default): the vault is a bind mount, and
# CLAUDE.md/docs/02-ARCHITECTURE.md's whole point of that choice over a
# named volume is "the vault is just files you own" - so any file-level
# backup tool, `git init` inside the vault, or editing a note directly
# with your own account all work with zero extra code (Phase 8's exit
# criteria). That only holds if the files the container writes are
# actually owned by *your* host user, not always the image's fixed "app"
# user (uid/gid 1654) - a real host user with a different uid would
# otherwise find every file in the vault owned by a uid that isn't
# theirs (confirmed while verifying this phase: `rm -rf ./vault` as the
# host user failed with "Permission denied" on a file the container had
# created). Set PUID/PGID in docker-compose.yml to `id -u`/`id -g` on the
# host to make the container write as that same uid/gid instead; left
# unset, this defaults to the image's built-in "app" user (1654:1654),
# preserving the previous non-root-by-default behavior with no config.
#
# `setpriv` (part of util-linux, already present in the base Ubuntu
# image - no extra package installed) drops to the target uid/gid for
# the actual dotnet process without touching the environment (ASP.NET
# Core config env vars like Vault__RootPath pass through untouched,
# unlike some `su`/login-shell approaches which reset the environment).
# It accepts a raw numeric uid/gid directly, so this works for an
# arbitrary host PUID/PGID with no matching /etc/passwd entry required.
set -euo pipefail

vault_root="${Vault__RootPath:-/data/vault}"
run_uid="${PUID:-1654}"
run_gid="${PGID:-1654}"

mkdir -p "$vault_root"
chown -R "$run_uid:$run_gid" "$vault_root"

exec setpriv --reuid="$run_uid" --regid="$run_gid" --clear-groups --inh-caps=-all \
    dotnet DotNotes.Api.dll
