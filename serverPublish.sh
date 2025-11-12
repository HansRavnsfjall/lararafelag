#!/usr/bin/env bash
set -euo pipefail

# Publish Umbraco 13 for Simply.com (IIS) — self-contained win-x86
# Usage: ./serverPublish.sh
# Requires: .NET SDK installed

cd "$(dirname "$0")"

OUT_DIR="$(pwd)/publish-out/win-x86"
PROJECT="Lararafelagid.csproj"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Error: dotnet CLI not found. Install .NET SDK and try again." >&2
  exit 1
fi

echo "Cleaning…"
dotnet clean "$PROJECT"

echo "Restoring…"
dotnet restore "$PROJECT"

echo "Resetting output folder: $OUT_DIR"
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo "Publishing (Release, win-x86, self-contained)…"
dotnet publish "$PROJECT" \
  -c Release \
  -r win-x86 \
  --self-contained true \
  -p:PublishIISAssets=true \
  -p:PublishReadyToRun=false \
  -p:PublishSingleFile=false \
  -o "$OUT_DIR"

echo "Publish complete → $OUT_DIR"
ls -la "$OUT_DIR" | sed -n '1,200p'
