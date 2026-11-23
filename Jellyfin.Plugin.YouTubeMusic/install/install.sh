#!/bin/sh
# Copies the plugin into /plugins/<name>/ and seeds a default config on first install.
set -eu
target="/plugins/${PLUGIN_DIR_NAME}"
mkdir -p "$target" /plugins/configurations
# Write to a temp file and rename: a running Jellyfin keeps its mapped copy of the old DLL
# instead of having it truncated underneath it. The new version loads on the next restart.
for f in Jellyfin.Plugin.YouTubeMusic.dll logo.png; do
  if ! cmp -s "/plugin/$f" "$target/$f"; then
    cp "/plugin/$f" "$target/.$f.tmp" && mv -f "$target/.$f.tmp" "$target/$f"
    echo "updated $f"
  fi
done
# Jellyfin rewrites meta.json itself (status etc.); only provide it on first install.
[ -f "$target/meta.json" ] || cp /plugin/meta.json "$target/"
# Make sure an existing meta.json points at the logo (plugin page image).
grep -q '"imagePath"' "$target/meta.json" || sed -i 's/^{/{\n  "imagePath": "logo.png",/' "$target/meta.json"

cfg=/plugins/configurations/Jellyfin.Plugin.YouTubeMusic.xml
if [ ! -f "$cfg" ]; then
  cat > "$cfg" <<XML
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <SidecarUrl>${SIDECAR_URL}</SidecarUrl>
  <SidecarApiKey />
  <EnableAutoDownloadOnFavorite>true</EnableAutoDownloadOnFavorite>
  <TriggerLibraryScanAfterDownload>true</TriggerLibraryScanAfterDownload>
  <SearchResultLimit>10</SearchResultLimit>
  <LibraryRootPath>${LIBRARY_ROOT_PATH}</LibraryRootPath>
  <SavedSearches />
</PluginConfiguration>
XML
  echo "Seeded $cfg (SidecarUrl=${SIDECAR_URL}, LibraryRootPath=${LIBRARY_ROOT_PATH})"
fi
echo "YouTube Music plugin installed to $target - restart Jellyfin to load it."
ls -la "$target"
