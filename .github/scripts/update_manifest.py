"""Adds (or replaces) one plugin version in a Jellyfin plugin-repository manifest.json.

Usage: update_manifest.py <manifest.json> <meta.json> <version> <sourceUrl> <checksum> <imageUrl>

The manifest is a JSON array with one entry per plugin; each entry lists every published
version, newest first. Jellyfin reads it from Dashboard -> Plugins -> Repositories.
"""
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

manifest_path, meta_path, version, source_url, checksum, image_url = sys.argv[1:7]

meta = json.loads(Path(meta_path).read_text(encoding="utf-8"))
manifest_file = Path(manifest_path)
manifest = json.loads(manifest_file.read_text(encoding="utf-8")) if manifest_file.exists() else []

entry = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
if entry is None:
    entry = {"guid": meta["guid"], "versions": []}
    manifest.append(entry)

entry.update({
    "name": meta["name"],
    "description": meta["description"],
    "overview": meta["overview"],
    "owner": meta["owner"],
    "category": meta["category"],
    "imageUrl": image_url,
})

new_version = {
    "version": version,
    "changelog": meta.get("changelog", ""),
    "targetAbi": meta["targetAbi"],
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}
versions = [v for v in entry["versions"] if v["version"] != version]
versions.append(new_version)
versions.sort(key=lambda v: tuple(int(x) for x in v["version"].split(".")), reverse=True)
entry["versions"] = versions

manifest_file.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"{meta['name']} {version}: {len(versions)} version(s) in {manifest_path}")
