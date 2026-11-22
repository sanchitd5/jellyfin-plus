# GPU vendors: running on Intel as well as NVIDIA

Design spec for making the enhancement engine vendor-aware. Written to be implemented as a series of
small pull requests, each one landing on its own branch and verified before the next starts. **PR 1
(detection and a report-only capability model, section 3), PR 2 (the encoder per vendor and Intel
load, sections 5.1 and 5.2) and PR 3 (the Vulkan device from the render node, and the NVIDIA-only
levels withheld on Intel, section 5.3) are built; the rest are the plan, with what is known, what was
measured, and what is still a guess marked as such.** Nothing in PRs 5 to 8 is built (PR 4 was folded
into PR 2). The three build clean (plugin and patcher) against the Jellyfin 12.4 server assemblies and
`scripts/test-gpu-vendor.sh` passes (941 checks, plus 11 in `scripts/test-shim-device.py`) in a .NET 10
SDK container with no GPU. None has been run on an NVIDIA or an Intel box: the detection is untested
against real sysfs, the Intel command lines (the encoder, and the render-node device of PR 3) have not
been run through ffmpeg, and the fdinfo parser has been run only against fixtures written from the
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
| `gu_inputs.h` (`vf_fsr2`, `vf_dlss`) | Motion vectors come from NVOFA optical flow (`libnvidia-opticalflow`), through CUDA | PR 6 |
| Decode | The decoder patches drop `-hwaccel` and `-c:v *_cuvid` so frames reach `hwupload` in system memory; only the CUDA-native levels keep a CUDA hwaccel | PR 7 |
| `scripts/build-ffmpeg.sh`, `proxmox-build.sh` | The patched binary must carry all five of `oidn optix ort fsr2 dlss` or the deploy is refused (AGENTS.md invariant 10); four of them need NVIDIA SDKs | PR 8 |

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
trust-it path when the test is inconclusive, the background test (a session call returns at once, two
callers start one test, a pass is never re-asked, a failure is re-asked only after the retry window),
the QSV pin to the detected node at every render node, including an AMD card at `renderD128` beside an
Intel one at `renderD129`, a node name that is not a plain render node, two-GPU machines in both orders (Intel first and
NVIDIA first, `auto`, `nvidia`, `intel`, two Intel GPUs, an AMD node beside an Intel one), that nothing
Intel emits `nvenc`, `cuda` or `vaapi`, that the probe lists lose exactly the NVIDIA-only levels and
report each with its own reason (`needs NVIDIA`, or "not measured on Intel" for the ONNX levels), that a session asking for `optix`, `dlpp-2` and `dlss` (or defaulted to
them) is enhanced without them, with `GpuWithheld` and the summary saying so, and that the same
sessions on an NVIDIA box run them. A mutation check (the Intel gate off in the device resolution, in
the level withholding and in the probe lists) failed 33, 12 and 6 of the earlier checks; the checks
added for the review have since been run and passed. The shim's `render_node` has its own 11 checks in
`scripts/test-shim-device.py`, run by the
script when python3 is present (the SDK container has none, so they were run separately).

## 4. The series

Each PR is on its own branch, small, and ends with the check that proves it, in the project's own
terms: a served segment probed with `ffprobe`, the built command read from `journalctl`, and the
negative case (feature off, output untouched). A PR does not start until the one before it is
verified on the real box.

| PR | Scope | Depends on | Proof |
|---|---|---|---|
| 1 | Detection and the report-only capability record (section 3) | none | Harness; the record shown live on an NVIDIA box and an Intel box; command lines unchanged |
| 2 | Encoder per vendor and Intel load (the old PR 2 and PR 4 together, section 5.1 and 5.2): map the negotiated codec to `*_nvenc` or `*_qsv`; a `*_nvenc` setting on an Intel box is treated as auto and said so; Jellyfin's own `*_qsv` choice is kept; `GuardAv1` test-encodes `av1_qsv` on Intel; the GPU-resident encode (`hwmap=derive_device=cuda`) is NVIDIA-only and reported n/a on Intel; `Gpu/Load` gains `vendor`, `encoderSessions`, `gpuBusyPercent` from DRM fdinfo | 1 | Served segment codec and size on each vendor; NVIDIA command byte-identical; busy percentage under a known encode |
| 3 | The Vulkan device from the render node instead of `vk:0`, on Intel only (section 5.3); the QSV device always pinned to the detected node; the NVIDIA-only levels (`optix`, `dlpp-*`, `vsr-rtcuda`, `fsr2`, `dlss`, `dlaa`) and the unmeasured ONNX levels (`realesr-*`) withheld from the probe on Intel, each with its reason; `MaxConcurrent` 1 for 4K output on a small Intel GPU recommended in docs, default unchanged | 1, 2 | Command on a two-GPU box names the right device; NVIDIA, `none` and one-GPU commands unchanged; the probe on an Intel box lacks the NVIDIA-only levels |
| 4 | Folded into PR 2 (Intel load via DRM fdinfo, section 5.2) | 1 | see PR 2 |
| 5 | `vf_ort` OpenVINO execution provider, `NeuralMaxSourceHeight` | 1, 8 | Neural level offered and running on Intel; a served segment |
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

So the Intel device is checked before it is used, **off the session path**. The engine runs the stock
ffmpeg once per node with exactly this device initialisation and a one-frame `hwupload,hwdownload`
(the pattern `Av1QsvUsable` and `VulkanDenoiseRunsOnPatchedBinary` already follow). The test takes up
to 20 s, so no session waits for it: `UpscaleEngine.WarmDevices`, called from `PatcherHost.Apply`
after `Configure`, starts it at plugin load, only one test runs per node (single-flight, behind a
short lock that guards the cache entry and never an ffmpeg run), and a session that arrives first
gets the last known answer or, when there is none yet, the untested node with the record text
`(test pending)`. A pass is kept for the life of the process. A failure, and an inconclusive result
(no ffmpeg found, a timeout), are kept for about 5 minutes and then re-tested in the background while
the stale answer is served, so a transient failure does not pin an Intel server to `vk:0` until
restart. A failed check falls back to `vk:0` (today's behaviour, so this PR cannot turn a working
Intel box into a dead one) and says so in the session record; an inconclusive check trusts the render
node, since nothing else would run either. When the record has no usable render node path, the
argument is `vk:0`. The node text is only ever the detector's own `/dev/dri/renderD<digits>`;
anything else is not put on a command line.

**The QSV device (also decided from the source).** With no QSV device on the command line,
`qsvenc_init_session` opens an internal session, and `ff_qsv_set_display_handle` (`libavcodec/qsv.c`)
creates a VA-API device with `vendor_id=0x8086`; `vaapi_device_create` then walks
`/dev/dri/renderD128` to `renderD135` (`max_devices = 8`) and takes the first node of that vendor.
That search is not what the engine relies on: it is derived from source, never run, and on a box with
an AMD GPU at `renderD128` and an Intel one at `renderD129` the detector's node is the only thing
that names the right card. So whenever ffmpeg lists a QSV encoder the arguments always carry
`-init_hw_device qsv=qs:hw_any,child_device=<node>`, which is what `-qsv_device <node>` expands to
(`opt_qsv_device`), with `<node>` the detected one; with no QSV encoder there is no argument and the
record says `none`. The encoder takes the one QSV device on the command line
(`hw_device_setup_for_encode`, `HW_CONFIG_ENCODER_DEVICE(NV12, QSV)`). What this does not do is let an
operator choose between two Intel GPUs: the first Intel node wins, as it does in the detector.

**Where it applies.** One function builds the device arguments and both the session command
(`HwaccelArgs`) and the two engine probes that start a Vulkan or QSV device use it, so a probe tests
the device a session will use. A plan resolves that device once (`Plan.Devices`, set in `Decide`) and
`HwaccelArgs(plan)`, `Describe` and both probe builders take it, so a render-node answer that changes
between two reads cannot make the session record and its command disagree. The shim fallback reads
an optional `render_node` from its JSON (unset keeps `vulkan=vk:0`); it has no vendor detection and
no probe, so it is for an operator who knows the node. It is set by hand in
`/etc/jellyfin-upscale.json`, is never test-started, and `ShimBridge.Sync` (which merges into the
existing keys) keeps it across a sync but never writes it.

**The NVIDIA-only levels.** On the Intel path these are not offered and are not run, and the reason
is stated, never silent:

| Axis | Withheld | Why |
|---|---|---|
| Denoise | `optix`, `optix-temporal` (and the unlisted `optix-hdr`) | OptiX needs CUDA frames |
| Neural | `dlpp-1` to `dlpp-4`, `vsr-rtcuda` (and the retired `vsr`) | NVIDIA RTX DLPP / VSR libraries over CUDA |
| Game | `fsr2`, `dlss`, `dlaa` | DLSS is NVIDIA's; `fsr2` is fed by NVOFA motion vectors through CUDA (`gu_inputs.h`), which is PR 6's problem |

Each is removed from the probe lists (`Levels.Denoise`, `Neural`, `Game`, `GameOptionLevels` and the
label maps), so the client, which builds its menu from the probe, never offers it. A server whose
dashboard default or a stale client still asks for one gets `off` for that axis and the session says
so: `SessionRecord.GpuWithheld` and a sentence in the summary ("... NOT run, needs NVIDIA ..."), with
`NeuralRequested` still naming what was asked. The probe carries `Levels.Withheld` (axis, level,
the level's own reason) and the status route the same, **only on the Intel path**, so the NVIDIA probe
and status are unchanged, and the dashboard's GPU line prints each as `Axis level (reason)`.

The ONNX neural levels (`realesr-anime-x2`, `realesr-anime-x4`, `realesr-general-x4`) are withheld on
Intel as well, but with a different reason, "not measured on Intel": they do not need CUDA, they fall
back to ONNX Runtime's CPU provider, which is almost certainly not real time and has never been
measured for this chain. The Intel `Neural` list is therefore `off`, the session summary names the
reason per group, and PR 5 (OpenVINO and `NeuralMaxSourceHeight`) is what brings them back. There is
no setting to opt in: that would need the five-place settings checklist for a level nobody has
measured, and the withholding goes away with PR 5.

**Recommended in documentation only: `MaxConcurrent` 1 for 4K output on a small Intel GPU.** The one
measurement there is (section 6) is a single session, 1080p to 2160p at 38 to 40 fps on an A380.
That is a margin over real time for one session, nothing is known for two, and the default of 4 would
admit four. The setting is not changed.

### 5.4 PR 5: `vf_ort` on OpenVINO, and routing by source height

`vf_ort` creates a CUDA execution provider and falls back to the CPU one. ONNX Runtime has an
OpenVINO execution provider that runs on Intel GPUs; adding it is a new `device` value
(`openvino`) in `vf_ort.c` and an ORT build that carries the provider, which is what
`OpenVinoAvailable` will check for. OpenVINO is Apache-2.0, so under AGENTS.md ("Apache-2.0 is
usable only because this is GPLv2-or-later") the file that links it records that the combined work
ships as GPLv3.

A neural level costs per pixel of source, so on a small GPU it is affordable only for small sources.
**`NeuralMaxSourceHeight`** (new setting, patcher mirror, probe) routes by source height: at or below
it a neural level may run on the vendor's neural path; above it the session uses the shader SR level
the ladder already offers. This is the Intel counterpart of the CUDA path's `SrMinScaleFactor`, and
like it the value is chosen from measurement. **No neural measurement exists for the A380 yet**, so
PR 5 measures first and sets the default from the result; until then the setting would default to a
value that offers nothing, so nothing unmeasured ships on (the project's rule for anything
unmeasured).

### 5.5 PR 6: `gu_inputs` without CUDA

FSR2 and DLSS are fed by `gu_inputs.h`, which synthesises four things a renderer would supply.
Depth comes from an ONNX model (so it follows PR 5 onto OpenVINO), jitter is a CPU phase
correlation, the reactive mask derives from flow consistency, and **motion vectors come from NVOFA**,
the NVIDIA fixed-function optical-flow block, through CUDA. That one dependency is what ties
`vf_fsr2` to NVIDIA although FSR2 itself is vendor-neutral. DLSS needs NVIDIA regardless.

The work is a flow provider interface in `gu_inputs.h` with NVOFA as one implementation and a
non-CUDA one as another (a CPU block-matching search, or an optical-flow network on the OpenVINO
path). Until a provider measures well, **Intel offers no game-upscaler levels**, reported through the
capability (`CudaOnlyLevelsAvailable` false and no flow provider), not hidden. The temporal-artifact
measurements AGENTS.md prescribes (still-pixel variation across frames) apply to any new flow source,
because per-frame metrics cannot see what a wrong flow field does.

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

### 5.7 PR 8: a `GPU_VENDOR=intel` build, and the five-filter invariant

`scripts/build-ffmpeg.sh` gains `GPU_VENDOR=nvidia|intel` (default `nvidia`, today's behaviour).
`intel` builds with VA-API (and oneVPL if PR 2 chooses QSV), the ORT OpenVINO provider, and without
the CUDA-only filters and the NVIDIA headers (`optix`, `dlss`, `dlpp_rtcuda`, `vsr_rtcuda`,
`nv-codec-headers`).

That collides with AGENTS.md invariant 10, which requires all five of `oidn optix ort fsr2 dlss`
to survive a rebuild and makes `proxmox-build.sh` refuse a binary missing any. Four of the five need
NVIDIA SDKs, so an Intel binary can never satisfy it. The fix is a **required set per vendor**
(NVIDIA: the five; Intel: the subset that builds), checked by the same script. That is an amendment
to an invariant, so it is made in PR 8 with the invariant's own text updated in the same commit and
is the maintainer's call, not something to slip in. The shim's per-invocation probe of the binary's
filter list already strips nodes the binary lacks, so a binary with fewer filters degrades rather
than failing, which is what the amended invariant relies on.

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
   (PR 7).
4. Neural throughput on the A380, and so the default for `NeuralMaxSourceHeight` (PR 5).
5. A non-CUDA flow source for `gu_inputs` that survives the temporal measurements (PR 6), or the
   decision that Intel does not get the game upscalers.
6. Where Xe-driver fdinfo differs from i915 on the target kernel (PR 2, section 5.2; the fixtures in the harness are written from the kernel documentation, not captured from a machine).
