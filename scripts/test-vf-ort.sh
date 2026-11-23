#!/usr/bin/env bash
# test-vf-ort.sh - compile ffmpeg/vf_ort.c against FFmpeg's own headers and check its provider calls.
#
#   scripts/test-vf-ort.sh [path/to/ffmpeg-8.1.2.tar.xz]
#
# Two things, neither of which needs a GPU, an ONNX Runtime library or a model:
#
#   1. COMPILE. vf_ort.c is compiled with -Wall -Wextra -Werror against the headers of the FFmpeg
#      release the build script pins (8.1.2) and the ONNX Runtime C header of the version the
#      OpenVINO build pins (1.24.1). This is the compile-only check: it proves the file is valid C
#      against those headers, not that the patched tree builds (patches 0001 to 0015, configure and
#      every other filter are not involved) and not that it runs.
#   2. PROVIDER CALLS. scripts/vf-ort-test/provider_calls.c includes vf_ort.c, replaces the ONNX
#      Runtime API with a fake, and runs the filter's session setup for device=cuda, cpu and
#      openvino: which provider is appended with which options, the CPU fallbacks (append fails;
#      session fails with OpenVINO and is rebuilt on the CPU once), and that the CUDA sequence NVIDIA
#      runs is unchanged. It is not an inference test and not a run on an Intel GPU.
#
#   3. (optional) THE REAL PROVIDER. With ORT_LIB_DIR (a directory holding libonnxruntime.so, for
#      example <prefix>/ort/lib of an ORT_EP=openvino build) and ORT_MODEL (any one-input,
#      one-output .onnx file) set, scripts/vf-ort-test/provider_real.c runs the same session setup
#      against that real ONNX Runtime and model for device=cuda, cpu and openvino and prints what ran
#      on this machine. On an Intel server with the GPU and its OpenCL runtime the openvino line ends
#      "device_name=openvino, ep_openvino=1"; anywhere else it ends "device_name=cpu" after the
#      filter's warning, which is the fallback working. The header the script fetches is 1.24.1: the runtime must be 1.24.1 too.
#
# It builds libavutil from the FFmpeg tarball (about a minute) and needs gcc, make, xz and python3,
# with network access to nuget.org (the ONNX Runtime headers, each checked against a pinned sha256;
# see scripts/fetch-ort-header.py) and, when no tarball is given, to ffmpeg.org (checked against a
# pinned sha256). Any native Linux with those works; an arm64 container is enough, because
# nothing here depends on the machine's instruction set. For example, from the repository root:
#
#   docker run --rm -v "$PWD":/repo:ro ubuntu:24.04 bash -c \
#       'apt-get update -qq && apt-get install -y -qq gcc libc6-dev make xz-utils python3 curl ca-certificates \
#        && /repo/scripts/test-vf-ort.sh'
#
# Exit status: 0 everything passed, 1 a check failed, 2 the tools or downloads were missing.
set -uo pipefail

FFMPEG_VER=8.1.2
FFMPEG_SHA256=464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c
ORT_VER=1.24.1
ORT_C_API_SHA256=7f03541b0233c53b8c91855910c9382c4fb4a26b8fe099965c7c3cad1af2ced6
ORT_EP_C_API_SHA256=7de0ebae527f18b39fbeb3129d5781a995e76f685842c2e16aa0468230e6368b

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
for tool in gcc make xz python3 sha256sum tar; do
    command -v "$tool" >/dev/null || { echo "test-vf-ort: $tool not found" >&2; exit 2; }
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/test-vf-ort.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

TARBALL="${1:-}"
if [ -z "$TARBALL" ]; then
    command -v curl >/dev/null || { echo "test-vf-ort: curl not found (or pass the FFmpeg tarball)" >&2; exit 2; }
    TARBALL="$WORK/ffmpeg-$FFMPEG_VER.tar.xz"
    curl -fsSL -o "$TARBALL" "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VER.tar.xz" \
        || { echo "test-vf-ort: could not download FFmpeg $FFMPEG_VER" >&2; exit 2; }
fi
[ "$(sha256sum "$TARBALL" | cut -d' ' -f1)" = "$FFMPEG_SHA256" ] \
    || { echo "test-vf-ort: $TARBALL is not the FFmpeg $FFMPEG_VER tarball (sha256 differs)" >&2; exit 2; }

python3 -I "$REPO/scripts/fetch-ort-header.py" "$ORT_VER" "$WORK/ort-include" \
        "onnxruntime_c_api.h:$ORT_C_API_SHA256" "onnxruntime_ep_c_api.h:$ORT_EP_C_API_SHA256" \
    || { echo "test-vf-ort: could not fetch the ONNX Runtime $ORT_VER header" >&2; exit 2; }

tar -C "$WORK" -xf "$TARBALL" || exit 2
SRC="$WORK/ffmpeg-$FFMPEG_VER"
cd "$SRC" || exit 2

echo "configure (libavutil only) and build"
./configure --disable-everything --disable-programs --disable-doc --disable-asm --disable-autodetect \
    --disable-network --cc=gcc >"$WORK/configure.log" 2>&1 \
    || { tail -20 "$WORK/configure.log" >&2; echo "test-vf-ort: configure failed" >&2; exit 2; }
make -j"$(nproc 2>/dev/null || echo 2)" libavutil/libavutil.a >"$WORK/make.log" 2>&1 \
    || { tail -20 "$WORK/make.log" >&2; echo "test-vf-ort: building libavutil failed" >&2; exit 2; }

cp "$REPO/ffmpeg/vf_ort.c" libavfilter/vf_ort.c
CFLAGS=(-std=gnu17 -O1 -D_GNU_SOURCE -DHAVE_AV_CONFIG_H -I. -I"$WORK/ort-include")

echo
echo "1. compile ffmpeg/vf_ort.c against FFmpeg $FFMPEG_VER and ONNX Runtime $ORT_VER headers (-Werror)"
gcc "${CFLAGS[@]}" -Wall -Wextra -Werror -Wno-missing-field-initializers -Wno-unused-parameter \
    -c libavfilter/vf_ort.c -o "$WORK/vf_ort.o" || { echo "FAIL  vf_ort.c does not compile"; exit 1; }
echo "PASS  vf_ort.c compiles clean"
nm "$WORK/vf_ort.o" | grep -q ' D ff_vf_ort$' || { echo "FAIL  no ff_vf_ort in the object"; exit 1; }
echo "PASS  the object defines ff_vf_ort"

echo
echo "2. provider calls against a fake ONNX Runtime"
gcc "${CFLAGS[@]}" -Wall -Wextra -Wno-missing-field-initializers -Wno-unused-parameter -Ilibavfilter \
    "$REPO/scripts/vf-ort-test/provider_calls.c" libavutil/libavutil.a -lm -lpthread \
    -o "$WORK/provider_calls" || { echo "FAIL  the provider test does not build"; exit 1; }
"$WORK/provider_calls" || exit 1

if [ -n "${ORT_LIB_DIR:-}" ] && [ -n "${ORT_MODEL:-}" ]; then
    echo
    echo "3. the real ONNX Runtime in $ORT_LIB_DIR with $ORT_MODEL"
    gcc "${CFLAGS[@]}" -Wall -Wextra -Wno-missing-field-initializers -Wno-unused-parameter -Ilibavfilter \
        "$REPO/scripts/vf-ort-test/provider_real.c" libavutil/libavutil.a -L"$ORT_LIB_DIR" -lonnxruntime \
        -Wl,-rpath,"$ORT_LIB_DIR" -lm -lpthread -o "$WORK/provider_real" \
        || { echo "FAIL  the real-provider test does not build"; exit 1; }
    "$WORK/provider_real" "$ORT_MODEL" || { echo "FAIL  a session could not be built"; exit 1; }
fi
