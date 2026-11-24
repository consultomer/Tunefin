#!/usr/bin/env bash
# Builds the Jellyfin plugin with a local .NET 10 SDK, or in a throwaway Docker/Podman container
# (no local SDK needed)
# and stages it in ./dist/YouTubeMusic_1.0.0.0/ ready to copy into <jellyfin-config>/plugins/.
set -euo pipefail
cd "$(dirname "$0")"

PLUGIN_DIR=Jellyfin.Plugin.YouTubeMusic
VERSION=$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' "$PLUGIN_DIR/Jellyfin.Plugin.YouTubeMusic.csproj")
OUT="dist/YouTubeMusic_${VERSION}"

if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  dotnet publish "$PLUGIN_DIR" -c Release -o "$PLUGIN_DIR/bin/publish"
elif command -v docker >/dev/null 2>&1; then
  docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -v "$PWD/$PLUGIN_DIR:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet publish -c Release -o /src/bin/publish
elif command -v podman >/dev/null 2>&1; then
  # Rootless podman maps the container's root to your user, so no --user is needed.
  podman run --rm -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -v "$PWD/$PLUGIN_DIR:/src:Z" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet publish -c Release -o /src/bin/publish
else
  echo "Need the .NET 10 SDK, Docker or Podman to build the plugin." >&2
  exit 1
fi

rm -rf "$OUT" && mkdir -p "$OUT"
cp "$PLUGIN_DIR/bin/publish/Jellyfin.Plugin.YouTubeMusic.dll" "$OUT/"
cp "$PLUGIN_DIR/meta.json" "$OUT/"
cp "$PLUGIN_DIR/Resources/logo.png" "$OUT/logo.png"
echo "Plugin staged in $OUT"
ls -la "$OUT"
