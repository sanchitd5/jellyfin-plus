# plugin-meta.sh - the plugin facts proxmox-build.sh and docker/intel/stage-plugin.sh must agree on.
# Sourced, never run. One copy, because the GUID is src/Plugin.cs's Id and manifest.json's, and a second
# copy that drifts installs a plugin Jellyfin does not recognise as the one it has (AGENTS.md invariant 10).

PLUGIN_GUID="6f2a9c31-4d7b-4e2a-9d15-8a1c0b7e3f44"
PLUGIN_NAME="Jellyfin Plus"
PLUGIN_TARGET_ABI="12.3.0.0"
PLUGIN_OVERVIEW="GPU video enhancement plus search, filtering and library quality-of-life improvements for Jellyfin."

# plugin_first_meta <version>: the meta.json of a first install, on stdout.
plugin_first_meta () {
    cat <<META
{
  "guid": "$PLUGIN_GUID",
  "name": "$PLUGIN_NAME",
  "version": "$1",
  "targetAbi": "$PLUGIN_TARGET_ABI",
  "owner": "",
  "overview": "$PLUGIN_OVERVIEW",
  "category": "General"
}
META
}
