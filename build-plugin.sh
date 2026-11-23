#!/usr/bin/env bash
# Builds the Jellyfin plugin with the .NET SDK in Docker (no local SDK needed)
# and stages it in ./dist/YouTubeMusic_1.0.0.0/ ready to copy into <jellyfin-config>/plugins/.
set -euo pipefail
cd "$(dirname "$0")"

PLUGIN_DIR=Jellyfin.Plugin.YouTubeMusic
VERSION=$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' "$PLUGIN_DIR/Jellyfin.Plugin.YouTubeMusic.csproj")
OUT="dist/YouTubeMusic_${VERSION}"

if command -v dotnet >/dev/null 2>&1; then
  dotnet publish "$PLUGIN_DIR" -c Release -o "$PLUGIN_DIR/bin/publish"
else
  docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -v "$PWD/$PLUGIN_DIR:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet publish -c Release -o /src/bin/publish
fi

rm -rf "$OUT" && mkdir -p "$OUT"
cp "$PLUGIN_DIR/bin/publish/Jellyfin.Plugin.YouTubeMusic.dll" "$OUT/"
cp "$PLUGIN_DIR/meta.json" "$OUT/"
echo "Plugin staged in $OUT"
ls -la "$OUT"
