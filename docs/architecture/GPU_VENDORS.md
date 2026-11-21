# GPU vendors: running on Intel as well as NVIDIA

Design spec for making the enhancement engine vendor-aware. Written to be implemented as a series of
small pull requests, each one landing on its own branch and verified before the next starts. **PR 1
(detection and a report-only capability model, section 3), PR 2 (the encoder per vendor and Intel
load, sections 5.1 and 5.2), PR 3 (the Vulkan device from the render node, and the NVIDIA-only
levels withheld on Intel, section 5.3), PR 5 (the ONNX neural level on OpenVINO, routing by source
height, and the `ORT_EP` build, section 5.4), PR 6 (`gu_inputs` without CUDA, section 5.5), PR 7
(opt-in Intel hardware decode, section 5.6) and PR 8 (the `GPU_VENDOR=intel` ffmpeg build and the
per-vendor required-filter set, section 5.7) are built; the rest are the plan, with what is known, what
was measured, and what is still a guess marked as such.** (PR 4 was folded into PR 2.) PR 6 was authored
with no compiler available, so none of it has been built or run. PR 7 is written the same way: the C# was not compiled, `scripts/test-gpu-vendor.sh` and the shim test were not run on it, and nothing was run on an Arc card. PR 8's `GPU_VENDOR=intel` build, with or without `WITH_FSR2=1`, has been checked only in plan-only mode. The first four build clean (plugin and patcher) against the Jellyfin 12.4 server assemblies
and `scripts/test-gpu-vendor.sh` passes (1082 checks, plus 11 in `scripts/test-shim-device.py`) in a .NET
10 SDK container with no GPU; `scripts/test-vf-ort.sh` compiles `ffmpeg/vf_ort.c` against FFmpeg 8.1.2
and checks its provider calls (28 checks); `scripts/test_build_ffmpeg_vendor.py` runs both build scripts
in plan-only mode for each vendor (section 5.7; no ffmpeg is built). None has been run on an NVIDIA or an Intel box: the detection is untested
against real sysfs, the Intel command lines (the encoder, the render-node device of PR 3 and the
OpenVINO device of PR 5) have not been run through ffmpeg, the OpenVINO provider has been exercised
only on a machine with no GPU, and the fdinfo parser has been run only against fixtures written from the
kernel documentation, not against output captured from a machine.

Today every GPU-specific decision in the engine assumes NVIDIA. This spec does not change that for
anyone who has an NVIDIA card: with the card present, every ffmpeg command line this plugin builds
stays byte-identical through the whole series. Intel is added beside NVIDIA, never in place of it.

## 1. Goal, and what is out

**Goal.** A server with an Intel Arc GPU (the measured one is an A380) can run the enhancement
chain that NVIDIA servers run today, as far as the hardware allows, and the plugin says plainly
which parts it can and cannot do. A server with both vendors keeps using NVIDIA unless told
otherwise.

**Out of the series.**

- **AMD.** The vendor id is recognised in the device list (it appears as a device with no vendor)
  so it is reported, not claimed. No AMD path.
- **Intel XeSS.** A proprietary SDK, the Intel analogue of DLSS. By this project's rule for
  proprietary SDKs (AGENTS.md, "Reuse before reinvention") it would stay unvendored and
  operator-fetched, and it would sit out of tree like `vf_dlss`'s runtime does. It is not part of
  this series; the capability model leaves room for it and does nothing else.
- **Anything that writes to the library.** AGENTS.md, "The source file is never touched", applies
  unchanged.

## 2. What is NVIDIA-only today

Read from the code at the commit this spec was written against. Each row is something a later PR
has to make vendor-aware, or consciously leave NVIDIA-only.

| Where | What is NVIDIA-specific | Decided in |
|---|---|---|
| `UpscaleEngine.ChooseEncoder` | The negotiated codec maps to `h264_nvenc` / `hevc_nvenc` / `av1_nvenc`; the fallback is `hevc_nvenc`; the `Encoder` setting defaults to `hevc_nvenc` | PR 2 |
| `UpscaleEngine.GuardAv1`, `Av1NvencUsable` | Asks `nvidia-smi --query-gpu=compute_cap` whether AV1 can be encoded (8.9 or newer) | PR 2 |
| `UpscaleEngine.HwaccelArgs` | `-init_hw_device vulkan=vk:0`: the first Vulkan device, whichever GPU that is. Correct on a one-GPU box and on this project's test box; wrong the moment two GPUs are present | PR 3 |
| `BuildChain`, `GpuResidentEncode` | The end of the chain is `hwmap=derive_device=cuda` (Vulkan frame handed to NVENC as a CUDA frame, `ffmpeg/0006`) | PR 2 |
| `GpuLoadController` (`GET /JellyfinPlus/Gpu/Load`) | NVENC session count from `nvidia-smi --query-gpu=encoder.stats.sessionCount` | PR 2 (section 5.2) |
| CUDA-only levels | `optix` denoise, `dlss` / `dlaa`, `dlpp-1..4`, `vsr-rtcuda` need CUDA (and, for DLSS, the NVIDIA runtime the operator fetches); `fsr2` is fed by NVOFA motion vectors through CUDA | Withheld from the probe on a detected Intel GPU, and reported as "needs NVIDIA" (PR 3, section 5.3) |
| `vf_ort` | The ONNX Runtime filter creates a CUDA execution provider and falls back to the CPU one (`ffmpeg/vf_ort.c`, option `device` = `cuda` / `cpu`) | PR 5 |
| `gu_inputs.h` (`vf_fsr2`, `vf_dlss`) | Motion vectors come from NVOFA optical flow (`libnvidia-opticalflow`), through CUDA; `GU_NO_NVOF` builds run with zero flow | PR 6 (section 5.5) |
| Decode | The decoder patches drop `-hwaccel` and `-c:v *_cuvid` so frames reach `hwupload` in system memory; only the CUDA-native levels keep a CUDA hwaccel | PR 7: on a detected Intel GPU the opt-in `IntelHardwareDecode` setting decodes with VA-API instead (section 5.6); every other session is unchanged |
| `scripts/build-ffmpeg.sh`, `proxmox-build.sh` | The patched binary must carry all five of `oidn optix ort fsr2 dlss` or the deploy is refused (AGENTS.md invariant 10); four of them need NVIDIA SDKs | PR 8: a required set per vendor (`scripts/ffmpeg-vendor.sh`), section 5.7 |

What is **not** NVIDIA-specific and so needs no work: the libplacebo chain itself (Vulkan), every
shader SR level (FSRCNNX, Anime4K, RAVU, CuNNy), RCAS, deband, nlmeans_vulkan, the denoise and
deblock CPU filters, the whole search and library half of the plugin.

## 3. PR 1: detection and the capability model (report only)

### 3.1 What it does

Finds out which GPU the server has, records it in a **capability record**, and shows the record in
the places this plugin already reports what the server did. **In PR 1 nothing reads the record to
decide anything** (PR 2 and PR 3 changed that for Intel only, sections 5.1 and 5.3). No command line,
no encoder choice, no level list changes, whatever the record says.

This is deliberate: it is the smallest change that makes the next seven PRs checkable. A later PR
can be reviewed against a record that has been live on real servers, and the claim "NVIDIA command
lines stay byte-identical" is a test (section 3.6), not a promise.

### 3.2 Detection

Implemented in `src/patcher/Transcoding/GpuVendor.cs` (BCL only, no Jellyfin types, so it compiles in
a harness on its own).

1. **Render nodes.** Every `/sys/class/drm/renderD*` directory whose `/dev/dri/<name>` device file
   exists. sysfs lists every GPU on the host, including ones a container was never handed, so a node
   with no device file is ignored. Nodes are taken in numeric order (`renderD129` before
   `renderD1000`).
2. **Vendor.** `/sys/class/drm/renderD*/device/vendor`, read as the kernel prints it:
   `0x10de` is NVIDIA, `0x8086` is Intel. Anything else is listed with no vendor and is not claimed.
3. **Tie-breakers for NVIDIA.** The proprietary driver often exposes no render node (no
   `nvidia_drm` modeset), so an NVIDIA server can show no `0x10de` node at all. NVIDIA is also
   present when `/proc/driver/nvidia` exists, and, only if neither of those answered, when
   `nvidia-smi -L` runs and lists a GPU (3 s limit, run once).
4. **Cached.** The machine is asked once per process: a GPU does not appear mid-run, and the
   transcode path must not spawn a process per session. The record is rebuilt from the cached
   inventory on each call, so a change to the setting applies at the next session.
5. **Never throws.** Any failure is a record with vendor `none` and the reason (AGENTS.md invariant 4).

### 3.3 The setting

`GpuVendor` = `auto` | `nvidia` | `intel` | `none`, default `auto`.

| Value | Meaning |
|---|---|
| `auto` | NVIDIA when it is present (node, `/proc/driver/nvidia`, or `nvidia-smi`), else Intel when an Intel render node is present, else `none`. NVIDIA first, so an existing install, and one that gains an Intel card later, behaves as before. |
| `nvidia`, `intel` | The setting wins over detection. If that vendor was not found the record says so (`Detected` false) and offers no capability. |
| `none` | No vendor-specific path is offered. |

A blank or unknown value is treated as `auto` and the record's reason says it was corrected.

Wired through the five places in CLAUDE.md, "Before you change the config page": the property in
`PluginConfiguration.cs`; the mirror in `UpscaleSettings.cs`; the control on `configPage.html` (the
page's load and save are generic over `data-config`/`data-kind`, so the control is the whole of it);
the probe; and the reporting.

### 3.4 The record

| Field | Meaning |
|---|---|
| `Vendor` | `nvidia`, `intel` or `none`: the vendor the plugin treats the server as having |
| `Setting` | The `GpuVendor` setting, normalised |
| `Detected` | Hardware of that vendor was actually seen. False when the setting named a vendor that was not found |
| `RenderNode` | The vendor's render node, or null (an NVIDIA GPU without `nvidia_drm` has none) |
| `EncoderFamily` | `nvenc`, `vaapi` or `software`: what the vendor's chain would end in. `qsv` is the alternative PR 2 measures against `vaapi` |
| `DecodeType` | `cuda`, `vaapi` or `none`: the hardware decode API the vendor's chain would use once hardware decode is wired. Report only: today's chain still decodes in system memory, except the CUDA-native levels |
| `CudaOnlyLevelsAvailable` | The CUDA-only levels can run: NVIDIA was seen |
| `OpenVinoAvailable` | Intel was seen **and** `libonnxruntime_providers_openvino.so` sits beside the CUDA provider in the patched ffmpeg's `ort/lib` (docs/filters/NEURAL.md) |
| `Reason` | One sentence, shown as written on the dashboard |
| `Devices` | Every render node seen, with PCI vendor id, including vendors the plugin does not handle |

Every flag describes what was **seen**, never what the setting wishes (AGENTS.md invariant 7). The
honest negative cases are `Detected` false with no capability, and `OpenVinoAvailable` false until an
operator has actually installed the provider.

### 3.5 Where it is reported

- **Probe** (`PatcherHost.Levels()`, served as `Levels.Gpu` by `GET /JellyfinPlus/Session/probe`).
  The probe is what the client builds its menu from, so this is where later PRs will filter levels.
- **Status** (`GET /JellyfinPlus/Status`, `Gpu`), and the dashboard's status line.
- **SessionRecord** (`Gpu`), written when the session's command is built, so Recent sessions and
  `GET /JellyfinPlus/Session/{id}` (`Record.Gpu`, and flat `Gpu`) say what the server looked like for
  that session.

### 3.6 Tests

`scripts/test-gpu-vendor.sh <jfbin>` builds `scripts/gpu-vendor-harness`, which compiles the real
patcher sources (so it reaches the internal `UpscaleEngine` and `Plan` as production does) against
the server's DLLs, the pattern AGENTS.md describes for profiling harnesses. It needs no GPU and no
running Jellyfin. It checks:

- the vendor-id mapping (`0x10de`, `0x8086`, case, prefix, padding, AMD not claimed);
- the decision: NVIDIA first on a mixed box, each explicit setting, a chosen vendor that is absent,
  the tie-breakers (driver, `nvidia-smi`) and that `nvidia-smi` is not run when something answered,
  the OpenVINO provider file, an unrecognised setting;
- the real sysfs reader against a fake sysfs tree, including a node with no `/dev` entry and numeric
  node order;
- **report only:** with `GpuVendor` set to each of `nvidia`, `auto`, `intel`, `none` and nonsense,
  `HwaccelArgs` (both forms), `BuildChain` (plain, deband with download, CUDA-native, CUDA-native
  bridged through libplacebo) and `EncoderFor` return the exact strings they returned before the
  setting existed. The expected strings are written out by hand from the code, not computed;
- that the probe, the status route and `SessionRecord` carry the record.

PR 2 extends the same harness (it now compiles the DRM fdinfo reader and the load controller as
well). The NVIDIA strings above are asserted on a machine with no GPU, an NVIDIA one and a mixed one,
for every `GpuVendor` value, and are extended to the encoder choice (client codec to `*_nvenc`, the
default setting, a refused setting, a named QSV encoder on a non-Intel box) and to the session
record's `GpuResidentEncode` and `Pipeline`; the PR 1 goldens are unchanged. Intel is asserted
separately: it picks QSV, falls back to software when ffmpeg lists no QSV encoder, refuses a VA-API
encoder, treats an NVENC setting as auto with the reason, swaps `av1_qsv` after a failed test
encode, and **never emits `nvenc`, `cuda` or `vaapi`** in the encoder, the chain or the device
arguments across every setting, client codec and `GpuResidentEncode` value. The fdinfo reader is
tested on i915 and xe fixtures (`scripts/gpu-vendor-harness/fixtures/`), a fake `/proc` tree with
duplicate descriptors and a second process, and the idle-residency reader on i915, xe (two GTs) and
older-kernel layouts. The load route is called through the real controller and its JSON shape and the
NVIDIA, Intel and mixed-box branches are checked. A mutation check (turning off the Intel gate on the
two decisions) fails seven of these, so they do test what they name.

PR 3 extends it again, in the same two directions. On a no-GPU, an NVIDIA and a mixed machine, for
every `GpuVendor` value, the Vulkan device is still `vulkan=vk:0`, the QSV device is absent, the
Vulkan-denoise probe and the AV1 test-encode command are the strings they were, the render-node test
never runs, the session record carries `vk:0` and no withheld text, and the probe has exactly the 23
keys it had with the level lists `ShaderLibrary` offers (and the status route has no new key). On Intel
it asserts the derived device (never `vk:0`), the fallback to `vk:0` when the test start fails and the
trust-it path when the test is inconclusive, the QSV pin at `renderD136` and above and not at 128 to
135, a node name that is not a plain render node, two-GPU machines in both orders (Intel first and
NVIDIA first, `auto`, `nvidia`, `intel`, two Intel GPUs, an AMD node beside an Intel one), that nothing
Intel emits `nvenc`, `cuda` or `vaapi`, that the probe lists lose exactly the NVIDIA-only levels and
report each as `needs NVIDIA`, that a session asking for `optix`, `dlpp-2` and `dlss` (or defaulted to
them) is enhanced without them, with `GpuWithheld` and the summary saying so, and that the same
sessions on an NVIDIA box run them. A mutation check (the Intel gate off in the device resolution, in
the level withholding, in the probe lists, and the QSV pin range widened) fails 33, 12, 6 and 9 of
these. The shim's `render_node` has its own 11 checks in `scripts/test-shim-device.py`, run by the
script when python3 is present (the SDK container has none, so they were run separately).

PR 5 extends it in the same two directions, plus the C side. With `NeuralMaxSourceHeight` at 0 and no
Intel GPU seen, the `ort` node is the string it was (written out by hand) on a no-GPU, an NVIDIA and
a mixed machine, for every `GpuVendor` value, with the OpenVINO provider file present and absent, and
on an Intel box the setting did not choose; the probe has no new key. On Intel the node carries
`device=openvino` with the provider file and `device=cpu` without it, with the plan's and the record's
device and note and the summary's words; `ShaderLibrary.NeuralFilter` takes only those two values. The
routing is asserted by value on NVIDIA and on Intel with OpenVINO at 240, 360 and 480 (chosen) and 481
to 1080 (shader path), for a level asked for by the session and by the dashboard (runs below the line,
dropped above it, and says so), a viewer who chose Off or upscale Off, a session with nothing to
upscale, a source stored sideways, the master switch, no weights installed, a CUDA-native level, a game
upscaler in force, and Intel without the provider (nothing chosen, said, and a named level still
runs on the CPU); and the probe key appears only above 0. A mutation check (routing on at 0, the
device argument given to every server, OpenVINO assumed present, the boundary moved by one, the
CPU choice allowed, the CUDA-native levels routed, a viewer's Off ignored, the probe key always
present) fails 67, 34, 9, 4, 3, 1, 2 and 17 of these. `scripts/test-vf-ort.sh` compiles `ffmpeg/vf_ort.c` against
FFmpeg 8.1.2's headers and the ONNX Runtime 1.24.1 header (`-Wall -Wextra -Werror`) and runs
`init_session` against a fake `OrtApi` for `device=cuda`, `cpu` and `openvino`, with the append failing
and with the session failing, and checks the CUDA call sequence NVIDIA runs is unchanged.

## 4. The series

Each PR is on its own branch, small, and ends with the check that proves it, in the project's own
terms: a served segment probed with `ffprobe`, the built command read from `journalctl`, and the
negative case (feature off, output untouched). A PR does not start until the one before it is
verified on the real box.

| PR | Scope | Depends on | Proof |
|---|---|---|---|
| 1 | Detection and the report-only capability record (section 3) | none | Harness; the record shown live on an NVIDIA box and an Intel box; command lines unchanged |
| 2 | Encoder per vendor and Intel load (the old PR 2 and PR 4 together, section 5.1 and 5.2): map the negotiated codec to `*_nvenc` or `*_qsv`; a `*_nvenc` setting on an Intel box is treated as auto and said so; Jellyfin's own `*_qsv` choice is kept; `GuardAv1` test-encodes `av1_qsv` on Intel; the GPU-resident encode (`hwmap=derive_device=cuda`) is NVIDIA-only and reported n/a on Intel; `Gpu/Load` gains `vendor`, `encoderSessions`, `gpuBusyPercent` from DRM fdinfo | 1 | Served segment codec and size on each vendor; NVIDIA command byte-identical; busy percentage under a known encode |
| 3 | The Vulkan device from the render node instead of `vk:0`, on Intel only (section 5.3); the QSV device pinned to the same node where the encoder would not find it by itself; the NVIDIA-only levels (`optix`, `dlpp-*`, `vsr-rtcuda`, `fsr2`, `dlss`, `dlaa`) withheld from the probe on Intel and reported as "needs NVIDIA"; `MaxConcurrent` 1 for 4K output on a small Intel GPU recommended in docs, default unchanged | 1, 2 | Command on a two-GPU box names the right device; NVIDIA, `none` and one-GPU commands unchanged; the probe on an Intel box lacks the NVIDIA-only levels |
| 4 | Folded into PR 2 (Intel load via DRM fdinfo, section 5.2) | 1 | see PR 2 |
| 5 | `vf_ort` OpenVINO execution provider (`device=openvino`, CPU with a reason when the provider is absent); `NeuralMaxSourceHeight` (0 = off) routing the ONNX neural levels by source height; `scripts/build-ffmpeg.sh` `ORT_EP=cuda\|openvino` with a standalone build patch that no longer needs the OptiX chain (section 5.4) | 1 (8 only for the required-filter set) | Harness for the routing and the `device` option; `scripts/test-vf-ort.sh` compiles the filter and checks its provider calls; on Intel, a served segment larger than the source with `NeuralDevice` `openvino` |
| 6 | `gu_inputs` without CUDA | 5 | Motion vectors from a non-NVOFA source, compared by the project's temporal measurements |
| 7 | Opt-in Intel hardware decode, with the VA-API to Vulkan derived-device fix | 2, 3 | Frames reach libplacebo from VA-API; off by default |
| 8 | A `GPU_VENDOR=intel` ffmpeg build and a per-vendor required-filter set | 1 | `-filters` lists the intel set; deploy refuses a binary missing one of it |

Order is not strict, but 2 and 3 precede anything that changes a command line, so the first
behaviour change is the smallest one. PR 2 stacks on PR 1's branch, and each later PR on the one
before it, until the series has been run end to end on real hardware.

## 5. Design notes for PRs 2 to 8

### 5.1 PR 2: the encoder per vendor

NVIDIA is unchanged: `*_nvenc`, the `nvidia-smi` AV1 guard, `hwmap=derive_device=cuda` when
`GpuResidentEncode` is on. Every Intel decision below is taken only when the record says the vendor
is `intel` **and** `Detected` is true (an Intel GPU was actually seen). Anything else, `none` and an
Intel setting with no Intel GPU included, keeps today's NVIDIA behaviour exactly, so no install that
works today can be moved by this PR.

**QSV, not VA-API (the open question 1, decided from the jellyfin-ffmpeg source).** The chain this
plugin builds ends in system memory (`hwdownload,format=yuv420p`), and the two Intel encoders differ
in what they accept:

- `libavcodec/qsvenc_hevc.c` (and `qsvenc_h264.c`, `qsvenc_av1.c`) take `nv12`, `p010` and other
  system-memory formats, and `qsvenc_init_session` (`qsvenc.c`) opens its own internal session when
  the encoder is handed neither a frames context nor a device: no `-init_hw_device` is needed.
- `libavcodec/vaapi_encode_h265.c` takes only `AV_PIX_FMT_VAAPI`. Feeding it needs a `hwupload` to a
  VA-API device, but `-filter_hw_device` is `vk` (libplacebo needs the Vulkan device on the graph),
  `hwupload` can only use that one device or derive from it (`libavfilter/vf_hwupload.c`), and a
  VA-API device can be derived only from a DRM device (`hwcontext_vaapi.c`, `vaapi_device_derive`),
  not from Vulkan. Mapping the Vulkan frame back with `hwmap` does not help either: Jellyfin's own
  chains map back with `hwmap=derive_device=vaapi:mode=write:reverse=1`, which returns to the VA-API
  device the frame was first mapped from, and a frame made by `hwupload` has no such parent.

So the Intel encode family is `qsv`, and a `*_vaapi` encoder is **not** emitted: it would be a dead
stream, not a worse picture. When ffmpeg lists no `*_qsv` encoder the family is `software` and the
chain feeds `libx264` / `libx265` / `libsvtav1` (the same system-memory frames), with the reason in
Recent sessions. VA-API encode belongs with PR 7, which has to solve the shared-device problem for
decode in the first place.

**Chain tail.** The tail on Intel is the non-resident NVIDIA one, `hwdownload,format=yuv420p`. It
cannot be `hwdownload,format=nv12`: `hwdownload` yields the sw_format of the Vulkan frames, and
libplacebo's output format defaults to its input's (`vf_libplacebo.c`, `config_output`), which our
own `format=yuv420p,hwupload` fixed as yuv420p. QSV wants `nv12`; ffmpeg's output stage appends a
`format=` filter listing the encoder's pixel formats (`ffmpeg_filter.c`) and the graph inserts a
`scale` for the difference (`avfiltergraph.c`, auto-inserted conversion). The cost is one CPU
yuv420p to nv12 pass per frame, which is not measured. `GpuResidentEncode` is ignored on Intel, the
session record says `GpuResidentEncode` is n/a, and `Pipeline` reads plain `Vulkan`.

**`ChooseEncoder`.** On Intel the negotiated codec maps to `h264_qsv` / `hevc_qsv` / `av1_qsv`
(auto, and the fallback when the configured encoder is refused). A configured `*_nvenc` encoder
(the setting defaults to `hevc_nvenc`) is treated as auto and the encoder reason says so. A
configured `*_vaapi` encoder is refused with a reason. The other configured-encoder guards (ffmpeg
lists it, the client declared its codec) are vendor-independent and unchanged. When Jellyfin has
already chosen the encoder (its own hardware acceleration is set to QSV), a QSV or software choice
of Jellyfin's is kept; one that cannot take this chain (`*_vaapi`, `*_nvenc`) is replaced, with the
reason.

**`GuardAv1`.** `av1_qsv` is listed by any build that compiles it, but only Arc and newer can open
it, so on Intel one frame is test-encoded with `av1_qsv` (once, remembered) and a failure swaps to
`hevc_qsv`, or `h264_qsv` when the client cannot take HEVC. An inconclusive probe (no ffmpeg, a
timeout) leaves the choice alone, as the NVIDIA guard does.

**Not done here, done in PR 3 (section 5.3).** The Vulkan device was still `vk:0`, and the CUDA-only
levels were not withheld from the probe on Intel; PR 3 starts the Vulkan device from the render node and
withholds those levels, with the reason reported.

### 5.2 Intel GPU load via DRM fdinfo (in PR 2)

NVIDIA's `Gpu/Load` counts NVENC sessions from `nvidia-smi`, and that field (`nvencSessions`) stays
exactly as it is. The response gains three additive fields, `vendor`, `encoderSessions` and
`gpuBusyPercent`, plus `gpuIdlePercent` and `loadSource`. On NVIDIA `encoderSessions` equals
`nvencSessions` and the other two are null; the NVIDIA path spawns the same process as before.

Intel has no session counter, but the kernel exports per-client engine time in
`/proc/<pid>/fdinfo/<fd>` of an open DRM file (Documentation/gpu/drm-usage-stats.rst). The two
drivers differ and are read by `drm-driver`:

| Driver | Per-client fields | Classes |
|---|---|---|
| `i915` | `drm-engine-<class>: <n> ns` (busy time), `drm-engine-capacity-<class>: <n>` (engines in the class, printed when more than one) | `render`, `copy`, `video`, `video-enhance` |
| `xe` | `drm-cycles-<class>: <n>` (busy GPU cycles), `drm-total-cycles-<class>: <n>` (the cycles the engine had available) | `rcs`, `bcs`, `vcs`, `vecs`, `ccs` |

Both carry `drm-client-id` and `drm-pdev`; one client can appear on several fds and in several
processes (inherited), so clients are deduplicated by `(drm-pdev, drm-client-id)`. The classes are
mapped to render, copy, video, video-enhance and compute so the maths is one code path.

Busy percentage is the change between two samples: i915 divides the summed busy nanoseconds of a
class by `elapsed x capacity`; xe divides the summed busy cycles by the change in
`drm-total-cycles`. `gpuBusyPercent` is the busiest class (clamped to 0-100). `encoderSessions` is
the number of distinct clients whose video-engine time moved in the window, which on this plugin's
chain (software decode) is encode; a hardware-decoding client would count too, and the doc says so.
Only clients present in both samples are counted, so an encode that starts and ends inside one window
is missed.

Boundaries, all unverified on hardware: Jellyfin runs unprivileged and can read `fdinfo` only of its
own processes, which is what is wanted (its ffmpeg children) but means another container's use of
the GPU is invisible; so a second figure that is not about processes is added when sysfs gives it
without any privilege: `gpuIdlePercent`, the least-idle GT's share of the window spent in RC6
(`gt/gt*/rc6_residency_ms` for i915, `tile*/gt*/gtidle/idle_residency_ms` for xe). RC6 idle is
"the GT slept", not "the engines were busy", so it is reported beside the busy figure, not folded
into it. `intel_gpu_top` and perf are never required. Sampling is two reads (the first call waits
300 ms between them, later calls reuse the previous sample when it is 0.5 to 30 s old), the result is
cached for 5 s and single-flighted, as the NVENC count is, and a failure is null, never an error.

### 5.3 PR 3: the Vulkan device from the render node, and the NVIDIA-only levels on Intel

Two changes, both taken only on the Intel path (`UpscaleEngine.IsIntelPath`: vendor `intel` and an
Intel GPU seen). NVIDIA, `none` and an Intel setting with no Intel GPU behind it keep every string
they have today, byte for byte (section 7), so this PR does not touch the NVIDIA device choice
either: on a box with both vendors and `GpuVendor` `auto` the chain is NVIDIA's and the device stays
`vk:0` exactly as before (set `GpuVendor` to `intel` to choose the Intel card). That leaves the
existing wrong-GPU risk on a mixed NVIDIA box where the Vulkan loader lists the Intel card first; it
is not made worse, and is left for a change that is allowed to alter an NVIDIA command line.

**The device, decided from the jellyfin-ffmpeg 8.1.3 source (this answers open question 2).** The
pinned build can start the Vulkan device from a DRM render node:

```
-init_hw_device drm=dr:/dev/dri/renderD129 -init_hw_device vulkan=vk@dr -filter_hw_device vk
```

- `fftools/ffmpeg_hw.c`, `hw_device_init_from_string`: `type=name:device` creates a device from a
  path and `type=name@source` derives one from a named device.
- `libavutil/hwcontext_drm.c`, `drm_device_create`: opens the path read-write and asks libdrm for its
  version; a path that is not a DRM node is an error.
- `libavutil/hwcontext_vulkan.c`, `vulkan_device_derive`, case `AV_HWDEVICE_TYPE_DRM` (compiled in
  with libdrm: jellyfin-ffmpeg's `debian/rules` passes `--enable-libdrm`, as does the Linux builder's
  libdrm script): it reads the node's PCI device id with `drmGetDevice` and hands it to
  `find_device`, which picks the Vulkan physical device whose `deviceID` matches.
- Jellyfin's own server code builds the same shape for its Vulkan path: `MediaBrowser.Controller.dll`
  (12.4) carries the strings `-init_hw_device drm={0}:{1}` and `-init_hw_device vulkan={0}{1}`, the
  names `dr`, `vk` and `qs`, and `/dev/dri/renderD128` as its default node. That is read from the
  assembly's string table, not seen in a run.

Two details of that source matter and are worth stating because they are not what the code's own
comments suggest. First, the selection is **by PCI device id, not by node**: `vulkan_device_derive`
also records the node's major and minor from `fstat` of the open descriptor (`st_dev`, which for a
device file is the filesystem's device, not the node's `st_rdev`), but `find_device` only consults
that when `VK_EXT_physical_device_drm` is already enabled, and that is a device extension
(`optional_device_exts`) enabled after `find_device` has chosen, so the branch is skipped and the PCI
id decides. Two GPUs of different models are told apart correctly; **two identical cards (two A380s)
are not**: the first in Vulkan's order is taken whichever node was asked for. Second, a derive that
cannot find a device is an error that ends the session's ffmpeg, a dead stream, not a degraded one.

So the Intel device is checked before it is used. The first time it is needed the engine runs the
stock ffmpeg once with exactly this device initialisation and a one-frame `hwupload,hwdownload`
(the pattern `Av1QsvUsable` and `VulkanDenoiseRunsOnPatchedBinary` already follow), remembers the
answer, and reports it. A failed check falls back to `vk:0` (today's behaviour, so this PR cannot
turn a working Intel box into a dead one) and says so in the session record; an inconclusive check (no
ffmpeg found, a timeout) trusts the render node, since nothing else would run either. When the record
has no usable render node path, the argument is `vk:0`. The node text is only ever the detector's own
`/dev/dri/renderD<digits>`; anything else is not put on a command line.

**The QSV device (also decided from the source).** Nothing needs to be added in the common case. With
no QSV device on the command line, `qsvenc_init_session` opens an internal session, and
`ff_qsv_set_display_handle` (`libavcodec/qsv.c`) creates a VA-API device with `vendor_id=0x8086`;
`vaapi_device_create` then walks `/dev/dri/renderD128` to `renderD135` (`max_devices = 8`) and takes
the first node of that vendor. The detector's render node is the first Intel node in numeric order,
so the two agree for every node from 128 to 135, which is every machine that is not unusual. Above
that the implicit search never reaches the node, so there (and only there, and only when ffmpeg lists
a QSV encoder) the arguments gain `-init_hw_device qsv=qs:hw_any,child_device=<node>`, which is what
`-qsv_device <node>` expands to (`opt_qsv_device`). The encoder takes the one QSV device on the
command line (`hw_device_setup_for_encode`, `HW_CONFIG_ENCODER_DEVICE(NV12, QSV)`). The one-GPU
command is therefore unchanged from PR 2 apart from the Vulkan device. What this does not do is let
an operator choose between two Intel GPUs: the first Intel node wins, as it does in the detector.

**Where it applies.** One function builds the device arguments and both the session command
(`HwaccelArgs`) and the two engine probes that start a Vulkan or QSV device use it, so a probe tests
the device a session will use. The shim fallback reads an optional `render_node` from its JSON
(unset keeps `vulkan=vk:0`); it has no vendor detection and no probe, so it is for an operator who
knows the node.

**The NVIDIA-only levels.** On the Intel path these are not offered and are not run, and the reason
is stated, never silent:

| Axis | Withheld | Why |
|---|---|---|
| Denoise | `optix`, `optix-temporal` (and the unlisted `optix-hdr`) | OptiX needs CUDA frames |
| Neural | `dlpp-1` to `dlpp-4`, `vsr-rtcuda` (and the retired `vsr`) | NVIDIA RTX DLPP / VSR libraries over CUDA |
| Game | `dlss`, `dlaa`; and `fsr2` unless the ffmpeg is the PR 6 CUDA-free build | DLSS is NVIDIA's; `fsr2` is fed by NVOFA motion vectors through CUDA (`gu_inputs.h`), and is offered on Intel only when the build left the `fsr2-nocuda` marker (section 5.5) |

Each is removed from the probe lists (`Levels.Denoise`, `Neural`, `Game`, `GameOptionLevels` and the
label maps), so the client, which builds its menu from the probe, never offers it. A server whose
dashboard default or a stale client still asks for one gets `off` for that axis and the session says
so: `SessionRecord.GpuWithheld` and a sentence in the summary ("... NOT run, needs NVIDIA ..."), with
`NeuralRequested` still naming what was asked. The probe carries `Levels.Withheld` (axis, level,
`needs NVIDIA`) and the status route the same, **only on the Intel path**, so the NVIDIA probe and
status are unchanged, and the dashboard's GPU line lists them. Left alone on purpose: the ONNX
neural levels (`ort`) fall back to ONNX Runtime's CPU provider without CUDA, which is not CUDA-required
but is almost certainly not real time; PR 5 owns that (OpenVINO and `NeuralMaxSourceHeight`), and until
then the record's neural description says CUDA is not what ran on Intel.

**Recommended in documentation only: `MaxConcurrent` 1 for 4K output on a small Intel GPU.** The one
measurement there is (section 6) is a single session, 1080p to 2160p at 38 to 40 fps on an A380.
That is a margin over real time for one session, nothing is known for two, and the default of 4 would
admit four. The setting is not changed.

### 5.4 PR 5: `vf_ort` on OpenVINO, and routing by source height

(This is the fourth pull request of the series upstream, because PR 4 was folded into PR 2; the
numbering here is the table's.) Three changes. NVIDIA is not touched: with no Intel GPU seen every
string the engine builds stays what it was (section 7), and `NeuralMaxSourceHeight` at its default of
0 changes nothing on any server.

**The execution provider.** ONNX Runtime has an OpenVINO execution provider that runs on Intel GPUs.
Decided from the ORT 1.24.1 C header and the `onnxruntime-openvino` 1.24.1 wheel (both read, neither
run):

- The C API appends it with `SessionOptionsAppendExecutionProvider_OpenVINO_V2(options, keys, values,
  n)` (`\since 1.17`), a string map; `device_type` takes `GPU` (OpenVINO's first GPU) or `GPU.<n>`.
  No OpenVINO header is needed to build the filter, only `onnxruntime_c_api.h`.
- `vf_ort.c` gains `device=openvino` beside `cuda` and `cpu`. `device_id` already exists for CUDA; for
  OpenVINO `0` is `GPU` and `n` is `GPU.<n>`. The engine does not pass it: OpenVINO numbers GPUs in
  its own order and nothing here maps a render node to that number, so on a box with an integrated
  GPU and an Arc card `GPU` may be the integrated one. **Unverified**, and the reason Intel-with-two-
  GPUs stays "first node wins" (section 5.3).
- The filter's contract does not change: warn and run on the CPU rather than fail the session. That
  covers the provider failing to append and also the session failing to build with it, in which case
  the filter builds a fresh session on the CPU provider once and says so in the log. Run against the
  real 1.24.1 provider (x86-64 under emulation on an arm64 host, so no GPU), the failure is at the append (`[OpenVINO] Device GPU
  is not available`), so that is the path that has been seen; the second, in `CreateSession` (a model
  the provider cannot compile for the device), is covered by a fake and has not been seen. The CUDA
  branch is not edited.
- The provider is a separate shared object (`libonnxruntime_providers_openvino.so`) that ONNX Runtime
  loads at run time, so `vf_ort.c` does not link OpenVINO. OpenVINO is Apache-2.0, which is why the
  combined binary is GPLv3 (the OIDN-linking build already is, `scripts/build-ffmpeg.sh` header, and
  AGENTS.md "Apache-2.0 is usable only because this is GPLv2-or-later"); `vf_ort.c` stays LGPL-2.1-or-
  later and says where the provider comes from.

**Which device a session asks for.** Only on the Intel path (`IsIntelPath`):

| Intel GPU seen and | The node the engine builds | The record says |
|---|---|---|
| the OpenVINO provider file is present (`OpenVinoAvailable`) | `ort=model=...:device=openvino` | `NeuralDevice` `openvino` |
| it is not | `ort=model=...:device=cpu` | `NeuralDevice` `cpu` and why: the provider is not installed, ONNX Runtime's CPU provider runs, not measured for this chain |

Any other server's node carries no `device` option, as today (the filter's default is CUDA). Today an
Intel server also ends on the CPU, but by way of a failed CUDA attempt and a warning; saying `cpu` is
the honest statement of the same outcome and spares the attempt. What the engine records is what it
asked for: whether ONNX Runtime really loaded the provider is visible only in ffmpeg's log (`OpenVINO
execution provider unavailable`), and the record says "requested" rather than claim it.

**`NeuralMaxSourceHeight`** (new setting; 0 = off, the default, so behaviour is unchanged; plugin
setting, patcher mirror, the probe when set, the dashboard). The owner's requirement is to use neural
upscaling when the resolution is really low; this is where the line is drawn. With it above 0 the
session's **source height** decides, and only for the ONNX levels (`realesr-*`, the `ort` filter); the
CUDA-native levels (`dlpp-*`, `vsr-rtcuda`) are not routed:

- source height at or below it: the neural level is used. It is the session's own choice or the
  dashboard's `NeuralLevel` when there is one. When neither names a level (and the viewer did not pick
  Off, and the upscale itself is on, and no game upscaler is in force), the cheapest installed weight
  is chosen (`realesr-anime-x2`, then `-anime-x4`, then `-general-x4`, with the ratio substitution
  `NeuralForRatio` already does). That is the one place the engine picks a network for a viewer, and
  it only happens because an operator set this value. The record says it was chosen, in
  `NeuralRouting`, and `NeuralRequested` stays `off`: the viewer did not ask.
- **Not on the CPU by choice.** On the Intel path this automatic choice needs the OpenVINO provider
  (`OpenVinoAvailable`). Without it nothing is chosen and `NeuralRouting` says why, because a network
  on the CPU is far from real time and choosing it for a viewer is a dead stream, not a worse picture.
  A level the session or the dashboard asked for by name still runs on the CPU as before, with the
  device reason.
- source height above it: an ONNX neural level that was asked for is not run, the shader path the
  ladder offers is what the session gets, and `NeuralRouting` says so ("neural X not run: source Np is
  taller than NeuralMaxSourceHeight M"). `NeuralRequested` keeps naming what was asked.

The height is the upright source height (`Plan.SourceHeight`, after rotation), so a portrait phone
clip is judged on its long edge, like `MaxSourceHeight`'s portrait rule. 480 is the recommended value
on Intel, recorded as a recommendation: it is the owner's, it is not measured, and section 6 has no
neural row.

**What a neural level costs per source pixel, and why 480.** The three weights are Real-ESRGAN's
`SRVGGNetCompact`, BSD-3-Clause, exported from the official checkpoints (docs/filters/NEURAL.md; no
weights are added to this repository). A compact network's cost is per **source** pixel and fixed by
its shape: one 3x3 convolution into 64 features, `num_conv` 3x3 convolutions of 64 to 64, one 3x3
convolution out to `3 x scale^2` channels, then a pixel shuffle. That is 9 x (3x64 + 64x64 x
`num_conv` + 64 x 3 x scale^2) multiply-accumulates (MACs) per source pixel (PReLU not counted):

| Weight | `num_conv` | MACs per source pixel | Measured on NVIDIA, 960x540 source to 1080p (NEURAL.md) | Source pixels per second |
|---|---|---|---|---|
| `realesr-anime-x2` | 16 | 598,464 | 24 fps | 12.4 M |
| `realesr-anime-x4` | 16 | 619,200 | 15 fps | 7.8 M |
| `realesr-general-x4` | 32 | 1,209,024 | 10 fps | 5.2 M |

The MAC counts are arithmetic from the published architecture (`num_conv` 16, 16 and 32 as the code
comments in `ShaderLibrary` and NEURAL.md state them), not read back from the weights in this series.
The measured column is from the NVIDIA card the project was built on, the whole chain, with the
unpack and scaling that come with a larger output, which is why x4 costs more than its MACs say. Real
time at 24 fps from an 854x480 source is 9.8 M source pixels per second. Scaling the NVIDIA numbers
(inferred, not measured at 480p): `realesr-anime-x2` clears it (about 30 fps), `-anime-x4` and
`-general-x4` do not (about 19 and 13 fps). So 480 is a ceiling that only the x2 weight could meet even
on that card. **Nothing is measured on the A380.** At 480p and 24 fps the x2 weight needs about 5.9
trillion MACs per second sustained; whether an A380 delivers that through OpenVINO's GPU plugin is the
measurement this series still lacks, and until it exists the default stays 0 (section 7, "Unmeasured
ships off") and the docs say to start with `realesr-anime-x2` as the `NeuralLevel` and measure.

**The build.** `scripts/build-ffmpeg.sh` gains `ORT_EP=cuda|openvino` (default `cuda`, which is
today's path, unedited).

- *The coupling.* Patch 0003 adds `vf_ort` to `configure`, the Makefile and `allfilters.c` with
  context lines patch 0002 (the OptiX filter) added, so `WITH_ORT=1` died unless `WITH_OPTIX=1`, which
  in turn needs `optix.h` (NVIDIA EULA) and the Optical Flow headers. That is a patch-context
  accident, not a code dependency: `vf_ort.c` includes nothing OptiX. `ORT_EP=openvino` applies a new
  `ffmpeg/0015-add-ort-filter-to-build-standalone.patch`, written against the tree with only 0001
  applied (the script always applies 0001 first), so it needs no OptiX SDK, no Optical Flow SDK and no
  NVPTX clang for ONNX Runtime. 0003 is not changed. `ORT_EP=openvino` refuses `WITH_OPTIX`,
  `WITH_DLSS` and `WITH_MAXINE_VSR` (they need the NVIDIA SDKs, and 0004 and 0005 stack on 0003);
  `WITH_FSR2=1` is allowed since PR 6 (section 5.5).
- *The runtime.* The OpenVINO provider is not in Microsoft's release tarballs; Intel publishes it in
  the `onnxruntime-openvino` wheel (MIT), which carries `libonnxruntime.so.1.24.1`, the provider, the
  OpenVINO runtime (Apache-2.0), the Intel GPU plugin and oneTBB, all with `RPATH` `$ORIGIN` (read with
  `readelf`; unlike the CUDA provider no `patchelf` fix is needed). The script downloads that wheel, pinned
  by version and checked against its sha256, and stages the libraries under `<prefix>/ort/lib`, the
  directory the binary's rpath and `OpenVinoAvailable` already name. The core and the provider must be
  the same ORT version, so the headers come from the same version too: the script fetches the
  `Microsoft.ML.OnnxRuntime` 1.24.1 package from nuget.org, takes only `onnxruntime_c_api.h` and the
  one header it includes, by range request (`scripts/fetch-ort-header.py`, a sha256 per file), and
  checks `ORT_API_VERSION` against the runtime. One ORT per binary: the
  CUDA build and the OpenVINO build are different ORT cores, so a server with both vendors builds for
  the one it uses (`GpuVendor`).
- *What still needs NVIDIA headers.* `nv-codec-headers` (FFmpeg's own MIT headers; the driver is
  `dlopen`ed at run time, so no NVIDIA SDK and no card), the `--enable-ffnvcodec --enable-cuda
  --enable-nvenc` configure flags and patches 0006 to 0014 were unconditional in the script when
  PR 5 was written. PR 8's `GPU_VENDOR=intel` takes them out of an Intel build, except 0008 and 0009
  (section 5.7), together with the per-vendor required-filter set (invariant 10).
- The script did not checksum any download before this; only the new ones are checked here, and the
  FFmpeg tarball's sha256 is not pinned by this PR.
- Intel's GPU plugin also needs the host's OpenCL stack (`libOpenCL.so.1`, from `ocl-icd-libopencl1`,
  and the Intel compute runtime, `intel-opencl-icd`); the plugin's `NEEDED` list names it. That is a
  host package, not something the script stages.

**Tests.** The harness (`scripts/gpu-vendor-harness`) asserts the routing and the device by value,
with the NVIDIA goldens alongside: the `ort` node on NVIDIA, no GPU, a mixed box and an Intel box
with `GpuVendor` set to something else is byte-identical to today's; on Intel it carries
`device=openvino` with the provider file and `device=cpu` without it; the routing table by source
height and setting (0, below, at, above; a session choice, the dashboard default, an explicit Off, the
CUDA-native levels, a game level in force, no weights); the record's reasons; the probe key when
the setting is on and its absence when it is 0. For the C side, `scripts/test-vf-ort.sh` compiles
`ffmpeg/vf_ort.c` against FFmpeg 8.1.2's own headers and the ONNX Runtime 1.24.1 C header inside a
container (a native arm64 container is enough for a compile-only check; it says nothing about x86
code generation or about running a model), then links the filter's session setup against a fake
`OrtApi` and checks which provider calls it makes for each `device` value and that an OpenVINO
failure builds a CPU session instead of ending the filter. With `ORT_LIB_DIR` and `ORT_MODEL` set it
also runs the same setup against a real ONNX Runtime and model (`scripts/vf-ort-test/provider_real.c`),
which is the line to read on the Intel server: `device_name=openvino` there, `device_name=cpu` after a
warning anywhere the GPU is not usable. It has been run only with no GPU: OpenVINO's CPU device accepted the
append and built a session, and `device=openvino` fell back to the CPU as above.

### 5.5 PR 6: `gu_inputs` without CUDA

FSR2 and DLSS are fed by `gu_inputs.h`, which synthesises four things a renderer would supply.
Depth comes from an ONNX model (so it follows PR 5 onto OpenVINO), jitter is a CPU phase
correlation, the reactive mask derives from flow consistency, and **motion vectors come from NVOFA**,
the NVIDIA fixed-function optical-flow block, through CUDA. That one dependency is what ties
`vf_fsr2` to NVIDIA although FSR2 itself is vendor-neutral. DLSS needs NVIDIA regardless.

**Decision: no new flow source.** A CPU block-matching search or an optical-flow network would be a
new motion estimator, and AGENTS.md says per-frame metrics cannot judge one (still-pixel variation
across frames is the measurement, and it has not been made). So PR 6 does not write one. It makes
`gu_inputs.h` run without CUDA, with the inputs it can still honestly produce, and offers only the
level that can use them:

| Input | Without CUDA | How |
|---|---|---|
| Motion vectors | zero | no optical flow, so the upscaler is told nothing moved |
| Reactive mask | empty | it derives from flow consistency, and there is no flow |
| Jitter | measured | CPU phase correlation, as on NVIDIA |
| Depth | model (OpenVINO, PR 5) or flat 0.5 | unchanged; flat when no model is configured |

`gu_inputs.h` has a compile switch, `GU_NO_NVOF`, that removes every CUDA and NVOFA line (the
`ffnvcodec` and Optical Flow headers, the CUDA context, the NVOFA session, the flow and reactive code),
so the build needs neither nv-codec-headers nor the Optical Flow SDK. Without the switch the NVIDIA path
is the one it was, plus one change: a CUDA that will not load or start (no `libcuda`, `cuInit` fails)
now logs a warning and runs with zero flow, as a missing NVOFA already did, instead of failing the
filter. Zero motion vectors are a known failure, not a noisy one, and the session record says so
(`GameMotionNote`, and a sentence in the summary): with them FSR2 behaves close to a spatial upscaler
that also accumulates history, so ghosting on moving content is possible and is **not measured**.

**The build.** `ORT_EP=openvino WITH_FSR2=1 scripts/build-ffmpeg.sh` is now allowed (it still refuses
`WITH_OPTIX`, `WITH_DLSS` and `WITH_MAXINE_VSR` there). `0004-add-fsr2-gu-inputs.patch` stacks on the
OptiX patches, so an OpenVINO build applies the standalone
`0016-add-fsr2-filter-to-build-standalone.patch` instead (the same shape as 0015 is for `ort`), which
registers `fsr2` with no `ffnvcodec` dependency, and compiles with `-DGU_NO_NVOF`. The build leaves a
marker, `fsr2-nocuda`, beside the models directory's parent, as the OpenVINO build leaves its provider.
`GPU_VENDOR=intel WITH_FSR2=1` takes the same path (section 5.7).

**The plugin.** `GpuCapability.NoCudaGameAvailable` is true only when an Intel GPU was seen and that
marker exists. Then, on Intel, `fsr2` is offered and runs; `dlss` and `dlaa` stay withheld ("needs
NVIDIA"), and without the marker all three stay withheld as PR 3 left them. NVIDIA, no-GPU and
mixed-box command lines and probes are unchanged.

**Not verified.** Nothing here has been compiled with a real toolchain or run on an Intel GPU; the
0016 patch has not been applied to a real FFmpeg tree; whether FSR2 with zero motion vectors looks
acceptable has not been looked at. `scripts/test-gu-inputs.sh` is the compile-and-run check for the
CUDA-free variant, and also runs the default variant against stub `ffnvcodec` and Optical Flow headers and
a fake CUDA (the call order, and a CUDA failing at load, `cuInit` or the context push). The stubs prove the
CUDA call order and the degrade path, not that the real SDK headers compile and not the NVOFA calls; a
`WITH_FSR2=1` NVIDIA build is that check.

### 5.6 PR 7: opt-in Intel hardware decode, and the VA-API to Vulkan fix

Hardware decode is off on both vendors today: the decoder patches exist so frames reach `hwupload`
in system memory. On Intel that costs a full-resolution copy per frame that the iGPU/dGPU decoder
could avoid. The attempt on the A380 **failed**: decode to VA-API frames, then `hwmap` into Vulkan,
did not work, and software decode was used. The cause was not isolated.

The hypothesis for the fix, **unverified**: the VA-API device and the Vulkan device were created
independently, so FFmpeg had two unrelated device contexts for the same GPU and the DRM-PRIME
import between them had no shared device to import into. Deriving one from the other
(`-init_hw_device vaapi=va:<render node>` then `-init_hw_device vulkan=vk@va`, or
`hwmap=derive_device=vulkan` from the VA-API frame) makes them share the physical device, which is
what a working Vulkan from DRM-PRIME mapping needs. PR 7 starts by reproducing the failure with the
exact command and the FFmpeg error text, since the cause above is a guess. It stays **opt-in**
(a setting, default off) until it has been verified on a served segment, because an unverified
hardware decode path is a dead stream for a viewer, not a worse picture.

**What PR 7 ships. NOT VERIFIED ON HARDWARE: the C# was not compiled, the harness and the shim test were
not run on it, and no Arc card was available.**

The setting `IntelHardwareDecode` (dashboard checkbox, default off, in `PluginConfiguration`,
`UpscaleSettings`, `configPage.html`, the probe and the session record). Hardware decode applies only
when ALL of these hold; otherwise the command is the software one and the session record says which
condition failed (`HardwareDecodeReason`):

1. the setting is on;
2. an Intel GPU is detected (`Devices().Intel`, with a render node that has a usable path);
3. the source is upright 8-bit SDR progressive h264 or hevc (anything Jellyfin would tone-map,
   deinterlace or rotate adds a CPU-side node);
4. the plan has no CPU-side node: no CUDA neural, CPU deblock, CPU denoise (`atadenoise`), neural or game
   step, and a Vulkan denoise only if the patched binary can compile its shader;
5. the start-up test passes: one synthetic frame uploaded to a VA surface, mapped to Vulkan and
   downloaded on the patched binary (`HardwareDecodeProbeArgs`), once per render node and remembered
   whatever the answer. **This is an approximation:** it exercises the VA-API to Vulkan map, not a
   real decode, so a passing test does not prove the decoder works.

When all hold the command is
`-init_hw_device vaapi=va:<node> -init_hw_device vulkan=vk@va [QSV device] -hwaccel vaapi
-hwaccel_output_format vaapi -filter_hw_device vk` and the chain is
`[setparams=range=tv,]hwmap=derive_device=vulkan,[Vulkan deblock/denoise,]libplacebo...,hwdownload,format=nv12`:
no `format=yuv420p`, no `hwupload`. Nodes Jellyfin keeps (subtitle overlay and the like) are placed after
the chain tail rather than before it.

Premises inferred from the source and not seen to work (P3 to P6): that `ffmpeg/0008` and `0009` make
the VA-API to Vulkan `hwmap` negotiate; that `vk@va` derives a device the decoder's frames can be mapped
into; that VA-API frames cannot enter a CPU node (hence the plan condition); and that the downloaded
format is `nv12` (one constant, `HardwareDecodeFormat`).

The shim: stock jellyfin-ffmpeg failed `hwmap=derive_device=vulkan` from VA-API ("Conversion failed!"
on the A380), and `0008`/`0009` exist only in the patched binary, so `pick_binary` routes a command
carrying `vulkan=vk@va` and `derive_device=vulkan` to the patched binary
(`scripts/test-shim-device.py`). That is a routing change beyond the setting itself.

Byte-identical, by harness: with the setting off, or on without a detected Intel GPU (NVIDIA, no GPU,
mixed auto), every command line, the probe JSON and the session JSON are what they were; the probe key
`IntelHardwareDecode` and the record keys `HardwareDecode` and `HardwareDecodeReason` exist only
while the setting is on.

### 5.7 PR 8: a `GPU_VENDOR=intel` build, and the per-vendor required filters

`scripts/build-ffmpeg.sh` and `scripts/proxmox-build.sh` take `GPU_VENDOR=nvidia|intel` (default
`nvidia`; anything else is refused by both scripts). With `nvidia` the configure line, the apt
list, the patch order and the verify step are what they were before, and the test below compares the
plan against a golden to keep it so.

**What `intel` builds.** The same FFmpeg and the same `vf_oidn` and `vf_ort`, plus:

- apt `libva-dev libvpl-dev libdrm-dev` (the oneVPL dispatcher, `libvpl-dev` 2023.3.0 or newer, is
  `vpl >= 2.6` to FFmpeg's configure; `--enable-libmfx` and `--enable-libvpl` cannot be combined, so
  only the latter is used) and configure `--enable-vaapi --enable-libvpl --enable-libdrm`. libdrm is
  there for PR 3's Vulkan device from the render node (`drm=dr:<node>`, then `vulkan=vk@dr`).
- `WITH_ORT=1` and `ORT_EP=openvino` by default (section 5.4).
- No `nv-codec-headers`, no `--enable-ffnvcodec/cuda/cuvid/nvdec/nvenc`, no CUDA-LLVM flags, no
  patches 0006, 0007 and 0010 to 0014 (the CUDA encode and filter patches), no `transpose_cuda`
  check and no bridge test.
- With `WITH_FSR2=1` (default off: it needs the out-of-tree FSR2 SDK build, `FSR2_SDK` and `FSR2_LIB`),
  the `fsr2` filter through PR 6's path: patch 0016 instead of 0004, `-DGU_NO_NVOF`, and the
  `fsr2-nocuda` marker (section 5.5).

**Why 0008 and 0009 stay on Intel.** Both touch only `libavfilter/vf_hwmap.c`. 0008 takes the output
format of a chained `hwmap` from `hwframes_ref`; 0009 pins the formats of `derive_device=vulkan` and
`derive_device=cuda` and falls through to the stock `ff_all_formats` for any other device type. They
are format-negotiation fixes for a `hwmap` chain, which is the shape PR 7's VA-API to Vulkan decode
needs, so they are kept rather than dropped and re-added later. **That is unverified on Intel:** no
Intel box has built or run this binary. If PR 7 shows they change nothing for a VA-API chain, or
they get in the way, removing them is a one-line change to the `intel` patch list in `build-ffmpeg.sh`.

**The invariant.** AGENTS.md invariant 10 required all five of `oidn optix ort fsr2 dlss` to survive a
rebuild and made `proxmox-build.sh` refuse a binary missing any. Four of the five need NVIDIA SDKs,
so an Intel binary can never satisfy it. It is now a **required set per vendor**, one list in
`scripts/ffmpeg-vendor.sh` that both scripts source (NVIDIA: the five; Intel: `oidn ort`), and
`proxmox-build.sh` adds `fsr2` for Intel when `WITH_FSR2=1`, as it adds `vsr` for Maxine.
AGENTS.md invariant 10 and CLAUDE.md say so in the same change. That is an amendment to an invariant
and so the maintainer's call, flagged in the pull request. The shim's per-invocation probe of the
binary's filter list already strips nodes the binary lacks, so a binary with fewer filters degrades
rather than failing, which is what the amended invariant relies on.

**Refusals.** On `intel` the script stops, naming the variable, for `WITH_OPTIX=1`, `WITH_DLSS=1`,
`WITH_MAXINE_VSR=1`, `WITH_RTXDLPP=1`, `WITH_RTXVSR=1`, `ORT_EP=cuda`, `WITH_ORT=0`, `WITH_OIDN=0` and
`BRIDGE_TEST=1`: each would either need an NVIDIA SDK or would drop a filter the Intel set requires.
`WITH_FSR2=1` is not refused (PR 6 made it build without CUDA); it still checks `FSR2_SDK`/`FSR2_LIB`
and `WITH_ORT=1` first. `proxmox-build.sh` does not turn the NVIDIA-only switches on for `intel`; it
checks the built binary's `-filters` against the vendor's set and restores `<binary>.prev` on a miss,
as before.

**`PLAN_ONLY=1`.** `GPU_VENDOR=intel PLAN_ONLY=1 scripts/build-ffmpeg.sh` runs the preflight, skips the
root and disk checks, prints `plan:` lines (vendor, apt list, nv-codec-headers, patches, hwaccel flags,
CUDA-LLVM flags, required filters, bridge-test default, and a `plan: fsr2:` line naming patch 0016,
`-DGU_NO_NVOF` and the marker when `WITH_FSR2=1`) and exits 0 before any apt, download or write.
The real run reads the same variables the plan prints, so the plan cannot drift from the build; it does
**not** run apt, `patch`, configure or make, so it says nothing about whether the build succeeds.

**Downloads.** The Intel variant adds none: libva, libvpl and libdrm come from apt, so there is
nothing new to pin by sha256. The existing wheel and header pins are untouched. The FFmpeg and OIDN
tarballs stay unpinned in `build-ffmpeg.sh`; `scripts/test-vf-ort.sh` already pins FFmpeg 8.1.2
(`464beb5e...`), which a later change could reuse there.

**Host runtime.** The binary needs the host's VA-API driver and QSV runtime at run time
(`intel-media-va-driver-non-free`, and `libvpl2` or `libmfx-gen1.2` for QSV), the same kind of host
package as the OpenCL stack in section 5.4. The script's closing message says so; it does not install
them.

**Test.** `python3 scripts/test_build_ffmpeg_vendor.py` runs the real `build-ffmpeg.sh` with
`PLAN_ONLY=1` for both vendors (goldens including intel with `WITH_FSR2=1`, each refusal, nothing
written to disk, the unknown vendor), the real `proxmox-build.sh` against a stub repo and a fake
`ffmpeg` (a missing required filter restores `.prev` for each vendor, `fsr2` is required on Intel only
with `WITH_FSR2=1`, an unknown vendor is refused before anything is touched), and static wiring checks
(`bash -n`, the arrays are the ones the real run reads, the NVIDIA filter list appears in no build
script). It also mutates copies of the scripts and requires the test to fail for each of its mutants.
It needs no root, GPU, network or ffmpeg build.

**Not verified.** No ffmpeg has been built with `GPU_VENDOR=intel`: that libvpl and VA-API configure
and link, that `-hwaccels` lists `vaapi` and `-encoders` lists `h264_qsv`, `hevc_qsv` and `av1_qsv`
(the Intel verify step checks it), that 0006..0014 as skipped leave a tree the rest still patches
(patch stacking was not run), that 0016 applies without 0006 to 0014, and 0008 and 0009 on a VA-API
chain are all open until a build on an Intel server.

## 6. A380 measurements

Reported by the owner from runs on an Intel Arc A380. **The method (source clips, frame counts,
what else was running) is not recorded in this repository**, so treat them as sizing, not as a
baseline, and re-measure with a served-segment probe in the PR that depends on any of them.

| Chain | Result |
|---|---|
| FSRCNNX, 360p to 540p source to 1080p output | 98 to 120 fps |
| FSRCNNX, 720p to 1080p | 75 fps |
| FSRCNNX, 1080p to 2160p | 38 to 40 fps |
| RAVU (RAVU-Zoom) | 50 fps at 4K |
| VA-API decode, then `hwmap` to Vulkan | failed; software decode used |

All of the throughput rows are the libplacebo Vulkan chain with software decode, i.e. what the
plugin builds on an NVIDIA box today. They say the shader half of the chain is viable on this GPU
at realtime for one session, which is the premise of the series. They do not say anything about
encode, the neural levels, or more than one session.

## 7. Rules that apply to every PR in the series

- **NVIDIA stays byte-identical.** Each PR that touches a command line carries the harness case for
  the NVIDIA string before the change and the same string after.
- **Honest negatives.** A level the vendor cannot run is withheld from the probe and said so; a
  vendor chosen and not found is reported as not found (AGENTS.md invariant 7).
- **Fail soft.** Detection and every new patch fall back to today's path on any error (invariants 4
  and 15). A detection failure is vendor `none`, never an exception on the transcode path.
- **One axis is only real when `Option()` reads it** (invariant 11): `GpuVendor` is a setting, not an
  axis. Since PR 2 it also decides the encoder and the end of the chain, but only when an Intel GPU
  was actually seen.
- **Unmeasured ships off.** Anything whose cost on Intel has not been measured defaults off, or to a
  value that offers nothing.
- **No library content** in the repo (invariant 18). Measurements name hardware and resolutions only.
- **New C in `ffmpeg/`** carries the LGPL-2.1-or-later header the other `vf_*.c` files carry, and
  names the origin of anything copied (AGENTS.md, "Reuse before reinvention").
- **Docs in the same commit.** ENHANCEMENTS.md and this file change with the code they describe.

## 8. Open questions

1. ~~`vaapi` or `qsv` as the Intel encode family~~ Decided for `qsv` from the source (section 5.1); the CPU cost of the yuv420p to nv12 conversion is still unmeasured.
2. ~~Whether the pinned jellyfin-ffmpeg can select the Vulkan device by DRM node~~ It can, by derivation from a DRM device, and the selection is by PCI device id, so two identical cards are not told apart (section 5.3). Unverified on a binary and a machine.
3. The real cause of the VA-API to Vulkan failure; the derived-device explanation is a hypothesis
   (PR 7 ships the derived-device command behind the off-by-default `IntelHardwareDecode`, section
   5.6, and has not been run on an Arc card, so the cause is still open).
4. Neural throughput on the A380 through OpenVINO's GPU plugin, and so the default for `NeuralMaxSourceHeight` (PR 5 ships it at 0, section 5.4); also which `GPU.<n>` OpenVINO gives the Arc card on a box with an integrated GPU.
5. Whether FSR2 with zero motion vectors is acceptable on Intel, by the still-pixel temporal
   measurement; and a real non-CUDA flow source for `gu_inputs` (PR 6 shipped the zero-flow variant,
   section 5.5, and did not write one).
6. Where Xe-driver fdinfo differs from i915 on the target kernel (PR 2, section 5.2; the fixtures in the harness are written from the kernel documentation, not captured from a machine).
