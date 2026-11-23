#!/usr/bin/env bash
# test-gu-inputs.sh - compile ffmpeg/gu_inputs.h with GU_NO_NVOF and run it with no CUDA.
#
#   scripts/test-gu-inputs.sh [path/to/ffmpeg-8.1.2.tar.xz]
#
# Two things, neither of which needs a GPU, libcuda, the Optical Flow SDK or an ONNX Runtime library:
#
#   1. COMPILE. scripts/gu-inputs-test/no_cuda.c includes gu_inputs.h with -DGU_NO_NVOF and
#      -Wall -Wextra -Werror against the headers of the FFmpeg release the build script pins
#      (8.1.2) and the ONNX Runtime C header the depth code includes. It proves the CUDA-free
#      variant is valid C there, not that the patched tree builds (patches 0001, 0015 and 0016 and
#      configure are not involved).
#   2. RUN. gu_inputs_init() and gu_inputs_frame() on synthetic frames: init succeeds, the flow and the
#      reactive mask are zero, the depth is flat, and the phase-correlation jitter still measures a
#      known shift. It is not a run of fsr2 and not a run on an Intel GPU.
#   3. and 4. The same two for the DEFAULT variant (CUDA and NVOFA, the code an NVIDIA server runs),
#      against stub ffnvcodec and Optical Flow headers written here and a fake CUDA: the CUDA call
#      order, and that a CUDA failing at load, cuInit, cuDeviceGet, the retain or the context push is
#      still an error from init, with only what was taken released. The stubs declare only what
#      gu_inputs.h touches, so this does not prove the real SDK headers compile (build-ffmpeg.sh
#      WITH_FSR2=1 on NVIDIA is that check), and the NVOFA calls themselves are not run (no library:
#      the step is skipped if one is installed).
#   5. PATCHES. `git apply` of 0001, 0015 and 0016 in order to the pristine configure, Makefile and
#      allfilters.c of the tarball, then scripts/test_patch_stack.py --tree on the same files.
#
# Builds libavutil from the FFmpeg tarball (about a minute) and needs gcc, make, xz and python3, with
# network access to nuget.org (the ONNX Runtime header, checked against a pinned sha256; see
# scripts/fetch-ort-header.py) and, when no tarball is given, to ffmpeg.org (checked against a pinned
# sha256). For example, from the repository root:
#
#   docker run --rm -v "$PWD":/repo:ro ubuntu:24.04 bash -c \
#       'apt-get update -qq && apt-get install -y -qq gcc libc6-dev make xz-utils python3 curl ca-certificates \
#        && /repo/scripts/test-gu-inputs.sh'
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
    command -v "$tool" >/dev/null || { echo "test-gu-inputs: $tool not found" >&2; exit 2; }
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/test-gu-inputs.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

TARBALL="${1:-}"
if [ -z "$TARBALL" ]; then
    command -v curl >/dev/null || { echo "test-gu-inputs: curl not found (or pass the FFmpeg tarball)" >&2; exit 2; }
    TARBALL="$WORK/ffmpeg-$FFMPEG_VER.tar.xz"
    curl -fsSL -o "$TARBALL" "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VER.tar.xz" \
        || { echo "test-gu-inputs: could not download FFmpeg $FFMPEG_VER" >&2; exit 2; }
fi
[ "$(sha256sum "$TARBALL" | cut -d' ' -f1)" = "$FFMPEG_SHA256" ] \
    || { echo "test-gu-inputs: $TARBALL is not the FFmpeg $FFMPEG_VER tarball (sha256 differs)" >&2; exit 2; }

python3 -I "$REPO/scripts/fetch-ort-header.py" "$ORT_VER" "$WORK/ort-include" \
        "onnxruntime_c_api.h:$ORT_C_API_SHA256" "onnxruntime_ep_c_api.h:$ORT_EP_C_API_SHA256" \
    || { echo "test-gu-inputs: could not fetch the ONNX Runtime $ORT_VER header" >&2; exit 2; }

tar -C "$WORK" -xf "$TARBALL" || exit 2
SRC="$WORK/ffmpeg-$FFMPEG_VER"
cd "$SRC" || exit 2

echo "configure (libavutil only) and build"
./configure --disable-everything --disable-programs --disable-doc --disable-asm --disable-autodetect \
    --disable-network --cc=gcc >"$WORK/configure.log" 2>&1 \
    || { tail -20 "$WORK/configure.log" >&2; echo "test-gu-inputs: configure failed" >&2; exit 2; }
make -j"$(nproc 2>/dev/null || echo 2)" libavutil/libavutil.a >"$WORK/make.log" 2>&1 \
    || { tail -20 "$WORK/make.log" >&2; echo "test-gu-inputs: building libavutil failed" >&2; exit 2; }

cp "$REPO/ffmpeg/gu_inputs.h" libavfilter/gu_inputs.h
CFLAGS=(-std=gnu17 -O1 -D_GNU_SOURCE -DHAVE_AV_CONFIG_H -DGU_NO_NVOF -I. -Ilibavfilter -I"$WORK/ort-include")

echo
echo "1. compile ffmpeg/gu_inputs.h with -DGU_NO_NVOF against FFmpeg $FFMPEG_VER (-Werror)"
gcc "${CFLAGS[@]}" -Wall -Wextra -Werror -Wno-missing-field-initializers -Wno-unused-parameter \
    -c "$REPO/scripts/gu-inputs-test/no_cuda.c" -o "$WORK/no_cuda.o" \
    || { echo "FAIL  gu_inputs.h does not compile without CUDA"; exit 1; }
echo "PASS  gu_inputs.h compiles clean without CUDA"

echo
echo "2. init and frames with no CUDA"
gcc "$WORK/no_cuda.o" libavutil/libavutil.a -ldl -lm -lpthread -o "$WORK/no_cuda" \
    || { echo "FAIL  the test does not link"; exit 1; }
"$WORK/no_cuda" || exit 1

# The default build includes nv-codec-headers and the Optical Flow SDK, which are not here: these stubs
# declare only what gu_inputs.h touches, so the default code path is compiled and run against a fake CUDA.
mkdir -p "$WORK/stub/ffnvcodec"
cat >"$WORK/stub/ffnvcodec/dynlink_loader.h" <<'EOF'
#ifndef STUB_DYNLINK_LOADER_H
#define STUB_DYNLINK_LOADER_H
#include <stddef.h>
#include <stdint.h>
typedef int CUresult;
#define CUDA_SUCCESS 0
typedef int CUdevice;
typedef struct CUctx_st *CUcontext;
typedef unsigned long long CUdeviceptr;
enum { CU_MEMORYTYPE_HOST = 1, CU_MEMORYTYPE_DEVICE = 2 };
typedef struct CUDA_MEMCPY2D {
    int srcMemoryType; const void *srcHost; CUdeviceptr srcDevice; size_t srcPitch;
    int dstMemoryType; void *dstHost; CUdeviceptr dstDevice; size_t dstPitch;
    size_t WidthInBytes; size_t Height;
} CUDA_MEMCPY2D;
typedef struct CudaFunctions {
    CUresult (*cuInit)(unsigned int);
    CUresult (*cuDeviceGet)(CUdevice *, int);
    CUresult (*cuDevicePrimaryCtxRetain)(CUcontext *, CUdevice);
    CUresult (*cuDevicePrimaryCtxRelease)(CUdevice);
    CUresult (*cuCtxPushCurrent)(CUcontext);
    CUresult (*cuCtxPopCurrent)(CUcontext *);
    CUresult (*cuMemcpy2D)(const CUDA_MEMCPY2D *);
    CUresult (*cuGetErrorName)(CUresult, const char **);
} CudaFunctions;
int fake_cuda_load_functions(CudaFunctions **, void *);
void fake_cuda_free_functions(CudaFunctions **);
#define cuda_load_functions fake_cuda_load_functions
#define cuda_free_functions fake_cuda_free_functions
#endif
EOF
cat >"$WORK/stub/nvOpticalFlowCommon.h" <<'EOF'
#ifndef STUB_NVOF_COMMON_H
#define STUB_NVOF_COMMON_H
#include <stdint.h>
typedef int NV_OF_STATUS;
#define NV_OF_SUCCESS 0
#define NV_OF_API_VERSION 0
typedef struct NvOFObj *NvOFHandle;
typedef struct NvOFBuf *NvOFGPUBufferHandle;
enum { NV_OF_OUTPUT_VECTOR_GRID_SIZE_4 = 4, NV_OF_HINT_VECTOR_GRID_SIZE_UNDEFINED = 0,
       NV_OF_MODE_OPTICALFLOW = 1, NV_OF_PERF_LEVEL_FAST = 1,
       NV_OF_BUFFER_FORMAT_GRAYSCALE8 = 1, NV_OF_BUFFER_FORMAT_SHORT2 = 2,
       NV_OF_BUFFER_USAGE_INPUT = 1, NV_OF_BUFFER_USAGE_OUTPUT = 2,
       NV_OF_CUDA_BUFFER_TYPE_CUDEVICEPTR = 1 };
typedef struct NV_OF_INIT_PARAMS { uint32_t width, height, outGridSize, hintGridSize, mode, perfLevel; } NV_OF_INIT_PARAMS;
typedef struct NV_OF_BUFFER_DESCRIPTOR { uint32_t width, height, bufferFormat, bufferUsage; } NV_OF_BUFFER_DESCRIPTOR;
typedef struct NV_OF_CUDA_BUFFER_STRIDE_INFO { struct { uint32_t strideXInBytes; } strideInfo[3]; } NV_OF_CUDA_BUFFER_STRIDE_INFO;
typedef struct NV_OF_EXECUTE_INPUT_PARAMS { NvOFGPUBufferHandle inputFrame, referenceFrame; } NV_OF_EXECUTE_INPUT_PARAMS;
typedef struct NV_OF_EXECUTE_OUTPUT_PARAMS { NvOFGPUBufferHandle outputBuffer; } NV_OF_EXECUTE_OUTPUT_PARAMS;
#endif
EOF
cat >"$WORK/stub/nvOpticalFlowCuda.h" <<'EOF'
#ifndef STUB_NVOF_CUDA_H
#define STUB_NVOF_CUDA_H
typedef struct NV_OF_CUDA_API_FUNCTION_LIST {
    NV_OF_STATUS (*nvCreateOpticalFlowCuda)(CUcontext, NvOFHandle *);
    NV_OF_STATUS (*nvOFInit)(NvOFHandle, const NV_OF_INIT_PARAMS *);
    NV_OF_STATUS (*nvOFCreateGPUBufferCuda)(NvOFHandle, const NV_OF_BUFFER_DESCRIPTOR *, int, NvOFGPUBufferHandle *);
    NV_OF_STATUS (*nvOFGPUBufferGetStrideInfo)(NvOFGPUBufferHandle, NV_OF_CUDA_BUFFER_STRIDE_INFO *);
    CUdeviceptr  (*nvOFGPUBufferGetCUdeviceptr)(NvOFGPUBufferHandle);
    NV_OF_STATUS (*nvOFExecute)(NvOFHandle, const NV_OF_EXECUTE_INPUT_PARAMS *, NV_OF_EXECUTE_OUTPUT_PARAMS *);
    NV_OF_STATUS (*nvOFDestroyGPUBufferCuda)(NvOFGPUBufferHandle);
    NV_OF_STATUS (*nvOFDestroy)(NvOFHandle);
} NV_OF_CUDA_API_FUNCTION_LIST;
#endif
EOF

DEFAULT_CFLAGS=(-std=gnu17 -O1 -D_GNU_SOURCE -DHAVE_AV_CONFIG_H -I. -Ilibavfilter -I"$WORK/stub" -I"$WORK/ort-include")

echo
echo "3. compile the default ffmpeg/gu_inputs.h (CUDA and NVOFA) against stub headers (-Werror)"
gcc "${DEFAULT_CFLAGS[@]}" -Wall -Wextra -Werror -Wno-missing-field-initializers -Wno-unused-parameter \
    -c "$REPO/scripts/gu-inputs-test/no_cuda.c" -o "$WORK/with_cuda.o" \
    || { echo "FAIL  the default gu_inputs.h does not compile against the stub headers"; exit 1; }
echo "PASS  the default gu_inputs.h compiles clean against stub headers"

echo
echo "4. the default build against a fake CUDA: call order, and a failing CUDA is an error"
gcc "$WORK/with_cuda.o" libavutil/libavutil.a -ldl -lm -lpthread -o "$WORK/with_cuda" \
    || { echo "FAIL  the test does not link"; exit 1; }
"$WORK/with_cuda" || exit 1

# The patches 0001, 0015 and 0016 touch three files; apply them in order to pristine copies of those.
if command -v git >/dev/null; then
    echo
    echo "5. patches 0001, 0015 and 0016 apply in order to FFmpeg $FFMPEG_VER (git apply)"
    mkdir -p "$WORK/stack-pristine/libavfilter"
    for f in configure libavfilter/Makefile libavfilter/allfilters.c; do
        cp "$SRC/$f" "$WORK/stack-pristine/$f" || exit 2
    done
    cp -r "$WORK/stack-pristine" "$WORK/stack" || exit 2
    (
        cd "$WORK/stack" || exit 2
        for p in 0001-add-oidn-filter-to-build 0015-add-ort-filter-to-build-standalone \
                 0016-add-fsr2-filter-to-build-standalone; do
            git apply "$REPO/ffmpeg/$p.patch" || { echo "FAIL  $p.patch does not apply"; exit 1; }
            echo "PASS  $p.patch applies"
        done
    ) || exit 1
    python3 -I "$REPO/scripts/test_patch_stack.py" --tree "$WORK/stack-pristine" || exit 1
else
    echo "SKIP  git not found: the patch stack was not applied"
fi
