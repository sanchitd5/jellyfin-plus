# Neural super-resolution levels, and the ONNX Runtime backend that carries them

`neural=realesr-anime-x2`, `neural=realesr-anime-x4` and `neural=realesr-general-x4` run a
super-resolution convolutional network over the frame before the libplacebo scaling pass. They are
**advanced, opt-in levels**: off by default, not rungs of the generated quality ladder, and nothing
selects one automatically unless an operator sets `NeuralMaxSourceHeight` ("On an Intel GPU" below).
Read "What these cost" before switching one on - none of them reaches realtime for a single session
on the hardware they were measured on.

## The backend, and why it is ONNX Runtime and not TensorRT

FFmpeg's own DNN module (`libavfilter/dnn`) speaks TensorFlow, OpenVINO and libtorch. None of those
is a good fit for a box that has an NVIDIA display driver and no CUDA toolkit, so a backend had to
be added. Both candidates were sized before either was downloaded, by reading the wheel and tarball
central directories over HTTP range requests rather than by downloading them:

| | download | unpacked | what else it needs |
|---|---|---|---|
| **ONNX Runtime 1.30.0, CUDA 13 build** | 236 MB | 337 MB | cuBLAS 856 MB, cuDNN 9 (the subset used) ~800 MB, cuRAND 169 MB, cudart 10 MB |
| TensorRT 11.3.0.99 (`tensorrt-cu12-libs`) | **4.39 GB** | **6.70 GB** | the same cuBLAS and cuDNN on top |

ONNX Runtime was chosen, and it would still have been chosen with unlimited disk:

* **It is a C API.** `onnxruntime_c_api.h` is one header, and the filter is C, like the rest of
  `libavfilter`. TensorRT's is C++ only.
* **There is no engine build step.** TensorRT compiles a plan for the exact GPU, driver and TRT
  version it will run on. That is a 30-120 second stall the first time a viewer selects a level,
  a cache to invalidate, and a third thing a driver upgrade can break. ONNX Runtime loads the
  `.onnx` and runs.
* **The model file stays a model file.** Anyone can drop a different `.onnx` in the models
  directory and select it; a TensorRT plan is not portable off the machine that built it.
* **The door stays open.** ONNX Runtime ships a TensorRT execution provider in the same package. If
  the throughput below is ever worth chasing, it is a provider swap inside the filter, not a
  rewrite.

Two thirds of the runtime cost is cuBLAS and cuDNN, which TensorRT would also have needed. About
1.8 GB is installed at `/usr/lib/jellyfin-ffmpeg-oidn/ort/lib`.

## The filter

There is no ONNX Runtime filter in FFmpeg, so this repository carries one: `ffmpeg/vf_ort.c`,
LGPL-2.1-or-later like the rest of `libavfilter`. It is a standalone filter rather than a fourth
backend inside `libavfilter/dnn`, deliberately: a DNN backend means implementing the async
`DNNModule` interface and its queues, and it would still not give a 3-channel RGB model that
changes the frame size without further patching `vf_sr`. One new file plus a three-hunk build patch
is the shorter path, and it matches `vf_oidn.c` and `vf_optix.c`, which are in this tree for the
same reason.

| option | values | default | note |
|---|---|---|---|
| `model` | path | *(required)* | the `.onnx` file |
| `device` | `cuda`, `cpu`, `openvino` | `cuda` | falls back to CPU with a warning if the CUDA or OpenVINO provider will not load, rather than failing the session. `openvino` runs on an Intel GPU and needs an ONNX Runtime built with the OpenVINO provider (see "On an Intel GPU") |
| `device_id` | int | `0` | CUDA device index; for `openvino` the GPU number, `0` meaning OpenVINO's `GPU` and `n` meaning `GPU.n`, in OpenVINO's own order |
| `scale` | 0-8 | `0` | `0` asks the model: nothing in an ONNX graph states the scale factor of a super-resolution net, so one 32x32 probe frame is run at configure time and the ratio read off the output |
| `threads` | int | `0` | intra-op threads, only meaningful for the CPU provider |

Accepted pixel format is `gbrpf32le` only. Pack and unpack are slice threaded; inference is
synchronous, one frame at a time, because these models are small enough that queueing machinery
would buy nothing. Output is clamped to [0,1] on the way out - residual SR networks overshoot by
design (about -0.45 to +1.59 was measured on these weights) and that is ringing, not a bug.

An ONNX Runtime carries the CUDA provider or the OpenVINO one, not both, so one patched binary runs
`ort` on one of them: the one it was built for (`ORT_EP` in `scripts/build-ffmpeg.sh`). The filter does
not link OpenVINO, which ONNX Runtime loads itself when the provider is appended, so `vf_ort.c` stays
LGPL-2.1-or-later; OpenVINO is Apache-2.0, which is why the combined binary is GPLv3, as the OIDN build
already is.

One implementation note worth keeping: ONNX Runtime is left to its default thread pool only if you
ask it to be, and its default is one thread per core, each pinned with
`pthread_setaffinity_np`. Inside an LXC container with a restricted cpuset every one of those calls
fails and logs. The filter therefore always sets the intra-op thread count explicitly.

## Where it sits in the chain

```
format=yuv420p, [denoise], format=gbrpf32le, ort=model=..., format=yuv420p, hwupload, libplacebo=w=..:h=.., hwdownload, format=yuv420p
```

After denoise, because a network handed noise turns it into structure. Before `hwupload`, because
the filter takes planar float RGB on the CPU - exactly as `oidn` does, and for the same reason: it
carries no `_vulkan` in its name, which is what the existing routing invariant keys on.

libplacebo then scales whatever the network produced to the size the session actually asked for, so
**the network's own factor never has to match the target ratio**. A x4 model on a 540p source at a
1080p target does not waste its work - it produces 2160p and libplacebo takes it down to 1080p -
but it does pay for pixels the viewer will not see. `realesr-anime-x2` is the one whose factor
matches that case exactly, and it is also the cheapest.

Neural super-resolution and the `sr` shader levels are **separate axes and compose**: the network
runs first, the shader runs inside the scaling pass afterwards. Selecting one does not turn off the
other.

## What these cost

One reading per level, taken in the deployed chain on a 960x540 source at a 1080p target, purely so
the cost hint is a measured number rather than an invented one:

| level | model | fps | vs realtime |
|---|---|---|---|
| *(off)* | - | 265 | 6.09x |
| `realesr-anime-x2` | `realesr-animevideo-x2-fp16.onnx` | **24** | **0.56x** |
| `realesr-anime-x4` | `realesr-animevideov3-x4-fp16.onnx` | **15** | **0.34x** |
| `realesr-general-x4` | `realesr-general-x4v3-fp16.onnx` | **10** | **0.24x** |

Read that plainly. **None of these sustains realtime for one session**, let alone two, and they are
roughly an order of magnitude more expensive than the FSRCNNX shader that ships as the default -
which is itself a super-resolution network, just one small enough to run as a fragment shader
inside the scaling pass. They are here because a level that exists and is labelled honestly is
better than a level that does not exist, which is the same reason NVScaler, OIDN and OptiX are
here. No quality stage will ever select one.

fp16 is what makes even these numbers possible: the same x2 model at fp32 measured 19 fps against
24. The conversion keeps fp32 graph inputs and outputs and casts at the boundary, so the filter
does not need to know.

### What a level costs per source pixel

The three weights are one architecture, Real-ESRGAN's `SRVGGNetCompact`, at three depths, so the cost
per source pixel can be written down rather than only measured. A 3x3 convolution into 64 features,
`num_conv` 3x3 convolutions of 64 to 64, a 3x3 convolution out to `3 x scale^2` channels, a pixel
shuffle: 9 x (3x64 + 64x64 x `num_conv` + 64 x 3 x scale^2) multiply-accumulates (PReLU not counted).

| level | `num_conv` | MACs per source pixel | source pixels per second, measured above | at 854x480 and 24 fps (9.8 M source pixels per second) |
|---|---|---|---|---|
| `realesr-anime-x2` | 16 | 598,464 | 12.4 M | clears it, by scaling (about 30 fps) |
| `realesr-anime-x4` | 16 | 619,200 | 7.8 M | does not (about 19 fps) |
| `realesr-general-x4` | 32 | 1,209,024 | 5.2 M | does not (about 13 fps) |

The MAC columns are arithmetic from the architecture as the code and this file state it, not read back
from the weights. The measured column is the NVIDIA card above, the whole chain (so x4 costs more than
its MACs say: its output is four times the pixels to unpack and scale), at 960x540. The last column
is that rate scaled to a smaller source, which is inferred, not measured at 480p. Nothing here has
been measured on an Intel GPU. The cheapest weight at 480p and 24 fps needs about 5.9 trillion MACs
per second sustained; whether an Arc A380 delivers that through OpenVINO's GPU plugin is not known.
The model licence is unchanged by any of this: Real-ESRGAN is BSD-3-Clause, no weights are added to
this repository, and no new model is introduced.

### On an Intel GPU

`device=openvino` asks ONNX Runtime for its OpenVINO provider on the GPU. The engine asks for it on a
detected Intel server when `libonnxruntime_providers_openvino.so` sits in `ort/lib` (the capability
record's `OpenVinoAvailable`), and asks for `device=cpu`, with the reason in the session record, when
it does not. Every other server's node has no `device` option and so runs CUDA, as before. The record's
`NeuralDevice` is what was asked for; whether ONNX Runtime loaded the provider is only in ffmpeg's log
(`OpenVINO execution provider unavailable ...` or `OpenVINO session could not be created ...`, then
`using CPU`). Checked against the real ONNX Runtime 1.24.1 OpenVINO provider on a machine with no GPU:
`device=openvino` logged `[OpenVINO] Device GPU is not available`, warned, and built a CPU session.
Not checked: any run on an Intel GPU, the compile time of these models on it (OpenVINO compiles per
input shape, and this filter runs a 32x32 probe frame and then the real size, so a session start pays
at least two compiles; there is no model cache yet), and which `GPU.n` is the Arc card on a machine
with an integrated GPU as well.

The **`NeuralMaxSourceHeight`** setting (dashboard, Neural and game upscalers; 0 = off, the default)
routes these levels by source height: at or below it a neural level is used, above it the shader path
runs. The mechanism and the reasoning are in `docs/architecture/GPU_VENDORS.md` 5.4. 480 is the
recommended value on Intel, and unmeasured: with it set, start with `realesr-anime-x2` as the neural
default (the only weight the arithmetic above lets near real time at 480p), and watch the GPU. When
nobody names a level the cheapest installed weight is chosen, on Intel only if the OpenVINO provider
is installed.

## The weights are not in this repository, and you have to export them

No model weights are vendored here. The three levels are exported from the **official Real-ESRGAN
checkpoints**, published by the author of Real-ESRGAN:

| level | upstream checkpoint | sha256 |
|---|---|---|
| `realesr-anime-x2` | `https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.3.0/RealESRGANv2-animevideo-xsx2.pth` | `27985aa2198711ecd72f9bb274ec7b164e018fc9ce2933daaa7c7ab36a2bd3fe` |
| `realesr-anime-x4` | `https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesr-animevideov3.pth` | `b8a8376811077954d82ca3fcf476f1ac3da3e8a68a4f4d71363008000a18b75d` |
| `realesr-general-x4` | `https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesr-general-x4v3.pth` | `8dc7edb9ac80ccdc30c3a5dca6616509367f05fbc184ad95b731f05bece96292` |

Real-ESRGAN is BSD-3-Clause. All three are `SRVGGNetCompact`; the architecture comes from upstream
source (`realesrgan/archs/srvgg_arch.py`), with the `ARCH_REGISTRY` decorator and the `basicsr`
import stripped so it is plain torch, and `out += base` written as `out = out + base` so the traced
graph has no in-place op. The constructor arguments are read out of each state dict rather than
guessed: `num_feat` from `conv0.weight.shape[0]`, `num_conv` from the count of 4-D weights minus
two, `upscale` from `sqrt(last_conv.out_channels / 3)`. All three wrap their weights under a
`params` key.

Export with `torch.onnx.export`, opset 17, `dynamo=False` (torch 2.14's default dynamo exporter
does not take the classic `dynamic_axes` dict), dynamic axes on batch, height and width, then
`onnxsim`. Then convert to fp16 with `onnxconverter_common.float16.convert_float_to_float16(...,
keep_io_types=True, disable_shape_infer=True)` **and clear `graph.value_info` afterwards** - the
converter leaves per-tensor shapes behind that pin the graph to whatever resolution it last saw,
and the filter then fails on the second frame size it meets with "Shape mismatch attempting to
re-use buffer".

Put the results in `/usr/lib/jellyfin-ffmpeg-oidn/models/` under the names in the table above. A
level whose `.onnx` is not there is not listed by the probe, does not appear in the dashboard or
the Advanced row, and cannot be selected.

**SPAN was not shipped.** `github.com/hongyuanyu/SPAN` publishes no releases and no tags, and
carries no weights in its tree; the only weights link in its README is a Google Drive file. That is
not an official plain-HTTP upstream, so no SPAN level exists here rather than one built from a
mirror.

## Building it

Do `OIDN.md`'s steps 1-5 and `OPTIX.md`'s header steps first - this is the third filter in that one
binary, not a separate build. Then:

```bash
# ONNX Runtime, official upstream release, MIT. Match the CUDA major version to the driver:
# nvidia-smi reported CUDA 13.2 here, so the cuda13 build.
curl -LO https://github.com/microsoft/onnxruntime/releases/download/v1.30.0/onnxruntime-linux-x64-gpu_cuda13-1.30.0.tgz
tar xf onnxruntime-linux-x64-gpu_cuda13-1.30.0.tgz          # -> $ORT

cd ffmpeg-8.1.2
cp .../ffmpeg/vf_ort.c libavfilter/
patch -p1 < .../ffmpeg/0003-add-ort-filter-to-build.patch    # apply AFTER 0001 and 0002

./configure ... --enable-libonnxruntime \
  --extra-cflags="... -I$ORT/include" \
  --extra-ldflags="... -L$ORT/lib -Wl,-rpath,/usr/lib/jellyfin-ffmpeg-oidn/ort/lib"
make -j"$(nproc)"
install -D -m755 ffmpeg /usr/lib/jellyfin-ffmpeg-oidn/ffmpeg
```

For an Intel GPU there is a shorter path that needs none of the NVIDIA SDKs, below. The rest of this
section is the CUDA build.

Then the runtime, into `/usr/lib/jellyfin-ffmpeg-oidn/ort/lib`: `libonnxruntime.so.1*`,
`libonnxruntime_providers_cuda.so`, `libonnxruntime_providers_shared.so`, plus `libcudart.so.13`,
`libcublas.so.13`, `libcublasLt.so.13`, `libcurand.so.10` and the cuDNN 9 libraries
(`libcudnn.so.9` and the `graph`, `ops`, `cnn`, `heuristic`, `engines_precompiled`,
`engines_runtime_compiled`, `engines_tensor_ir` modules), from NVIDIA's own wheels
(`nvidia-cublas`, `nvidia-cuda-runtime`, `nvidia-curand`, `nvidia-cudnn-cu13`).

**One thing is easy to miss.** `libonnxruntime_providers_cuda.so` is `dlopen`ed and ships with an
empty `RUNPATH`, so it will not find the CUDA libraries sitting next to it and the filter silently
falls back to the CPU provider. Fix it in place:

```bash
patchelf --set-rpath '$ORIGIN' /usr/lib/jellyfin-ffmpeg-oidn/ort/lib/libonnxruntime_providers_cuda.so
```

Check it:

```bash
/usr/lib/jellyfin-ffmpeg-oidn/ffmpeg -h filter=ort
```

and watch a real encode at `-v verbose` for `CUDA execution provider unavailable`, which is the
line that says it is running on the CPU.

### Building for Intel: `ORT_EP=openvino`

```bash
WITH_ORT=1 ORT_EP=openvino sudo -E ./scripts/build-ffmpeg.sh
```

`ORT_EP` defaults to `cuda`, which is everything above, unchanged. `openvino` builds OIDN and `ort`
and nothing from NVIDIA's SDKs:

* **No OptiX.** Patch 0003 adds `vf_ort` to `configure`, the Makefile and `allfilters.c` with context
  lines patch 0002 (the OptiX filter) added, so on the CUDA path `WITH_ORT=1` needs `WITH_OPTIX=1` and
  with it `optix.h` (NVIDIA EULA), the Optical Flow SDK and an NVPTX clang. That is the patch context,
  not a code dependency: `vf_ort.c` includes nothing from OptiX. `ffmpeg/0015-add-ort-filter-to-build-standalone.patch`
  is the same change against a tree with only 0001 applied; the script uses it for `openvino` and
  refuses `WITH_OPTIX`, `WITH_FSR2`, `WITH_DLSS` and `WITH_MAXINE_VSR` beside it (they need the NVIDIA
  SDKs, and 0004 and 0005 stack on 0003).
* **The runtime** is Intel's `onnxruntime-openvino` wheel (MIT; it carries OpenVINO and oneTBB, Apache-2.0),
  version 1.24.1, downloaded from pypi.org and checked against a pinned sha256. The script unpacks
  only its libraries into `<prefix>/ort/lib`: the ONNX Runtime core, the OpenVINO provider, OpenVINO and
  its Intel GPU plugin. All of them carry an `$ORIGIN` rpath (read with `readelf`), so unlike the CUDA
  provider nothing needs `patchelf`. The core and the provider must be the same ONNX Runtime version, so
  the headers are the same version: `onnxruntime_c_api.h` and the one header it includes come from
  Microsoft's `Microsoft.ML.OnnxRuntime` package on nuget.org (`scripts/fetch-ort-header.py`, each file
  checked against a pinned sha256, and `ORT_API_VERSION` checked against the runtime), or from
  `ORT_SDK=/path/` holding `include/` of that version.
* **The host** needs OpenCL for the Intel GPU plugin (`ocl-icd-libopencl1` and `intel-opencl-icd`; the
  plugin's `NEEDED` list names `libOpenCL.so.1`). The script does not install them.
* **`GPU_VENDOR=intel` is the whole Intel build.** Run on its own, `ORT_EP=openvino` still installs
  FFmpeg's `nv-codec-headers`, the `--enable-ffnvcodec/cuda/nvenc` flags and patches 0006 to 0014 (the
  CUDA hwmap bridge and `transpose_cuda`). `GPU_VENDOR=intel` (GPU_VENDORS.md section 5.7) drops all of
  that except patches 0008 and 0009, adds VA-API, libvpl and libdrm, defaults to `WITH_ORT=1
  ORT_EP=openvino`, and `proxmox-build.sh` with the same variable requires only `oidn` and `ort`:

  ```bash
  GPU_VENDOR=intel sudo -E ./scripts/build-ffmpeg.sh        # PLAN_ONLY=1 prints the plan and stops
  GPU_VENDOR=intel sudo -E ./scripts/proxmox-build.sh --with-ffmpeg
  ```

  Without `GPU_VENDOR=intel`, `proxmox-build.sh` requires all five filters and refuses an
  `ORT_EP=openvino`-only binary; use `build-ffmpeg.sh` directly. The CUDA bridge test is skipped by
  default for `openvino` (there is no CUDA to cross). Not run on an Intel box.
* **One ONNX Runtime per prefix.** The script refuses if `<prefix>/ort/lib` already holds the CUDA
  provider. A server with both vendors builds for the vendor `GpuVendor` selects.

Check it as on the CUDA path, then read a real encode at `-v verbose`:

```bash
/usr/lib/jellyfin-ffmpeg-oidn/ffmpeg -h filter=ort | grep -A4 "device"       # openvino is listed
ls /usr/lib/jellyfin-ffmpeg-oidn/ort/lib/libonnxruntime_providers_openvino.so  # what OpenVinoAvailable looks for
```

Compile check without a GPU or a builder: `scripts/test-vf-ort.sh` compiles `vf_ort.c` against FFmpeg
8.1.2's headers and the ONNX Runtime 1.24.1 header (`-Werror`) and runs its session setup against a
fake ONNX Runtime, checking which provider each `device` value appends and the CPU fallbacks. A native
arm64 container is enough for it; the command is in the script's header. That proves the file compiles
and picks providers, not that the patched tree builds on x86-64 or that a model runs.

## What breaks it

* **A rebuild of the patched binary that forgets `vf_ort.c`** would leave a binary that exists and
  runs but has no `ort` filter. The shim handles this: it asks the binary what filters it has,
  caches the answer against the binary's size and mtime, and strips the node if the filter is
  missing - so those sessions play without the network instead of failing. The same check covers
  `oidn` and `optix`.
* **Deleting `/usr/lib/jellyfin-ffmpeg-oidn`** degrades the same way, as it already did.
* **Deleting a `.onnx`** removes that level from the probe, the dashboard and the Advanced row, and
  a session naming it falls back to the configured default. Nothing fails.
* **No OpenVINO provider, or no OpenCL on the host.** On an Intel server the engine asks for
  `device=cpu` (the record says why) when `libonnxruntime_providers_openvino.so` is absent, and for
  `device=openvino` when it is present; if ONNX Runtime then cannot start OpenVINO on the GPU the filter
  warns and runs on the CPU, which is far from real time. A session that was chosen automatically by
  `NeuralMaxSourceHeight` is only chosen when the provider file is present; a missing OpenCL runtime is
  not visible from the file, so the warning in ffmpeg's log is the signal.
* **An NVIDIA driver upgrade** is the real risk, and it is a smaller one than OptiX's. ONNX Runtime
  talks to cuDNN and cuBLAS, which are bundled here and not in the driver, so only the CUDA driver
  API contract matters: a driver too old for the CUDA 13 runtime would make the CUDA provider fail
  to load, and the filter would warn and run on the CPU, which is unusably slow rather than broken.
  Re-run `-h filter=ort` and one short encode at `-v verbose` after a driver change.
* **A `jellyfin-ffmpeg` upgrade** cannot touch it - this binary is separate - but it can move
  Jellyfin's generated command onto options this build lacks, which would fail these sessions only.
* **A Jellyfin upgrade** that moves the Harmony patch targets disables all enhancement, these
  levels included. Existing failure mode, not a new one.
