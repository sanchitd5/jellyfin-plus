#!/usr/bin/env bash
# stage-plugin.sh <repo> <jellyfin-bin> <plugin-dist> <patcher-dir>
#
# The image-time half of scripts/proxmox-build.sh's assembly and staging steps, with no server to
# deploy to: build the plugin and the patcher against the Jellyfin assemblies already in the image,
# then lay them out for the places they are read from.
#
#   <plugin-dist>/JellyfinPlus_<version>/   plugin DLL, deps.json, meta.json, icon. NOT in /config: an
#                                           initContainer copies it in, as the SSO plugin is copied.
#   <patcher-dir>/                          the patcher DLL and 0Harmony.dll (PatcherLoader.PatcherDirectory,
#                                           /usr/lib/jellyfin-plus), plus the injector's inputs.
#
# meta.json is the first-install one proxmox-build.sh writes (same guid, name, targetAbi), with
# assets/plugin-meta.json merged over it, as that script does for an existing install.
set -euo pipefail

REPO="${1:?usage: stage-plugin.sh <repo> <jellyfin-bin> <plugin-dist> <patcher-dir>}"
BIN="${2:?jellyfin-bin}"
DIST="${3:?plugin-dist}"
PATCHER="${4:?patcher-dir}"
VERSION="${PLUGIN_VERSION:-1.0.0.0}"

[[ -d "$BIN" ]] || { echo "stage-plugin: no Jellyfin assemblies at $BIN" >&2; exit 1; }
[[ -d "$REPO/src/patcher" ]] || { echo "stage-plugin: no $REPO/src/patcher" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

dotnet publish "$REPO/src"         -c Release -p:JellyfinBin="$BIN" -o "$WORK/out"         --nologo -v q
dotnet publish "$REPO/src/patcher" -c Release -p:JellyfinBin="$BIN" -o "$WORK/out-patcher" --nologo -v q

PLUGIN_DIR="$DIST/JellyfinPlus_$VERSION"
mkdir -p "$PLUGIN_DIR" "$PATCHER"
cp -a "$WORK/out/Jellyfin.Plugin.Plus.dll"       "$PLUGIN_DIR/"
cp -a "$WORK/out/Jellyfin.Plugin.Plus.deps.json" "$PLUGIN_DIR/"
cp -a "$WORK/out-patcher/Jellyfin.Plugin.Plus.Patcher.dll" "$PATCHER/"
cp -a "$WORK/out-patcher/0Harmony.dll"                      "$PATCHER/"
install -m 0644 "$REPO/assets/jellyfinplus.png" "$PLUGIN_DIR/jellyfinplus.png"

cat > "$PLUGIN_DIR/meta.json" <<META
{
  "guid": "6f2a9c31-4d7b-4e2a-9d15-8a1c0b7e3f44",
  "name": "Jellyfin Plus",
  "version": "$VERSION",
  "targetAbi": "12.3.0.0",
  "owner": "",
  "overview": "GPU video enhancement plus search, filtering and library quality-of-life improvements for Jellyfin.",
  "category": "General"
}
META

python3 - "$PLUGIN_DIR/meta.json" "$REPO/assets/plugin-meta.json" <<'PYEOF'
import json, os, sys
path, extra = sys.argv[1], sys.argv[2]
meta = json.load(open(path))
add = {k: v for k, v in json.load(open(extra)).items() if not k.startswith('_')}
tmp = path + '.tmp'
json.dump(dict(meta, **add), open(tmp, 'w'), indent=2)
os.replace(tmp, path)
PYEOF

echo "stage-plugin: $PLUGIN_DIR and $PATCHER staged (version $VERSION)"
