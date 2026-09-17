#!/usr/bin/env bash
# Populates ../.chromium-libs/x86_64-linux-gnu with just the shared
# libraries Playwright's downloaded Chromium needs that a minimal
# Debian/Ubuntu base image doesn't ship by default (libnspr4, libnss3,
# libasound2 and their transitive deps) - no root, no system package
# install, nothing outside this script's own working directory. Safe to
# re-run; it's a no-op if the target files already exist.
#
# Only needed on a minimal Linux base without a desktop environment
# already providing these libraries system-wide (most developer desktops
# and most CI Ubuntu images already have them - this script's output
# simply won't be picked up in that case, see playwright.config.js).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="$SCRIPT_DIR/../.chromium-libs"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

PACKAGES=(libnspr4 libnss3 libasound2t64 libasound2-data)

echo "Downloading .deb packages (apt-get download, no install/root)..."
cd "$WORK_DIR"
apt-get download "${PACKAGES[@]}"

mkdir -p "$OUT_DIR"
for deb in *.deb; do
  echo "Extracting $deb..."
  dpkg -x "$deb" "$OUT_DIR"
done

mkdir -p "$OUT_DIR/x86_64-linux-gnu"
find "$OUT_DIR/usr/lib/x86_64-linux-gnu" -maxdepth 1 -type f -exec cp {} "$OUT_DIR/x86_64-linux-gnu/" \;

echo "Done. Libraries staged under $OUT_DIR/x86_64-linux-gnu (gitignored, never committed)."
