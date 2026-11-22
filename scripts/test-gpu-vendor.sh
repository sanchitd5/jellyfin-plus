#!/usr/bin/env bash
# test-gpu-vendor.sh - tests for GPU vendor detection and for "report only".
#
#   scripts/test-gpu-vendor.sh /path/to/jfbin
#
# jfbin is a directory holding the server's own MediaBrowser.*.dll (a copy of the server's
# /usr/lib/jellyfin/bin, see CLAUDE.md "Deploying"). The harness compiles the real patcher sources
# against them (scripts/gpu-vendor-harness), so it needs no GPU and no running Jellyfin.
#
# What it proves (docs/architecture/GPU_VENDORS.md, PR 1, PR 2, PR 3 and PR 5):
#   - the PCI vendor ids map to the right vendor (0x10de NVIDIA, 0x8086 Intel, anything else none);
#   - auto picks NVIDIA first, the explicit settings override it, a vendor that was chosen and not
#     found reports Detected=false instead of claiming a capability;
#   - the real sysfs reader works on a fake sysfs tree, and ignores a node with no /dev entry;
#   - with GpuVendor set to ANY value, and no Intel GPU that was actually seen, HwaccelArgs,
#     BuildChain and EncoderFor return exactly what they returned before the setting existed (the
#     NVIDIA command lines are byte-identical), on a no-GPU, an NVIDIA and a mixed machine;
#   - (PR 2) with an Intel GPU seen, the encoder is QSV (software when ffmpeg lists none), a VA-API
#     encoder is refused, an NVENC setting is treated as auto, the chain never ends in the CUDA
#     hand-off, and nothing emitted contains nvenc, cuda or vaapi;
#   - (PR 2) the DRM fdinfo reader (i915 and xe fixtures, a fake /proc), the idle-residency reader,
#     and GET /JellyfinPlus/Gpu/Load through the real controller;
#   - (PR 3) on a no-GPU, an NVIDIA and a mixed machine the Vulkan device stays "vulkan=vk:0", the
#     render-node test never runs, and the probe (the same keys and level lists as ShaderLibrary
#     offers) and the status route carry nothing new; with an Intel GPU seen the Vulkan device is
#     derived from its render node (never vk:0), falls back to vk:0 when the test start fails, a
#     QSV device is pinned only above renderD135, a two-GPU machine gets the node of the vendor
#     that was chosen, the NVIDIA-only levels (optix, dlpp, vsr-rtcuda, fsr2, dlss, dlaa) are
#     withheld from the probe and not run on Intel, with the reason in the probe and the session
#     record, and an NVIDIA session with the same request runs them;
#   - (PR 3) the shim's optional render_node (scripts/test-shim-device.py, when python3 is present);
#   - (PR 5) the ort node is byte-identical on every server that is not a detected Intel one, whatever
#     the OpenVINO provider file or the setting says; on Intel it asks for device=openvino with the
#     provider file and device=cpu without it, with the reason in the record; and NeuralMaxSourceHeight
#     (0 changes nothing, the probe has no new key) routes the ONNX levels by upright source height:
#     chosen at or below it only when nobody asked and an accelerator is there, dropped above it with
#     the reason, never for the CUDA-native levels, a viewer's Off, or a source with no upscale;
#     device=openvino is asked for only when a stub patched ffmpeg's `-h filter=ort` lists it,
#     else device=cpu with a rebuild note (the real probe runs, off the request path).
#     The C side of the filter is scripts/test-vf-ort.sh (compile check and provider calls), not this.
#   - (PR 6) the fsr2-nocuda marker counts only for a detected Intel GPU, and only with a passing one-frame
#     fsr2 probe (a fake one here, never run on an NVIDIA or no-GPU box, retried after its TTL when it
#     fails); with both fsr2 (not dlss or dlaa) is offered and runs on Intel and the record says the motion
#     vectors are zero, without them all three stay withheld, and an NVIDIA machine is unchanged. The C
#     side is scripts/test-gu-inputs.sh.
#   - (PR 7) IntelHardwareDecode (default off): off, or on without a detected Intel GPU, the command
#     line, the probe and the session JSON are identical to the setting-off ones, the reason is null and
#     the start-up test never runs; on with an Intel GPU, a passing per-codec test and a plan with no
#     CPU-side node, the command decodes with VA-API and maps to Vulkan (golden argument, chain and test
#     command strings, `-hwaccel_device va` included). The source gate is a table over profiles and chroma
#     formats: only 8-bit 4:2:0 h264 Main/High and hevc Main pass. The test is read, never waited on
#     (CachedProbe on a fake clock: cold = software, true kept for life, failed retried after 60 s, a
#     throw is no answer, the answer is per codec); a missing patched ffmpeg, a CPU-side step, HDR, 10-bit,
#     interlaced, sideways sources or an unusable render node keep software decode with the reason in the
#     record. JoinChain pins where Jellyfin's kept nodes sit. The shim's routing and its warning are
#     scripts/test-shim-device.py and scripts/test_shim_patched_path.py. Not run on hardware.
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
status=$?

if command -v python3 >/dev/null; then
    echo
    echo "shim device arguments (scripts/test-shim-device.py)"
    python3 -I "$HERE/test-shim-device.py" || status=1
else
    echo
    echo "test-gpu-vendor: python3 not found, so the shim device checks (scripts/test-shim-device.py) were NOT run"
fi

exit "$status"
