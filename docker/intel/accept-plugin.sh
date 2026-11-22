#!/usr/bin/env bash
# accept-plugin.sh [entrypoint]
#
# The image-build acceptance for the plugin: start Jellyfin headless through the image's own entrypoint, against
# throwaway data/config/cache/log dirs, and require that the plugin dir was seeded and that the server logs
# "Loaded plugin: Jellyfin Plus". A miss prints the log tail and exits 1, so an image that exists has loaded
# the plugin. Needs no GPU, library or network. Everything it writes is under one temp dir, removed on exit.
set -euo pipefail

ENTRY="${1:-/usr/local/bin/jellyfin-plus-entrypoint}"
WAIT="${ACCEPT_WAIT:-120}"
WANT="Loaded plugin: Jellyfin Plus"

T="$(mktemp -d)"
PID=""
stop() {
    if [[ -n "$PID" ]] && kill -0 "$PID" 2>/dev/null; then
        kill "$PID" 2>/dev/null || true
        for _ in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$PID" 2>/dev/null || break; sleep 1; done
        kill -9 "$PID" 2>/dev/null || true
    fi
    rm -rf "$T"
}
trap stop EXIT

mkdir -p "$T/data" "$T/config" "$T/cache" "$T/log"
LOG="$T/jellyfin.out"

JELLYFIN_DATA_DIR="$T/data" JELLYFIN_CONFIG_DIR="$T/config" JELLYFIN_CACHE_DIR="$T/cache" JELLYFIN_LOG_DIR="$T/log" \
    "$ENTRY" >"$LOG" 2>&1 &
PID=$!

fail() {
    echo "accept-plugin: $1" >&2
    echo "--- last 60 lines of the server output ---" >&2
    tail -n 60 "$LOG" >&2 || true
    exit 1
}

found=0
for ((i = 0; i < WAIT; i++)); do
    if grep -q "$WANT" "$LOG"; then found=1; break; fi
    kill -0 "$PID" 2>/dev/null || fail "Jellyfin exited before it loaded the plugin"
    sleep 1
done
[[ "$found" == 1 ]] || fail "no '$WANT' line within ${WAIT}s"

shopt -s nullglob
seeded=("$T"/data/plugins/JellyfinPlus_*/meta.json)
[[ ${#seeded[@]} -ge 1 ]] || fail "the log names the plugin but plugins/JellyfinPlus_* was not seeded"
[[ -f "$(dirname "${seeded[0]}")/Jellyfin.Plugin.Plus.dll" ]] || fail "the seeded plugin dir has no Jellyfin.Plugin.Plus.dll"

grep -m1 "$WANT" "$LOG"
echo "accept-plugin: ok, $(dirname "${seeded[0]}" | xargs basename) seeded and loaded"
