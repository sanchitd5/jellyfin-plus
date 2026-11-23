# GPU vendors: running on Intel as well as NVIDIA

Design spec for making the enhancement engine vendor-aware. Written to be implemented as a series of
small pull requests, each one landing on its own branch and verified before the next starts. **PR 1
(detection and a report-only capability model) is built (section 3); the rest are the plan, with
what is known, what was measured, and what is still a guess marked as such.** Nothing in PRs 2 to 8
is built. PR 1 builds clean (plugin and patcher) against the Jellyfin 12.4 server assemblies and
`scripts/test-gpu-vendor.sh` passes (113 checks) in a .NET 10 SDK container with no GPU. It has not
been run on an NVIDIA or an Intel box, so the detection itself is untested against real sysfs.

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
| `GpuLoadController` (`GET /JellyfinPlus/Gpu/Load`) | NVENC session count from `nvidia-smi --query-gpu=encoder.stats.sessionCount` | PR 4 |
| CUDA-only levels | `optix` denoise, `dlss` / `dlaa`, `dlpp-1..4`, `vsr-rtcuda` need CUDA (and, for DLSS, the NVIDIA runtime the operator fetches) | Offered only when the capability says CUDA is available (PR 2) |
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
the places this plugin already reports what the server did. **Nothing reads the record to decide
anything.** No command line, no encoder choice, no level list changes, whatever the record says.

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
3. **NVIDIA device in the container.** The proprietary driver often exposes no render node (no
   `nvidia_drm` modeset), so an NVIDIA server can show no `0x10de` node at all. NVIDIA is also
   present when the container has `/dev/nvidiactl` or `/dev/nvidia[0-9]*` (`nvidia-uvm` and
   `nvidia-caps` do not count). `/proc/driver/nvidia` and `nvidia-smi` are deliberately **not**
   used: `/proc/driver` is the host kernel's procfs, visible in every pod, so an Intel-only pod on a
   host that also carries an NVIDIA card was called NVIDIA, and ffmpeg then died on
   `hwmap=derive_device=cuda`.
4. **Cached.** The machine is asked once per process: a GPU does not appear mid-run, and the
   transcode path must not spawn a process per session. The record is rebuilt from the cached
   inventory on each call, so a change to the setting applies at the next session.
5. **Never throws.** Any failure is a record with vendor `none` and the reason (AGENTS.md invariant 4).

### 3.3 The setting

`GpuVendor` = `auto` | `nvidia` | `intel` | `none`, default `auto`.

| Value | Meaning |
|---|---|
| `auto` | NVIDIA when it is present (an NVIDIA render node, or an NVIDIA device in the container), else Intel when an Intel render node is present, else `none`. NVIDIA first, so an existing install, and one that gains an Intel card later, behaves as before. |
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

Only the **Status** route is administrator-only. `GET /JellyfinPlus/Session/{id}` is open to any
authenticated user (it answers for the caller's own session), so everything it serves carries the
summary alone: `Vendor`, `Detected`, `EncoderFamily`, `CudaOnlyLevelsAvailable`, `OpenVinoAvailable`
(`GpuSummary`). `Devices`, `RenderNode`, `Reason`, `Setting` and `DecodeType` are never served there
(AGENTS.md invariant 13).

- **Probe** (`PatcherHost.Levels()`, served as `Levels.Gpu` by `GET /JellyfinPlus/Session/probe`): summary.
  The probe is what the client builds its menu from, so this is where later PRs will filter levels.
- **Status** (`GET /JellyfinPlus/Status`, `Gpu`), and the dashboard's status line: the full record.
- **SessionRecord** (`Gpu`), written when the session's command is built, so Recent sessions and
  `GET /JellyfinPlus/Session/{id}` (`Record.Gpu`, and flat `Gpu`) say what the server looked like for
  that session: summary, one immutable instance shared by every record built under the same detection.

### 3.5.1 The shared probe helper

`CachedProbe<T>` (`src/patcher/CachedProbe.cs`) is how the patcher asks the machine something slow
from a path that must not wait for it. PRs 2, 3 and 7 reuse it rather than probing under a lock:

- `Start()` runs the probe on the thread pool at plug-in start-up (`PatcherHost.Apply`); `Peek()` is
  all the transcode path calls, takes a lock for a few field reads and never runs the probe.
- A conclusive answer (the `conclusive` predicate) is kept for the life of the process. A null,
  throwing or inconclusive one stands only for `retryAfter` (60 s for GPU detection); the next `Peek()`
  after that schedules a new probe and returns the last answer meanwhile. This is the fix for
  `nvidia-smi` failing while the driver loads at boot and pinning the box to `none` until restart.
- Before the first answer lands the record says so ("GPU detection has not finished; reported as
  none") rather than guessing.
- `Reset()` discards the answer and any probe in flight. Tests pass a fake clock and a queue as the
  scheduler (`scripts/gpu-vendor-harness`).

### 3.6 Tests

`scripts/test-gpu-vendor.sh <jfbin>` builds `scripts/gpu-vendor-harness`, which compiles the real
patcher sources (so it reaches the internal `UpscaleEngine` and `Plan` as production does) against
the server's DLLs, the pattern AGENTS.md describes for profiling harnesses. It needs no GPU and no
running Jellyfin. It checks:

- the vendor-id mapping (`0x10de`, `0x8086`, case, prefix, padding, AMD not claimed);
- the decision: NVIDIA first on a mixed box, each explicit setting, a chosen vendor that is absent,
  the NVIDIA-device rule (an Intel-only pod with the host's `/proc/driver/nvidia` is Intel; a
  `/dev/nvidia0` or `/dev/nvidiactl` is NVIDIA, with today's precedence over Intel unchanged),
  the OpenVINO provider file, an unrecognised setting;
- the real sysfs reader against a fake sysfs tree, including a node with no `/dev` entry and numeric
  node order;
- **report only:** with `GpuVendor` set to each of `nvidia`, `auto`, `intel`, `none` and nonsense,
  `HwaccelArgs` (both forms), `BuildChain` (plain, deband with download, CUDA-native, CUDA-native
  bridged through libplacebo) and `EncoderFor` return the exact strings they returned before the
  setting existed. The expected strings are written out by hand from the code, not computed;
- **access:** the probe and a recorded session (what any authenticated user can fetch) carry the five
  summary fields and none of `Devices`, `RenderNode`, `Reason`, `Setting`, nor a device path or PCI
  id anywhere in the body; the status route keeps the full record;
- `CachedProbe<T>` with a fake clock: the probe never runs on the caller, a negative answer is retried
  after the window and replaced, a conclusive one is never re-asked, a throw is no answer, and a
  probe in flight across `Reset()` is discarded;
- `GpuVendor=none` with an NVIDIA card present says the setting overrode detection.

## 4. The series

Each PR is on its own branch, small, and ends with the check that proves it, in the project's own
terms: a served segment probed with `ffprobe`, the built command read from `journalctl`, and the
negative case (feature off, output untouched). A PR does not start until the one before it is
verified on the real box.

| PR | Scope | Depends on | Proof |
|---|---|---|---|
| 1 | Detection and the report-only capability record (section 3) | none | Harness; the record shown live on an NVIDIA box and an Intel box; command lines unchanged |
| 2 | Encoder per vendor: map the negotiated codec to `*_nvenc` or `*_vaapi` / `*_qsv`; `GuardAv1` asks the capability instead of `nvidia-smi` on Intel; `GpuResidentEncode` ends in `hwmap=derive_device=vaapi` on Intel; CUDA-only levels withheld from the probe when the capability says no CUDA | 1 | Served segment codec and size on each vendor; NVIDIA command byte-identical |
| 3 | The Vulkan device from the render node instead of `vk:0` | 1 | Command on a two-GPU box names the right device; one-GPU command unchanged |
| 4 | Intel load via DRM fdinfo, for `Gpu/Load` | 1 | Busy percentage under a known encode |
| 5 | `vf_ort` OpenVINO execution provider, `NeuralMaxSourceHeight` | 1, 8 | Neural level offered and running on Intel; a served segment |
| 6 | `gu_inputs` without CUDA | 5 | Motion vectors from a non-NVOFA source, compared by the project's temporal measurements |
| 7 | Opt-in Intel hardware decode, with the VA-API to Vulkan derived-device fix | 2, 3 | Frames reach libplacebo from VA-API; off by default |
| 8 | A `GPU_VENDOR=intel` ffmpeg build and a per-vendor required-filter set | 1 | `-filters` lists the intel set; deploy refuses a binary missing one of it |

Order is not strict (4 is independent of 5 to 8), but 2 and 3 precede anything that changes a
command line, so the first behaviour change is the smallest one.

## 5. Design notes for PRs 2 to 8

### 5.1 PR 2: the encoder per vendor

NVIDIA is unchanged: `*_nvenc`, the `nvidia-smi` AV1 guard, `hwmap=derive_device=cuda`.

Intel encodes through the same iGPU/dGPU media engine whichever API drives it. The choice between
`*_vaapi` and `*_qsv` is **open and is measured in PR 2**, not decided here. The reason `vaapi` is
the reported family is the hand-off: this repo's own reading of FFmpeg's `hwcontext_vulkan.c`
(docs/roadmap/hw-resident-encode-plan.md) found that `vulkan_map_from()` already has a **VA-API**
case, which is exactly why the NVIDIA direction needed a patch (`ffmpeg/0006`) and the Intel one
may not: a libplacebo (Vulkan) frame can be mapped to a VA-API frame in stock FFmpeg, so the Intel
analogue of `GpuResidentEncode` is `hwmap=derive_device=vaapi` into `hevc_vaapi`. That reading is
from source, not from a run on the A380; PR 2 verifies it, and if it fails the chain ends in
`hwdownload,format=yuv420p` as the non-resident NVIDIA path does.

The AV1 guard on Intel asks the encoder list and a one-frame test encode rather than a compute
capability: the A380 has an AV1 encoder, and a driver that lacks it must fall back the way
`GuardAv1` falls back to HEVC today. `ChooseEncoder`'s configured-encoder rules (ffmpeg has it, the
client declared the codec) are vendor-independent and stay.

The `Encoder` setting's default stays `hevc_nvenc`. A setting naming an encoder the active vendor
cannot run is refused with a reason in Recent sessions, which is what the configured-encoder path
already does for a missing encoder.

### 5.2 PR 4: Intel GPU load via DRM fdinfo

NVIDIA's `Gpu/Load` counts NVENC sessions from `nvidia-smi`. Intel has no equivalent counter, but
the kernel exports per-client engine time: `/proc/<pid>/fdinfo/<fd>` of an open DRM file carries
`drm-driver:`, `drm-client-id:` and per-engine busy time in nanoseconds (`drm-engine-video:`,
`drm-engine-render:`, `drm-engine-copy:` for i915; Xe reports cycles instead). Busy percentage is the
change in busy time over wall time between two samples, per engine, summed across clients (deduplicate
by `drm-client-id`, since one client can hold several fds to the same file).

Boundaries to check, not assume: Jellyfin runs as an unprivileged user and can read `fdinfo` only for
its own processes, which is what is wanted (its ffmpeg children) but means other workloads on the GPU
are invisible; the sampling interval must be short and the result cached, as `Gpu/Load` already
caches the NVENC count; and the i915 and Xe field names differ and must be read per `drm-driver`.
`NvencSessions` stays as it is for NVIDIA; the response gains an engine-busy field for Intel.

### 5.3 PR 3: the Vulkan device from the render node

`-init_hw_device vulkan=vk:0` picks the first Vulkan device. With an Intel and an NVIDIA card both
present, which is "0" is up to the loader, and the chain can run on the wrong GPU while the record
says otherwise. The render node in the capability record is the stable handle: a Vulkan device
reports its DRM render node through `VK_EXT_physical_device_drm`, so the device is chosen by
matching that, or, where the pinned jellyfin-ffmpeg cannot select by DRM node, by enumerating once
and caching the index for that node. Which of the two the pinned build supports is **not yet
checked**; PR 3 starts by checking it on the real binary. When the record has no render node (an
NVIDIA GPU without `nvidia_drm`), the argument stays `vk:0` exactly as today.

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
  axis, and is read only by the capability record until PR 2 makes it matter.
- **Unmeasured ships off.** Anything whose cost on Intel has not been measured defaults off, or to a
  value that offers nothing.
- **No library content** in the repo (invariant 18). Measurements name hardware and resolutions only.
- **New C in `ffmpeg/`** carries the LGPL-2.1-or-later header the other `vf_*.c` files carry, and
  names the origin of anything copied (AGENTS.md, "Reuse before reinvention").
- **Docs in the same commit.** ENHANCEMENTS.md and this file change with the code they describe.

## 8. Open questions

1. `vaapi` or `qsv` as the Intel encode family (PR 2, measured).
2. Whether the pinned jellyfin-ffmpeg can select the Vulkan device by DRM node, or the index must be
   discovered (PR 3).
3. The real cause of the VA-API to Vulkan failure; the derived-device explanation is a hypothesis
   (PR 7).
4. Neural throughput on the A380, and so the default for `NeuralMaxSourceHeight` (PR 5).
5. A non-CUDA flow source for `gu_inputs` that survives the temporal measurements (PR 6), or the
   decision that Intel does not get the game upscalers.
6. Where Xe-driver fdinfo differs from i915 on the target kernel (PR 4).
