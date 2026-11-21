#!/usr/bin/env bash
# test-gpu-vendor.sh - tests for GPU vendor detection and for "report only".
#
#   scripts/test-gpu-vendor.sh /path/to/jfbin
#
# jfbin is a directory holding the server's own MediaBrowser.*.dll (a copy of the server's
# /usr/lib/jellyfin/bin, see CLAUDE.md "Deploying"). The harness compiles the real patcher sources
# against them (scripts/gpu-vendor-harness), so it needs no GPU and no running Jellyfin.
#
# What it proves (docs/architecture/GPU_VENDORS.md, PR 1):
#   - the PCI vendor ids map to the right vendor (0x10de NVIDIA, 0x8086 Intel, anything else none);
#   - auto picks NVIDIA first, the explicit settings override it, a vendor that was chosen and not
#     found reports Detected=false instead of claiming a capability;
#   - the real sysfs reader works on a fake sysfs tree, and ignores a node with no /dev entry;
#   - with GpuVendor set to ANY value, HwaccelArgs, BuildChain and EncoderFor return exactly what
#     they returned before the setting existed (the NVIDIA command lines are byte-identical).
#
# Exit status: 0 every check passed, 1 a check failed, 2 the harness could not be built.
set -uo pipefail

JFBIN="${1:-}"
[ -d "$JFBIN" ] || { echo "test-gpu-vendor: '$JFBIN' is not a directory (pass the server's bin dir with MediaBrowser.*.dll)" >&2; exit 2; }
command -v dotnet >/dev/null || { echo "test-gpu-vendor: dotnet SDK not found" >&2; exit 2; }

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$(mktemp -d "${TMPDIR:-/tmp}/gpu-vendor-harness.XXXXXX")"
trap 'rm -rf "$OUT"' EXIT

dotnet build "$HERE/gpu-vendor-harness/GpuVendorHarness.csproj" -c Release \
    -p:JellyfinBin="$(cd "$JFBIN" && pwd)" -o "$OUT" --nologo -v q || { echo "test-gpu-vendor: build failed" >&2; exit 2; }

dotnet "$OUT/GpuVendorHarness.dll"
