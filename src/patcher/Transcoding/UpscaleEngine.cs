using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.MediaEncoding.Transcoding;

namespace Jellyfin.Plugin.Plus.Patcher
{
    /// <summary>
    /// What the server actually did for one playback session. Written at the moment the ffmpeg
    /// command is built, so it reflects the command that ran - never what the client asked for.
    /// </summary>
    public class SessionRecord
    {
        public string Timestamp { get; set; }

        public string PlaySessionId { get; set; }

        /// <summary>
        /// The viewer this transcode was started for, so the per-session endpoint can refuse to
        /// describe someone else's playback. Empty when the request carried nothing to identify
        /// one; see <see cref="UserKey"/>.
        /// </summary>
        public string UserId { get; set; }

        public string Source { get; set; }

        public int SourceWidth { get; set; }

        public int SourceHeight { get; set; }

        public int OutputWidth { get; set; }

        public int OutputHeight { get; set; }

        /// <summary>True only when a larger-than-source chain really went into the command.</summary>
        public bool UpscaleApplied { get; set; }

        /// <summary>True only when a sharpening pass really went into the command.</summary>
        public bool DeblurApplied { get; set; }

        /// <summary>True only when a denoise filter node really went into the command.</summary>
        public bool DenoiseApplied { get; set; }

        /// <summary>True only when a deblocking / deringing node really went into the command.</summary>
        public bool DeblockApplied { get; set; }

        /// <summary>True only when a neural super-resolution network really went into the command.</summary>
        public bool NeuralApplied { get; set; }

        /// <summary>
        /// True when this session's neural level (dlpp-1..4, vsr-rtcuda) runs CUDA-native. Until
        /// 2026-09-24 this also meant Detail/Refine/Chroma/Debanding and the scaling-kernel choice
        /// were always forced off for it - the Vulkan libplacebo stage those axes need was not
        /// reachable from a CUDA-only chain (ffmpeg/0006 hwmap=derive_device=cuda existed, but not
        /// the reverse hop). Fixed: BuildChain now bridges through hwmap=derive_device=vulkan and
        /// back when any of those five axes is requested alongside a CUDA-native level (see its own
        /// comment; ffmpeg/0007-0009, livetestbox.md). So this flag alone no longer tells you
        /// whether those axes ran - check Pipeline for that, which now distinguishes the bridged
        /// case. Kept named CudaNeuralBypass and still true in both cases: the session's ENCODE
        /// HANDOFF still bypasses Vulkan-for-encode either way (NVENC gets a CUDA frame directly,
        /// never software), which is the part everything downstream of this flag (ResizeCredit)
        /// actually cares about. See INTEGRATION_DESIGN.md section 6, "what should be
        /// user-visible."
        /// </summary>
        public bool CudaNeuralBypass { get; set; }

        /// <summary>
        /// The branch UpscaleEngine actually took for this session's encode handoff: "CUDA (RTX)"
        /// for a CudaNeuralBypass session that never touches libplacebo (dlpp-1..4/vsr-rtcuda with
        /// none of deband/kernel-scale/refine/chroma/deblur requested), "CUDA+Vulkan (RTX bridge)"
        /// for a CudaNeuralBypass session that DOES touch libplacebo (BuildChain's hwmap bridge,
        /// fixed 2026-09-24 - ffmpeg/0007-0009, livetestbox.md), "Vulkan (GPU-resident)" when
        /// GpuResidentEncode handed NVENC a mapped CUDA frame straight off the Vulkan chain
        /// (hwmap=derive_device=cuda - see ffmpeg/0006-vulkan-to-cuda-hwmap.patch, verified on real
        /// GPU for every libplacebo axis), or "Vulkan" for the older hwdownload round trip. Null
        /// when nothing ran. See WEB_PANEL_DESIGN.md section 3.5, which named this row before the
        /// server sent it.
        /// </summary>
        public string Pipeline { get; set; }

        public bool GameApplied { get; set; }

        /// <summary>True only when libplacebo debanding really went into the command.</summary>
        public bool DebandApplied { get; set; }

        public string DeblurLevel { get; set; }

        public string SrLevel { get; set; }

        /// <summary>The post-scale refinement pass that ran (SSimSuperRes), or "off".</summary>
        public string RefineLevel { get; set; }

        /// <summary>Whether a refinement pass was actually put in the shader.</summary>
        public bool RefineApplied { get; set; }

        /// <summary>The chroma upscaling pass that ran (KrigBilateral), or "off".</summary>
        public string ChromaLevel { get; set; }

        /// <summary>Whether a chroma pass was actually put in the shader.</summary>
        public bool ChromaApplied { get; set; }

        /// <summary>
        /// True when an SR level was asked for and deliberately not run because the upscale ratio
        /// was below SrMinScaleFactor. Reported so the player can say the level is inactive for
        /// this combination instead of claiming a network ran.
        /// </summary>
        public bool SrBypassed { get; set; }

        /// <summary>
        /// True when the SR level sharpens inside its own pass (NVScaler) and the separate
        /// sharpening pass was therefore dropped rather than stacked on top of it.
        /// </summary>
        public bool SrOwnsSharpening { get; set; }

        /// <summary>The libplacebo scaling kernel this job used.</summary>
        public string Upscaler { get; set; }

        /// <summary>The SR level that was asked for, even when it was bypassed.</summary>
        public string SrRequested { get; set; }

        /// <summary>
        /// The neural level that was asked for, even when it did not run. NeuralLevel carries only
        /// what was applied, so without this a requested-but-dropped network is indistinguishable
        /// from one nobody asked for, which is how the neural axis shipped dead once already.
        /// </summary>
        public string NeuralRequested { get; set; }

        public string DenoiseLevel { get; set; }

        /// <summary>The deblocking / deringing level that ran, or "off".</summary>
        public string DeblockLevel { get; set; }

        /// <summary>
        /// The deblock level that was asked for, even when it did not run. DeblockLevel carries
        /// only what was applied, and a level can be asked for and dropped here for a reason no
        /// other axis has: this build may simply not carry the filter. Without this, "requested
        /// and not available" would be indistinguishable from "nobody asked", which is exactly how
        /// the neural axis shipped dead once already.
        /// </summary>
        public string DeblockRequested { get; set; }

        /// <summary>The neural super-resolution level that ran, or "off".</summary>
        public string NeuralLevel { get; set; }

        /// <summary>
        /// The ONNX Runtime provider the ort filter was told to use for this session: "openvino" or
        /// "cpu" on an Intel GPU, "cuda" on any other server. Null when no ONNX neural level ran
        /// (not enhanced, no neural level, or a CUDA-native level). It is what the command asked
        /// for, not a probe: that ONNX Runtime really loaded the provider shows only in ffmpeg's
        /// own log, where the filter warns when it ran on the CPU instead.
        /// </summary>
        public string NeuralDevice { get; set; }

        /// <summary>
        /// Why the device is the CPU on an Intel server (the OpenVINO provider is not installed),
        /// or null.
        /// </summary>
        public string NeuralDeviceNote { get; set; }

        /// <summary>
        /// Set when a game level ran on an Intel server, where the filter has no CUDA and so no
        /// optical flow: the motion vectors it is given are zero. Null everywhere else.
        /// </summary>
        public string GameMotionNote { get; set; }

        /// <summary>
        /// What NeuralMaxSourceHeight did to this session: a neural level chosen because the source
        /// is small, a neural level not run because the source is too tall, or why nothing was
        /// chosen. Null when the setting is 0 or did nothing to this session.
        /// </summary>
        public string NeuralRouting { get; set; }

        public string GameLevel { get; set; }

        /// <summary>The jitter source the game upscaler actually ran with, or null when it did not run.</summary>
        public string GameJitter { get; set; }

        /// <summary>The depth source the game upscaler actually ran with, or null.</summary>
        public string GameDepth { get; set; }

        /// <summary>The reactive-mask source the game upscaler actually ran with, or null.</summary>
        public string GameReactive { get; set; }

        /// <summary>
        /// True when a depth source other than flat was asked for and the weights are not on this
        /// server, so flat is what ran. The filter's other fallback - no CUDA execution provider -
        /// happens inside ffmpeg and is not visible here, so this is not the whole story and does
        /// not claim to be.
        /// </summary>
        public bool GameDepthDowngraded { get; set; }

        /// <summary>
        /// The Vulkan denoise level dropped because this session also needed the patched binary,
        /// whose shaderc cannot compile that filter. Empty when nothing was dropped. Carried so the
        /// panel and the dashboard can say it happened rather than showing a level that did not run.
        /// </summary>
        public string DenoiseDroppedForPatchedBinary { get; set; }

        /// <summary>The video encoder that went into the command, when this plugin chose it.</summary>
        public string Encoder { get; set; }

        /// <summary>Why that encoder, in particular whether a configured encoder was refused.</summary>
        public string EncoderReason { get; set; }

        /// <summary>
        /// The GPU capability record as the plugin saw it when this session's command was built:
        /// vendor, render node, encoder family, decode type, whether the CUDA-only levels and
        /// OpenVINO are available. On Intel the vendor and Detected decided the encoder and the end
        /// of the chain (docs/architecture/GPU_VENDORS.md, PR 2); the other fields are descriptive.
        /// Null only when the session was recorded by a build that predates the field.
        /// </summary>
        public GpuCapability Gpu { get; set; }

        /// <summary>
        /// Whether the encoder was fed straight from the GPU (the frame mapped from Vulkan to CUDA
        /// without a download): "on", "off" (the GpuResidentEncode setting is off), or "n/a" on an
        /// Intel GPU, where that hand-off exists only for NVENC and the frame is always downloaded.
        /// Null when the session ran no Vulkan chain (not enhanced, or a CUDA-native level, which
        /// never leaves GPU memory). Never "on" on a server where the hand-off did not happen.
        /// </summary>
        public string GpuResidentEncode { get; set; }

        /// <summary>
        /// The Vulkan device this session's command initialised: "vk:0" (the first Vulkan device,
        /// every server but a detected Intel one), or the Intel render node it was derived from, with
        /// the reason when the render node was not used (its test device start failed) or could not
        /// be tested. Null when the session was not enhanced. GPU_VENDORS.md 5.3.
        /// </summary>
        public string VulkanDevice { get; set; }

        /// <summary>
        /// Which device the QSV encoder opens, on an Intel GPU only: "implicit" (the encoder finds
        /// the first Intel render node itself, which is the one in the record), the render node when
        /// the command pins it, or why there is none. Null off Intel and when not enhanced.
        /// </summary>
        public string QsvDevice { get; set; }

        /// <summary>
        /// The levels this session asked for (or the dashboard defaulted to) and did not get because
        /// they need an NVIDIA GPU and this server's is Intel, for example "denoise optix, game
        /// dlss". Null when nothing was withheld, which is every server but an Intel one.
        /// </summary>
        public string GpuWithheld { get; set; }

        /// <summary>
        /// Whether this session decoded on the Intel GPU (VA-API surfaces mapped to Vulkan) instead
        /// of in software. Null unless the IntelHardwareDecode setting is on, the GPU is a detected
        /// Intel one and the session was enhanced, so every other server's session record is
        /// unchanged. GPU_VENDORS.md 5.6.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? HardwareDecode { get; set; }

        /// <summary>Why hardware decode ran, or which condition kept software decode. Null whenever HardwareDecode is.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string HardwareDecodeReason { get; set; }

        /// <summary>applied | not-requested | off-by-client | concurrency-cap | subtitle-burn-in | stream-copy | disabled | ineligible.</summary>
        public string Status { get; set; }

        public string Reason { get; set; }

        /// <summary>
        /// Null when this session never asked for a live A/B swap. Otherwise one of: swapping (the
        /// new ffmpeg process is admitted and starting), swap-capacity (refused; degraded to
        /// today's tear-down-and-restart), swapped (the new process was ready and the old one's
        /// teardown ran), swap-timeout (the new process never arrived in time; the old one's
        /// teardown ran anyway so it is never left running forever). See
        /// UpscaleEngine.TryAdmitSwap/OnNewJobReady and LIVE_APPLY_DESIGN.md.
        /// </summary>
        public string SwapStatus { get; set; }

        /// <summary>A one-line summary for a player overlay.</summary>
        public string Summary { get; set; }
    }

    /// <summary>
    /// Decides what to do with one encoding job and builds the ffmpeg fragments for it.
    /// Every entry point is total: on any surprise it reports "do nothing", so Jellyfin keeps its
    /// own behaviour.
    /// </summary>
    internal static class UpscaleEngine
    {
        private const int HistoryLimit = 50;

        // The long edge at which a portrait source is treated as already 4K+ and the upscale
        // ladder is bypassed rather than offered - see Decide()'s portrait branch. 3840 is 4K
        // UHD's long edge (3840x2160 landscape, or a 2160x3840 portrait recording); either the
        // width or the height reaching it means the source is already at that resolution class.
        private const int PortraitUltraHdEdge = 3840;

        private static readonly List<SessionRecord> _history = new List<SessionRecord>();
        private static readonly object _historyLock = new object();
        private static readonly ConcurrentDictionary<string, SessionRecord> _bySession =
            new ConcurrentDictionary<string, SessionRecord>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, Tuple<string, string>> _encoderBySession =
            new ConcurrentDictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Parks the encoder this plugin chose for a session, and backfills any record already made.</summary>
        public static void NoteEncoder(string playSessionId, string encoder, string reason)
        {
            if (string.IsNullOrEmpty(playSessionId))
            {
                return;
            }

            _encoderBySession[playSessionId] = Tuple.Create(encoder, reason);
            if (_bySession.TryGetValue(playSessionId, out var existing))
            {
                existing.Encoder = encoder;
                existing.EncoderReason = reason;
            }
        }

        /// <summary>Live settings, pushed across from the plugin.</summary>
        public static UpscaleSettings Settings { get; set; } = new UpscaleSettings();

        internal sealed class Plan
        {
            public bool Act { get; set; }

            public bool UpscaleApplied { get; set; }

            public bool DeblurApplied { get; set; }

            public bool DenoiseApplied { get; set; }

            public bool DebandApplied { get; set; }

            public int Width { get; set; }

            public int Height { get; set; }

            public int SourceWidth { get; set; }

            public int SourceHeight { get; set; }

            public string SrLevel { get; set; } = "off";

            /// <summary>The SR level the session or the dashboard asked for, before any bypass.</summary>
            public string SrRequested { get; set; } = "off";

            /// <summary>True when that level was dropped because the ratio was below SrMinScaleFactor.</summary>
            public bool SrBypassed { get; set; }

            /// <summary>
            /// True when the source carries no colour range tag, so the chain has to declare one
            /// rather than let libplacebo guess. See where this is set in Decide.
            /// </summary>
            public bool RangeUntagged { get; set; }

            public string DeblurLevel { get; set; } = "off";

            public string RefineLevel { get; set; } = "off";

            public bool RefineApplied { get; set; }

            public string ChromaLevel { get; set; } = "off";

            public bool ChromaApplied { get; set; }

            public string DenoiseLevel { get; set; } = "off";

            /// <summary>The ffmpeg filter node for the denoise level, or null.</summary>
            public string DenoiseFilter { get; set; }

            /// <summary>True when the denoise node needs Vulkan frames, i.e. goes after hwupload.</summary>
            public bool DenoiseWantsHwFrames { get; set; }

            /// <summary>
            /// The Vulkan denoise level that was dropped because this session also needs the
            /// patched binary, whose shaderc cannot compile that filter's shader. Null when
            /// nothing was dropped. Reported rather than silently applied.
            /// </summary>
            public string DenoiseDroppedForPatchedBinary { get; set; }

            public string DeblockLevel { get; set; } = "off";

            /// <summary>The deblock level asked for, whether or not this build could run it.</summary>
            public string DeblockRequested { get; set; } = "off";

            /// <summary>The ffmpeg filter node for the deblock level, or null.</summary>
            public string DeblockFilter { get; set; }

            /// <summary>True when the deblock node needs Vulkan frames. No level here does today.</summary>
            public bool DeblockWantsHwFrames { get; set; }

            public bool DeblockApplied { get; set; }

            public string NeuralLevel { get; set; } = "off";

            /// <summary>
            /// The neural level the session or the dashboard asked for, before the ratio decided
            /// which weight of that family actually runs. NeuralLevel carries what ran, so without
            /// this a substituted weight would be indistinguishable from the one chosen.
            /// </summary>
            public string NeuralRequested { get; set; } = "off";

            public string GameLevel { get; set; } = "off";

            /// <summary>The ffmpeg filter node for the neural super-resolution level, or null.</summary>
            public string NeuralFilter { get; set; }

            /// <summary>The ort device the node asked for ("openvino", "cpu", "cuda"), or null when no ONNX neural level is in the command.</summary>
            public string NeuralDevice { get; set; }

            /// <summary>Why NeuralDevice is "cpu" on an Intel server, or null.</summary>
            public string NeuralDeviceNote { get; set; }

            /// <summary>Why the game level's motion vectors are zero (Intel, no CUDA), or null. See SessionRecord.GameMotionNote.</summary>
            public string GameMotionNote { get; set; }

            /// <summary>What NeuralMaxSourceHeight did to this session, or null. See SessionRecord.NeuralRouting.</summary>
            public string NeuralRouting { get; set; }

            /// <summary>The ffmpeg filter node for the game upscaler level, or null.</summary>
            public string GameFilter { get; set; }

            public bool NeuralApplied { get; set; }

            public bool GameApplied { get; set; }

            /// <summary>
            /// True when NeuralLevel is dlpp-1..4 or vsr-rtcuda: both need AV_PIX_FMT_CUDA frames
            /// straight from decode, so BuildChain takes a separate, much shorter path for this
            /// session (decode cuda -> [optix] -> the neural filter(s) -> NVENC, no Vulkan, no
            /// libplacebo) and HwaccelArgs/the two hwaccel-suppression patches must leave decode's
            /// own -hwaccel cuda alone instead of forcing system memory. See
            /// INTEGRATION_DESIGN.md section 2.
            /// </summary>
            public bool UsesCudaNeural { get; set; }

            /// <summary>
            /// The source is stored sideways (rotation of 90 or 270 degrees), so Jellyfin turns it
            /// upright in the chain - with transpose_cuda on the CUDA path. The plugin's own size
            /// arithmetic already works on the upright picture; this lets the CUDA-native branch
            /// hand the final width to ffmpeg to work out from the frame that really arrives.
            /// </summary>
            public bool SourceSideways { get; set; }

            /// <summary>
            /// The bare "optix" node (no format=gbrpf32le wrap) to run ahead of the CUDA-native
            /// neural filter(s), or null when this session's denoise choice does not carry over
            /// (see ShaderLibrary.CudaDenoiseFilter - only optix/optix-temporal qualify).
            /// </summary>
            public string CudaDenoiseNode { get; set; }

            /// <summary>The resolved jitter / depth / reactive the game filter node was built with.</summary>
            public string GameJitter { get; set; }

            public string GameDepth { get; set; }

            public string GameReactive { get; set; }

            /// <summary>Depth fell back to flat because the weights are not installed.</summary>
            public bool GameDepthDowngraded { get; set; }

            public string ShaderPath { get; set; }

            /// <summary>The session explicitly asked for a larger picture.</summary>
            public bool ClientOptIn { get; set; }

            /// <summary>True when the SR level does its own sharpening and the RCAS pass was dropped.</summary>
            public bool SrOwnsSharpening { get; set; }

            /// <summary>The libplacebo scaling kernel for this job.</summary>
            public string Upscaler { get; set; }

            public string Status { get; set; } = "not-requested";

            public string Reason { get; set; } = string.Empty;

            /// <summary>
            /// Levels asked for and dropped because they need NVIDIA and this server's GPU is Intel,
            /// as "axis level" pairs joined by ", ". Null when none.
            /// </summary>
            public string GpuWithheld { get; set; }

            /// <summary>
            /// True when the command decodes on the Intel GPU and maps the VA-API surfaces to Vulkan
            /// (IntelHardwareDecode, GPU_VENDORS.md 5.6). Decided once in Decide, read by HwaccelArgs,
            /// BuildChain and the hwaccel patches, so the three can never disagree.
            /// </summary>
            public bool HardwareDecode { get; set; }

            /// <summary>The pixel format the chain downloads in after a hardware decode, derived from the source's pixel format by HardwareSurfaceFormat. Set together with HardwareDecode.</summary>
            public string HardwareDecodeFormat { get; set; }

            /// <summary>Why HardwareDecode is what it is. Null when the setting is off or there is no detected Intel GPU, which keeps every other command and record as before.</summary>
            public string HardwareDecodeReason { get; set; }

            public static Plan No(string status, string reason) =>
                new Plan { Act = false, Status = status, Reason = reason };
        }

        /* ------------------------------------------------------------------ session bookkeeping */

        public static IReadOnlyList<SessionRecord> History
        {
            get
            {
                lock (_historyLock)
                {
                    return _history.ToArray();
                }
            }
        }

        public static SessionRecord ForSession(string playSessionId)
        {
            if (string.IsNullOrEmpty(playSessionId))
            {
                return null;
            }

            return _bySession.TryGetValue(playSessionId, out var record) ? record : null;
        }

        public static void Record(SessionRecord record)
        {
            if (!string.IsNullOrEmpty(record.PlaySessionId))
            {
                _bySession[record.PlaySessionId] = record;
            }

            lock (_historyLock)
            {
                _history.Insert(0, record);
                if (_history.Count > HistoryLimit)
                {
                    foreach (var dropped in _history.Skip(HistoryLimit).ToList())
                    {
                        // Record runs more than once per session - GetVideoEncoder and the filter
                        // patch are separate call sites - so an OLDER record for a session that is
                        // still playing can fall off the tail while the live one is in the
                        // dictionary. Dropping the key on that would make the per-session endpoint
                        // answer "unknown" mid-playback and lose the parked encoder with it, so the
                        // entry only goes when it is still this very record.
                        if (!string.IsNullOrEmpty(dropped.PlaySessionId)
                            && _bySession.TryGetValue(dropped.PlaySessionId, out var current)
                            && ReferenceEquals(current, dropped))
                        {
                            _bySession.TryRemove(dropped.PlaySessionId, out _);
                            _encoderBySession.TryRemove(dropped.PlaySessionId, out _);
                        }
                    }

                    _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
                }
            }
        }

        /// <summary>The key a client can use to ask what happened to its own session.</summary>
        public static string SessionKey(EncodingJobInfo state)
        {
            try
            {
                if (state?.BaseRequest is StreamingRequestDto dto && !string.IsNullOrEmpty(dto.PlaySessionId))
                {
                    return dto.PlaySessionId;
                }

                return state?.BaseRequest?.GetOption("playSessionId");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The viewer this transcode belongs to, as a lowercase "N"-format GUID, or null when the
        /// request carried nothing to identify one.
        ///
        /// Found by reflection rather than by a typed property on purpose: which member carries the
        /// user differs between the request DTO and the job state across Jellyfin versions, and this
        /// assembly is loaded beside a server it was not compiled against. A miss here is not fatal
        /// - it yields null, and the endpoint then falls back to the behaviour it had before, which
        /// is to answer anyone. It must never throw into the transcode path.
        /// </summary>
        public static string UserKey(EncodingJobInfo state)
        {
            try
            {
                object[] roots = { state?.BaseRequest, state };
                foreach (var root in roots)
                {
                    if (root == null)
                    {
                        continue;
                    }

                    var type = root.GetType();
                    object value = type.GetProperty("UserId")?.GetValue(root);
                    if (value == null)
                    {
                        object user = type.GetProperty("User")?.GetValue(root);
                        value = user?.GetType().GetProperty("Id")?.GetValue(user);
                    }

                    if (value is Guid guid)
                    {
                        if (guid != Guid.Empty)
                        {
                            return guid.ToString("N", CultureInfo.InvariantCulture);
                        }

                        continue;
                    }

                    string text = value?.ToString();
                    if (!string.IsNullOrEmpty(text) && Guid.TryParse(text, out var parsed) && parsed != Guid.Empty)
                    {
                        return parsed.ToString("N", CultureInfo.InvariantCulture);
                    }
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static SessionRecord Describe(EncodingJobInfo state, Plan plan, string status, string reason)
        {
            DeviceInit devices = plan.Act ? Devices() : null;
            var record = new SessionRecord
            {
                Timestamp = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
                PlaySessionId = SessionKey(state),
                UserId = UserKey(state),
                Source = state?.MediaPath,
                SourceWidth = plan.SourceWidth,
                SourceHeight = plan.SourceHeight,
                OutputWidth = plan.Act ? plan.Width : plan.SourceWidth,
                OutputHeight = plan.Act ? plan.Height : plan.SourceHeight,
                UpscaleApplied = plan.Act && plan.UpscaleApplied,
                DeblurApplied = plan.Act && plan.DeblurApplied,
                DenoiseApplied = plan.Act && plan.DenoiseApplied,
                DeblockApplied = plan.Act && plan.DeblockApplied,
                NeuralApplied = plan.Act && plan.NeuralApplied,
                GameApplied = plan.Act && plan.GameApplied,
                DebandApplied = plan.Act && plan.DebandApplied,
                DeblurLevel = plan.Act && plan.DeblurApplied ? plan.DeblurLevel : "off",
                SrLevel = plan.Act && plan.UpscaleApplied ? plan.SrLevel : "off",
                RefineLevel = plan.Act && plan.RefineApplied ? plan.RefineLevel : "off",
                RefineApplied = plan.Act && plan.RefineApplied,
                ChromaLevel = plan.Act && plan.ChromaApplied ? plan.ChromaLevel : "off",
                ChromaApplied = plan.Act && plan.ChromaApplied,
                SrRequested = plan.SrRequested ?? "off",
                // Unconditional, unlike NeuralLevel below: what was asked for, whether or not it ran.
                NeuralRequested = plan.NeuralRequested ?? "off",
                SrBypassed = plan.Act && plan.SrBypassed,
                SrOwnsSharpening = plan.Act && plan.SrOwnsSharpening,
                Upscaler = plan.Act ? plan.Upscaler : null,
                DenoiseLevel = plan.Act && plan.DenoiseApplied ? plan.DenoiseLevel : "off",
                DeblockLevel = plan.Act && plan.DeblockApplied ? plan.DeblockLevel : "off",
                // Unconditional, like NeuralRequested: what was asked for, run or not.
                DeblockRequested = plan.DeblockRequested ?? "off",
                NeuralLevel = plan.Act && plan.NeuralApplied ? plan.NeuralLevel : "off",
                NeuralDevice = plan.Act && plan.NeuralApplied ? plan.NeuralDevice : null,
                NeuralDeviceNote = plan.Act && plan.NeuralApplied ? plan.NeuralDeviceNote : null,
                NeuralRouting = plan.Act ? plan.NeuralRouting : null,
                GameLevel = plan.Act && plan.GameApplied ? plan.GameLevel : "off",
                GameMotionNote = plan.Act && plan.GameApplied ? plan.GameMotionNote : null,
                GameJitter = plan.Act && plan.GameApplied ? plan.GameJitter : null,
                GameDepth = plan.Act && plan.GameApplied ? plan.GameDepth : null,
                GameReactive = plan.Act && plan.GameApplied ? plan.GameReactive : null,
                GameDepthDowngraded = plan.Act && plan.GameApplied && plan.GameDepthDowngraded,
                DenoiseDroppedForPatchedBinary = plan.Act ? plan.DenoiseDroppedForPatchedBinary : null,
                CudaNeuralBypass = plan.Act && plan.UsesCudaNeural,
                // Same condition BuildChain's own UsesCudaNeural branch uses to decide whether it
                // builds the hwmap bridge (deband/kernel-scale/refine/chroma/deblur) - kept as the
                // same field checks rather than a shared helper because Plan itself lives in this
                // file already and duplicating five field reads costs less than a cross-call
                // dependency for a value this simple. If BuildChain's own condition ever changes,
                // this one has to move with it.
                Pipeline = !plan.Act
                    ? null
                    : plan.UsesCudaNeural
                        ? ((plan.UpscaleApplied || plan.DeblurApplied || plan.RefineApplied
                                || plan.ChromaApplied || plan.DebandApplied)
                            ? "CUDA+Vulkan (RTX bridge)"
                            : "CUDA (RTX)")
                        : (GpuResidentEncodeOn() ? "Vulkan (GPU-resident)" : "Vulkan"),
                GpuResidentEncode = !plan.Act || plan.UsesCudaNeural
                    ? null
                    : IsIntelPath() ? "n/a" : (Settings?.GpuResidentEncode == true ? "on" : "off"),
                VulkanDevice = devices?.VulkanText,
                QsvDevice = devices?.QsvText,
                GpuWithheld = plan.Act ? plan.GpuWithheld : null,
                HardwareDecode = plan.Act && plan.HardwareDecodeReason != null ? plan.HardwareDecode : (bool?)null,
                HardwareDecodeReason = plan.Act ? plan.HardwareDecodeReason : null,
                Status = status,
                Reason = reason,

                // Current never throws and asks the machine once per process, so after the first
                // session it costs the transcode path one small allocation (plus a file-exists
                // check on Intel); the encode family is resolved against ffmpeg's cached list.
                Gpu = Capability(),
            };

            // GetVideoEncoder and GetVideoProcessingFilterParam are separate call sites and
            // Jellyfin does not promise an order, so the encoder decision is parked by session id
            // and picked up by whichever of the two builds the record second.
            if (!string.IsNullOrEmpty(record.PlaySessionId)
                && _encoderBySession.TryGetValue(record.PlaySessionId, out var enc))
            {
                record.Encoder = enc.Item1;
                record.EncoderReason = enc.Item2;
            }

            record.Summary = Summarise(record);
            return record;
        }

        /// <summary>Which sharpener a deblur level actually is, for the honest report.</summary>
        private static string SharpenerName(string level)
        {
            if (level == null)
            {
                return "RCAS";
            }

            if (level.StartsWith("cas-", StringComparison.OrdinalIgnoreCase))
            {
                return "CAS";
            }

            return level.StartsWith("nvsharpen", StringComparison.OrdinalIgnoreCase)
                ? "NVIDIA Image Sharpening"
                : "RCAS";
        }

        /// <summary>
        /// Which network a neural level actually is, for the honest report. The level name says
        /// the family and the factor; this names the weights file that ran and the engine that
        /// ran it, so the record names the model rather than an opaque level.
        /// </summary>
        private static string NeuralName(string level, string device = null, string note = null)
        {
            // RTX VSR bypass resampler: a fast GPU resample, not a network - the filter's own
            // header comment is explicit about this, and the report must not overclaim it (see
            // INTEGRATION_DESIGN.md section 4). No "-x" model factor, no ONNX Runtime.
            if (ShaderLibrary.IsVsrRtcudaLevel(level))
            {
                return "RTX VSR bypass resample (nvaivpx.dll), NOT a neural network - "
                    + "fast GPU resample, measured better than bilinear, no detail added";
            }

            // RTX DLPP: content-dependent, never negative but never large either (the filter's
            // own DEGRADED AVOption text) - do not present the four levels as a ladder.
            if (ShaderLibrary.IsDlppLevel(level))
            {
                return "RTX DLPP level " + level.Trim().Substring("dlpp-".Length)
                    + " (nvdlppx.dll), GPU only - the gain depends on the picture, and a higher "
                    + "level is not simply better";
            }

            string path = ShaderLibrary.NeuralModelPath(level, Settings);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "none";
            }

            // On an Intel GPU there is no CUDA execution provider. The command asks for the OpenVINO
            // provider when it is installed and the CPU one when it is not (NeuralDeviceNow), and
            // says which; naming CUDA there would be wrong. device is null for a record built
            // without a plan that went through Decide, which falls back to the server's GPU.
            string engine;
            if (device == "openvino")
            {
                engine = "ONNX Runtime with the OpenVINO provider on the Intel GPU (asked for, not measured "
                    + "for this chain; ffmpeg's log says if it ran on the CPU instead)";
            }
            else if (device == "cpu" || (device == null && IsIntelPath()))
            {
                engine = "ONNX Runtime; this server's GPU is Intel, so no CUDA and the CPU provider"
                    + (string.IsNullOrEmpty(note) ? ", not measured for this chain" : " (" + note + ")");
            }
            else
            {
                engine = "ONNX Runtime CUDA";
            }

            return System.IO.Path.GetFileName(path) + ", Real-ESRGAN compact (SRVGGNetCompact), " + engine;
        }

        /// <summary>
        /// Which ffmpeg filter a denoise level actually is, for the honest report. The level names
        /// are a cost ladder that spans two filter families (atadenoise, then nlmeans), so naming
        /// the level alone would not tell the viewer what ran.
        /// </summary>
        private static string DenoiserName(string level)
        {
            string filter = ShaderLibrary.DenoiseFilter(level, out _, out _);
            if (string.IsNullOrWhiteSpace(filter))
            {
                return "none";
            }

            // A level may be a small chain rather than one node - the OIDN levels carry the
            // format= conversions the filter needs around them. Report the filter that actually
            // denoises, not the plumbing, and name OIDN in full because it is the one level that
            // does not run on the stock jellyfin-ffmpeg at all.
            foreach (string node in SplitFilters(filter))
            {
                if (node.StartsWith("format=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (node.StartsWith("oidn", StringComparison.OrdinalIgnoreCase))
                {
                    return node + ", Intel Open Image Denoise";
                }

                // OptiX has two models behind one filter name and they are not interchangeable -
                // the temporal one is the whole point of the level - so the report has to say
                // WHICH one ran, not just that OptiX did.
                if (node.StartsWith("optix", StringComparison.OrdinalIgnoreCase))
                {
                    if (node.IndexOf("mode=temporal", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return node + ", NVIDIA OptiX AI denoiser, temporal model with NVOFA motion vectors";
                    }

                    return node + ", NVIDIA OptiX AI denoiser, "
                        + (node.IndexOf("mode=hdr", StringComparison.OrdinalIgnoreCase) >= 0 ? "HDR" : "LDR")
                        + " spatial model";
                }

                return node;
            }

            return filter;
        }

        /// <summary>
        /// Which ffmpeg filter a deblock level actually is, for the honest report. Same reason as
        /// DenoiserName: the level names span three filter families (deblock, fspp, pp7), so the
        /// level alone does not say what ran, and only two of the three attack ringing.
        /// </summary>
        private static string DeblockerName(string level)
        {
            string filter = ShaderLibrary.DeblockFilter(level, out _, out _);
            if (string.IsNullOrWhiteSpace(filter))
            {
                return "none";
            }

            if (filter.StartsWith("deblock", StringComparison.OrdinalIgnoreCase))
            {
                return filter + ", libavfilter deblocking on the coding grid, no deringing";
            }

            if (filter.StartsWith("fspp", StringComparison.OrdinalIgnoreCase))
            {
                return filter + ", libpostproc fast simple post-processing, deblock and dering";
            }

            return filter + ", libpostproc";
        }

        /// <summary>
        /// Which pass actually produced the output size, for the "Upscaled WxH to WxH (...)" line.
        /// Pulled out of Summarise() because it was one nested ternary answering four different
        /// "who resized this" cases, which is exactly the shape a fifth case (this method's own
        /// reason for existing: the CUDA-native neural branch) gets bolted onto wrong instead of
        /// added cleanly. A game upscaler hands libplacebo a picture already at the target size,
        /// so the scale is a no-op and the SR level was forced off to avoid enlarging twice. A
        /// CUDA-native neural session (dlpp-*/vsr-rtcuda) is the same story: it takes
        /// UpscaleEngine's separate CUDA hwaccel branch (see Decide()), the Vulkan/SR shader chain
        /// never runs, and the resize was actually done by vsr_rtcuda (levels 3/4 conforming
        /// dlpp_rtcuda's fixed 2x output to the real target, or vsr-rtcuda alone) or by
        /// dlpp_rtcuda itself (levels 1/2, which take the requested size directly - see
        /// ShaderLibrary.CudaNeuralFilter). Reporting any of those as "plain scaling" names a pass
        /// that never ran (libplacebo's own scaler) and hides the one that did - the exact honesty
        /// failure AGENTS.md's reporting invariant exists to catch.
        /// </summary>
        private static string ResizeCredit(SessionRecord r)
        {
            if (!string.Equals(r.SrLevel, "off", StringComparison.OrdinalIgnoreCase))
            {
                return r.SrLevel;
            }

            if (r.GameApplied && !string.IsNullOrEmpty(r.GameLevel)
                && !string.Equals(r.GameLevel, "off", StringComparison.OrdinalIgnoreCase))
            {
                return r.GameLevel + ", which produced the output size itself";
            }

            if (r.CudaNeuralBypass && ShaderLibrary.IsVsrRtcudaLevel(r.NeuralLevel))
            {
                return "vsr_rtcuda, fast GPU resample";
            }

            if (r.CudaNeuralBypass && ShaderLibrary.IsDlppLevel(r.NeuralLevel))
            {
                // vsr_rtcuda resizes after dlpp at every level while it is available; levels 1/2
                // only take the size themselves when it is not (CudaNeuralFilter).
                return ShaderLibrary.DlppLevelNumber(r.NeuralLevel) >= 3 || ShaderLibrary.RtxVsrOffered(Settings)
                    ? "vsr_rtcuda, conforming " + r.NeuralLevel + "'s fixed 2x output to this size"
                    : r.NeuralLevel + ", native output size";
            }

            return "plain scaling";
        }

        private static string Summarise(SessionRecord r)
        {
            if (r.Status != "applied")
            {
                switch (r.Status)
                {
                    case "concurrency-cap":
                        return "Enhancement requested but not applied (all upscale slots busy)";
                    case "subtitle-burn-in":
                        return "Enhancement not applied (burned-in subtitles)";
                    case "stream-copy":
                        return "Enhancement not applied (video is being streamed as-is)";
                    case "disabled":
                        return "Enhancement disabled on the server";
                    case "off-by-client":
                        return "No enhancement (the viewer selected Off; playing as Jellyfin would)";
                    default:
                        return "No enhancement";
                }
            }

            var parts = new List<string>();

            // First in the summary because it is first in the chain: it runs at source resolution,
            // ahead of everything below, on the damage the source codec did rather than on grain.
            if (r.DeblockApplied)
            {
                parts.Add("Deblock " + r.DeblockLevel + " (" + DeblockerName(r.DeblockLevel) + ", source resolution, before the scale)");
            }
            else if (!string.IsNullOrEmpty(r.DeblockRequested)
                && !string.Equals(r.DeblockRequested, "off", StringComparison.OrdinalIgnoreCase))
            {
                // Said out loud, like SrBypassed: this axis can be asked for and dropped because
                // this ffmpeg build has no such filter, and a viewer who picked a level is entitled
                // to know it did not run rather than reading a summary that simply omits it.
                parts.Add("deblock " + r.DeblockRequested + " not run (this ffmpeg build does not carry the filter)");
            }

            if (r.DenoiseApplied)
            {
                parts.Add("Denoise " + r.DenoiseLevel + " (" + DenoiserName(r.DenoiseLevel) + ")");
            }

            if (r.NeuralApplied)
            {
                parts.Add("Neural SR " + r.NeuralLevel + " (" + NeuralName(r.NeuralLevel, r.NeuralDevice, r.NeuralDeviceNote) + ")");

                // Say which weight ran when it is not the one named, for the same reason SrBypassed
                // is said out loud: the viewer picked a level and is entitled to know what ran.
                if (!string.IsNullOrEmpty(r.NeuralRequested)
                    && !string.Equals(r.NeuralRequested, r.NeuralLevel, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} run in place of {1} at {2:0.00}x (the larger weight's extra pixels would "
                            + "have been scaled straight back off)",
                        r.NeuralLevel,
                        r.NeuralRequested,
                        r.SourceHeight > 0 ? (double)r.OutputHeight / r.SourceHeight : 0));
                }
            }

            // What NeuralMaxSourceHeight did, in its own words, whether or not a network ran.
            if (!string.IsNullOrEmpty(r.NeuralRouting))
            {
                parts.Add(r.NeuralRouting);
            }

            if (r.GameApplied)
            {
                // The degraded wording is not optional and is not softened. A viewer reading
                // this report must not believe they are getting what a game gets.
                parts.Add("Game upscaler " + r.GameLevel + " (" + ShaderLibrary.GameLabel(r.GameLevel)
                    + "; jitter " + (r.GameJitter ?? "?")
                    + ", depth " + (r.GameDepth ?? "?")
                    + (r.GameDepthDowngraded ? " - the depth weights are not installed, so flat is what ran" : string.Empty)
                    + ", reactive " + (r.GameReactive ?? "?") + ")");
                if (!string.IsNullOrEmpty(r.GameMotionNote))
                {
                    parts.Add(r.GameMotionNote);
                }
            }

            // Never silent. The viewer asked for a denoise and did not get it, and the reason is
            // a property of this build rather than of their choice, so it says which and why.
            if (!string.IsNullOrEmpty(r.DenoiseDroppedForPatchedBinary))
            {
                parts.Add("denoise " + r.DenoiseDroppedForPatchedBinary
                    + " NOT run: it needs the stock ffmpeg's shader compiler, and this session also"
                    + " uses a filter only the patched binary carries");
            }

            // Same rule, and the reason is a property of the server's GPU, not of the viewer's choice.
            if (!string.IsNullOrEmpty(r.GpuWithheld))
            {
                parts.Add(r.GpuWithheld + " NOT run: needs an NVIDIA GPU and this server's is Intel");
            }

            // Only present while IntelHardwareDecode is on, so every other summary is unchanged.
            if (!string.IsNullOrEmpty(r.HardwareDecodeReason))
            {
                parts.Add(r.HardwareDecodeReason);
            }

            if (r.UpscaleApplied)
            {
                // Name the shader family rather than assuming FSRCNNX: two families are selectable.
                parts.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Upscaled {0}x{1} to {2}x{3} ({4})",
                    r.SourceWidth,
                    r.SourceHeight,
                    r.OutputWidth,
                    r.OutputHeight,
                    ResizeCredit(r)));
            }

            if (r.DeblurApplied)
            {
                parts.Add("Unblur " + r.DeblurLevel + " (" + SharpenerName(r.DeblurLevel) + ")");
            }

            // These two are named in full because neither is an SR level and a viewer reading
            // "Upscaled ... (fsrcnnx)" would otherwise have no way to tell that a second and a
            // third shader pass also ran.
            if (r.RefineApplied)
            {
                parts.Add("Refine " + r.RefineLevel + " (SSimSuperRes, post-scale)");
            }

            if (r.ChromaApplied)
            {
                parts.Add("Chroma " + r.ChromaLevel + " (KrigBilateral, 4:2:0 chroma upscaling)");
            }

            // Say it out loud rather than quietly reporting "plain scaling": the viewer picked a
            // super-resolution level and is entitled to know it was not run, and why.
            if (r.SrBypassed)
            {
                parts.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} not run at {1:0.00}x (below the {2:0.00}x super-resolution threshold; "
                        + "plain scaling measured as good there and costs far less)",
                    r.SrRequested,
                    r.SourceHeight > 0 ? (double)r.OutputHeight / r.SourceHeight : 0,
                    Settings?.SrMinScaleFactor ?? 0));
            }

            if (r.SrOwnsSharpening)
            {
                parts.Add("unblur left to " + r.SrLevel + ", which sharpens internally");
            }

            if (r.DebandApplied)
            {
                parts.Add("deband");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : "No enhancement";
        }

        /* ------------------------------------------------------------------------- the decision */

        /// <summary>
        /// Every axis this plugin reads off the request, in one place, because two things now need
        /// the same list: deciding whether a session named anything, and remembering what it named.
        /// </summary>
        private static readonly string[] _axisNames =
        {
            "upscale", "maxheight", "sr", "deblur", "deblock", "denoise", "neural", "game",
            "refine", "chroma", "deband", "kernel", "jitter", "depth", "reactive"
        };

        // The axes a play session arrived with, kept so a later request for the SAME session that
        // lost them can be answered with what the viewer actually chose. Bounded, and evicted with
        // the session record, so a long-running server does not accumulate them.
        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> _optionsBySession =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// THE PARAMETERS DO NOT SURVIVE THE HLS MASTER PLAYLIST.
        ///
        /// The client marks the TranscodingUrl, which is the master playlist. Jellyfin then writes
        /// the VARIANT urls into that playlist itself, and it writes the parameters it knows about,
        /// not ours. The browser fetches the variant, and that request - the one that actually
        /// builds the ffmpeg command - arrives with every axis missing, so the server falls back to
        /// its dashboard defaults and honestly reports having done so. The viewer sees their picks
        /// in the panel, a chain that is not theirs on the stream, and nothing anywhere saying why.
        ///
        /// PlaySessionId does survive that hop, so the axes are remembered against it the first
        /// time they are seen and restored for later requests of the same session that lack them.
        /// A request that carries axes always wins: this only ever fills a gap.
        /// </summary>
        private static void RememberOrRestoreOptions(EncodingJobInfo state)
        {
            try
            {
                string key = SessionKey(state);
                if (string.IsNullOrEmpty(key))
                {
                    return;
                }

                var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string axis in _axisNames)
                {
                    string value = RawOption(state, axis);
                    if (value != null)
                    {
                        present[axis] = value;
                    }
                }

                if (present.Count > 0)
                {
                    // Bounded the crude way rather than leaked: this holds a handful of short
                    // strings per session, and the cap is far above any real concurrent count.
                    if (_optionsBySession.Count > 256)
                    {
                        _optionsBySession.Clear();
                    }

                    _optionsBySession[key] = present;
                    return;
                }

                // Nothing on this request: restore what the session arrived with, if anything, by
                // writing it back onto the request so every existing read path sees it unchanged.
                if (!_optionsBySession.TryGetValue(key, out var remembered) || remembered == null)
                {
                    return;
                }

                foreach (var pair in remembered)
                {
                    try
                    {
                        // Indexer, not Add: Add throws on a key that is already there, and this
                        // runs on the transcode path where that would be a failed session.
                        var opts = state?.BaseRequest?.StreamOptions;
                        if (opts != null)
                        {
                            opts[pair.Key] = pair.Value;
                        }
                    }
                    catch (Exception)
                    {
                        // A collection that refuses the write leaves the axis unset, which is the
                        // behaviour before this existed rather than a new failure.
                    }
                }
            }
            catch (Exception)
            {
                // Never throw into the transcode path for a convenience.
            }
        }

        private static string RawOption(EncodingJobInfo state, string name)
        {
            try
            {
                string value = state?.BaseRequest?.GetOption(name);
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Option(EncodingJobInfo state, string name)
        {
            return RawOption(state, name);
        }

        /// <summary>
        /// True when this session named an enhancement axis itself, as opposed to inheriting a
        /// dashboard default.
        ///
        /// The difference decides whether a stream copy is worth replacing with a full transcode.
        /// A plan's *Applied flags cannot answer it: they are equally true for a dashboard default,
        /// so reading them there would turn every direct-play-eligible session on the server into a
        /// GPU transcode the moment an admin set one of those defaults.
        /// </summary>
        public static bool SessionNamedEnhancement(EncodingJobInfo state)
        {
            string[] axes = { "sr", "deblur", "deblock", "denoise", "neural", "game", "refine", "chroma", "deband", "kernel", "jitter", "depth", "reactive" };
            foreach (string axis in axes)
            {
                if (Option(state, axis) != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Works out what this job should get. Session options win; the dashboard settings are the
        /// fallback, which is what keeps the features working when the player UI is not there.
        /// </summary>
        public static Plan Decide(EncodingJobInfo state)
        {
            try
            {
                // Before anything reads an axis: the HLS variant request arrives without them, and
                // this is where the session's own choices come back. See RememberOrRestoreOptions.
                RememberOrRestoreOptions(state);

                UpscaleSettings cfg = Settings;
                if (cfg == null || !cfg.Enabled)
                {
                    return Plan.No("disabled", "plugin disabled");
                }

                if (state == null || !state.IsVideoRequest)
                {
                    return Plan.No("ineligible", "not a video request");
                }

                var vs = state.VideoStream;
                if (vs?.Width == null || vs.Height == null || vs.Width <= 0 || vs.Height <= 0)
                {
                    return Plan.No("ineligible", "unknown source size");
                }

                (int sw, int sh) = UprightSize(vs.Width.Value, vs.Height.Value, vs.Rotation);

                var plan = new Plan
                {
                    SourceWidth = sw, SourceHeight = sh, Width = sw, Height = sh,
                    SourceSideways = IsQuarterTurn(vs.Rotation),
                };

                // A source that never declared its range is the one case libplacebo has to guess
                // at, and guessing full on limited material lifts black by about 9/255 across the
                // whole frame. That is larger than any shader difference this plugin measures, and
                // it was previously corrected only inside the benchmark harness, so the served
                // segment carried a shift the measurements did not. Video is limited range unless
                // it says otherwise, so an untagged source is told so before anything scales it.
                plan.RangeUntagged = string.IsNullOrWhiteSpace(vs.ColorRange);

                // ---- upscale target -------------------------------------------------------
                // "upscale" is a lowercase query parameter, so Jellyfin puts it in the request's
                // StreamOptions verbatim (Jellyfin.Api ParseStreamOptions). Unlike a bitrate or a
                // maxHeight it is never clamped or rewritten on the way through.
                string upscaleOption = Option(state, "upscale");
                int? requested = null;

                // "SAID NOTHING" AND "SAID OFF" ARE DIFFERENT ANSWERS.
                //
                // A session carrying no upscale marker at all has expressed no opinion, so the
                // dashboard defaults apply - that is what RequireClientOptIn=false means and it is
                // deliberately left alone. A session carrying upscale=off has expressed one, and
                // the only honest reading of it is stock Jellyfin behaviour: no upscale, and no
                // server-side deblur or denoise default either, so nothing turns a would-be direct
                // play or stream copy into a transcode. Only levels the session asked for BY NAME
                // survive an explicit Off.
                bool clientSaidOff = false;
                if (upscaleOption != null)
                {
                    if (string.Equals(upscaleOption, "off", StringComparison.OrdinalIgnoreCase))
                    {
                        requested = 0;
                        clientSaidOff = true;
                    }
                    else if (int.TryParse(upscaleOption, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    {
                        requested = parsed;
                    }
                }

                // Older clients (and the first version of gpu-upscale.js) signalled an upscale with
                // maxHeight. Only treat it as a request when it is actually ABOVE the source:
                // Jellyfin fills MaxHeight in from the device profile for ordinary playback too,
                // usually at or below the source height, and taking that as "the session asked for
                // this size" silently suppressed the RequireClientOptIn=false default - the job
                // was then rejected as "target within MinScaleFactor of source" and nothing was
                // enhanced. A maxHeight at or below the source is a ceiling, not a request.
                if (requested == null && state.BaseRequest?.MaxHeight > sh)
                {
                    requested = state.BaseRequest.MaxHeight;
                }

                int target = 0;
                if (requested.HasValue)
                {
                    target = requested.Value;
                    plan.ClientOptIn = target > sh;
                }
                else if (!cfg.RequireClientOptIn)
                {
                    target = cfg.TargetHeight;
                }

                string upscaleReason = null;
                if (target <= 0)
                {
                    upscaleReason = "no upscale requested";
                }
                else if (sw < sh && (sw >= PortraitUltraHdEdge || sh >= PortraitUltraHdEdge))
                {
                    // Portrait source (width < height, a phone recording) that is already 4K or
                    // taller on its long edge. MaxSourceHeight's own check does not reliably catch
                    // this for portrait content: it compares sh against a landscape-tuned cutoff,
                    // and for a source this tall to begin with, running it up further is not the
                    // problem the ladder exists to solve. Below this edge, portrait content is
                    // offered the ladder exactly like landscape content is - the proportional
                    // target math a few lines down (tw = sw * (target/sh)) preserves aspect ratio
                    // regardless of which dimension is longer, so nothing else here needs to
                    // change for portrait, only this one bypass condition.
                    upscaleReason = "portrait source already 4K+, upscale bypassed";
                }
                else if (sh > cfg.MaxSourceHeight && sw >= sh)
                {
                    // Landscape only: MaxSourceHeight is tuned against a landscape source's height
                    // being the short edge, so it must not also catch a portrait source under the
                    // 4K bypass above - that case is handled entirely by the branch before this one.
                    upscaleReason = "source taller than MaxSourceHeight";
                }
                else
                {
                    target = Math.Min(target, cfg.MaxTargetHeight);
                    if (target <= sh * cfg.MinScaleFactor)
                    {
                        upscaleReason = "target within MinScaleFactor of source";
                    }
                    else
                    {
                        target -= target % 2;

                        // Width rounds to a multiple of 8, not 2. NVENC allocates surfaces on an
                        // 8-pixel alignment, so a width like 1918 is padded internally and the
                        // encoder works on a surface wider than the picture it is given. The
                        // nearest multiple of 8 moves the width by at most 4 pixels, which on a
                        // 16:9 target is inside a pixel of the exact aspect ratio.
                        int tw = (int)Math.Round(sw * ((double)target / sh) / 8.0, MidpointRounding.AwayFromZero) * 8;
                        if (tw > 0)
                        {
                            plan.UpscaleApplied = true;
                            plan.Width = tw;
                            plan.Height = target;
                        }
                        else
                        {
                            upscaleReason = "bad computed width";
                        }
                    }
                }

                // ---- sharpening -----------------------------------------------------------
                string deblurDefault = clientSaidOff ? "off" : cfg.DeblurLevel;
                string deblurLevel = cfg.DeblurAllowed ? (Option(state, "deblur") ?? deblurDefault) : "off";
                // A VALUE THAT FAILS ITS CHECK FALLS BACK TO THE COMPUTED DEFAULT, NOT THE
                // DASHBOARD ONE. The two are the same until the session says Off, and there the
                // dashboard value would defeat the explicit Off: a stale or misspelt level would
                // buy the viewer a server-side pass they asked not to have, and a transcode with
                // it. Every axis below is guarded the same way and for the same reason.
                if (!ShaderLibrary.IsDeblurLevel(deblurLevel))
                {
                    deblurLevel = ShaderLibrary.IsDeblurLevel(deblurDefault) ? deblurDefault : "off";
                }

                // ---- super-resolution level ----------------------------------------------
                // No clientSaidOff default of its own: an explicit Off leaves no upscale, and an
                // SR level without one is forced off below. An unusable value falls back to the
                // dashboard and then to "off" - never to a named network, which would put a level
                // nobody chose into SrRequested and report the viewer as having asked for it.
                string srLevel = Option(state, "sr") ?? cfg.SrLevel;
                if (!ShaderLibrary.IsSrLevel(srLevel))
                {
                    srLevel = ShaderLibrary.IsSrLevel(cfg.SrLevel) ? cfg.SrLevel : "off";
                }

                plan.SrRequested = ShaderLibrary.CanonicalSr(srLevel) ?? "off";

                // ---- post-scale refinement and chroma upscaling ---------------------------
                // Two axes of their own, carried on the same lowercase-query-parameter channel as
                // everything else. Neither is an SR level: the refinement hooks POSTKERNEL and the
                // chroma pass hooks CHROMA, so both compose with whatever SR level is in force
                // rather than replacing it. See ShaderLibrary._refineFiles / _chromaFiles.
                // Both gated the same way as neural and game: with the master switch off the
                // session's own parameter is not read at all, so the dashboard decides.
                string refineDefault = clientSaidOff ? "off" : cfg.RefineLevel;
                string refineLevel = cfg.RefineAllowed ? (Option(state, "refine") ?? refineDefault) : "off";
                if (!ShaderLibrary.IsRefineLevel(refineLevel))
                {
                    refineLevel = ShaderLibrary.IsRefineLevel(refineDefault) ? refineDefault : "off";
                }

                string chromaDefault = clientSaidOff ? "off" : cfg.ChromaLevel;
                string chromaLevel = cfg.ChromaAllowed ? (Option(state, "chroma") ?? chromaDefault) : "off";
                if (!ShaderLibrary.IsChromaLevel(chromaLevel))
                {
                    chromaLevel = ShaderLibrary.IsChromaLevel(chromaDefault) ? chromaDefault : "off";
                }

                if (!plan.UpscaleApplied)
                {
                    // The SR shaders only earn their pass when the output is meaningfully larger
                    // than the source, so at 1:1 they would cost a pass for nothing. The same is
                    // true of the refinement pass: SSimSuperRes corrects an enlargement, and its
                    // own //!WHEN guard would not fire at 1:1 anyway, so offering it there would be
                    // offering a pass that silently does nothing. The chroma pass is different -
                    // 4:2:0 chroma is subsampled whether or not the frame is being enlarged - so it
                    // is deliberately left alone here.
                    srLevel = "off";
                    refineLevel = "off";
                }
                else if (cfg.SrMinScaleFactor > 1.0
                    && !string.Equals(srLevel, "off", StringComparison.OrdinalIgnoreCase)
                    && !ShaderLibrary.SrIsRatioAgnostic(srLevel)
                    && plan.Height < sh * cfg.SrMinScaleFactor)
                {
                    // BELOW THE RATIO THE NETWORK IS WORTH RUNNING. Both shipped SR networks are
                    // fixed 2x, and below about 1.5x their output is shrunk back far enough that
                    // they measured no better than plain ewa_lanczos - at 1.41x they measured
                    // WORSE on detail energy - while costing ~15% of throughput. So drop the
                    // network and keep the rest of the chain: the upscale still happens, and the
                    // sharpener (RCAS, which costs about nothing) recovers more detail at these
                    // ratios than the network did. See UpscaleSettings.SrMinScaleFactor.
                    //
                    // NOT every SR level is fixed-2x. ravu-zoom is handed the output size directly
                    // and scales to it at any ratio, so the shrink-back argument above is simply
                    // untrue of it and SrIsRatioAgnostic exempts it. The refinement pass
                    // (SSimSuperRes) is exempt for the same reason and is not touched here at all -
                    // filling this gap with something better than a sharpener is the whole reason
                    // it was added.
                    plan.SrBypassed = true;
                    srLevel = "off";
                }

                // ---- denoise --------------------------------------------------------------
                // Same carrier as the others: a lowercase query parameter survives Jellyfin's
                // ParseStreamOptions into StreamOptions and is read back with GetOption.
                string denoiseDefault = clientSaidOff ? "off" : cfg.DenoiseLevel;
                string denoiseLevel = cfg.DenoiseAllowed ? (Option(state, "denoise") ?? denoiseDefault) : "off";
                if (!ShaderLibrary.IsDenoiseLevel(denoiseLevel))
                {
                    denoiseLevel = ShaderLibrary.IsDenoiseLevel(denoiseDefault) ? denoiseDefault : "off";
                }

                // The OptiX levels need CUDA frames. On a detected Intel GPU they are not offered
                // and not run, and the session says so (GPU_VENDORS.md 5.3); nothing else stands in.
                bool onIntel = IsIntelPath();
                denoiseLevel = Withhold(plan, onIntel, "denoise", denoiseLevel, ShaderLibrary.IsNvidiaOnlyDenoise(denoiseLevel));

                // ---- deblocking and deringing ---------------------------------------------
                // Same carrier and the same fallback rule as denoise, because it is the closest
                // existing shape: a pre-SR restoration pass that could in principle land either
                // side of hwupload. BuildChain puts this node AHEAD of the denoise one: this pass
                // attacks what the source encoder did, and handing a temporal denoiser a frame
                // whose block edges have already gone is the way round that gives the denoiser
                // fewer false edges to chase. Assumed, not measured, and the reverse order would be
                // defensible too; what is NOT in question is that both run at source resolution and
                // ahead of the super-resolution pass.
                string deblockDefault = clientSaidOff ? "off" : cfg.DeblockLevel;
                string deblockLevel = cfg.DeblockAllowed ? (Option(state, "deblock") ?? deblockDefault) : "off";
                if (!ShaderLibrary.IsDeblockLevel(deblockLevel))
                {
                    deblockLevel = ShaderLibrary.IsDeblockLevel(deblockDefault) ? deblockDefault : "off";
                }

                // Recorded before DeblockFilter can drop it: this axis is the one that can be a
                // real level and still not run, because the filter may be absent from this build.
                plan.DeblockRequested = ShaderLibrary.IsDeblockLevel(deblockLevel)
                    ? deblockLevel.Trim().ToLowerInvariant()
                    : "off";

                plan.DeblockFilter = ShaderLibrary.DeblockFilter(deblockLevel, out string deblockUsed, out bool deblockHw);
                plan.DeblockLevel = deblockUsed;
                plan.DeblockWantsHwFrames = deblockHw;
                plan.DeblockApplied = plan.DeblockFilter != null;

                plan.DenoiseFilter = ShaderLibrary.DenoiseFilter(denoiseLevel, out string denoiseUsed, out bool denoiseHw);
                plan.DenoiseLevel = denoiseUsed;
                plan.DenoiseWantsHwFrames = denoiseHw;
                plan.DenoiseApplied = plan.DenoiseFilter != null;

                // ---- neural super-resolution ----------------------------------------------
                // Its own axis, same carrier as the rest. Never inferred from anything: a session
                // gets a network only if it or the dashboard named one, because every level here
                // is well below realtime and choosing one for a viewer would be a decision this
                // plugin has no business making. The one exception is an operator who has set
                // NeuralMaxSourceHeight, which is that decision made on purpose (RouteNeuralBySourceHeight).
                string neuralDefault = clientSaidOff ? "off" : cfg.NeuralLevel;
                string neuralLevel = cfg.NeuralAllowed ? (Option(state, "neural") ?? neuralDefault) : "off";
                if (!ShaderLibrary.IsNeuralLevel(neuralLevel))
                {
                    neuralLevel = ShaderLibrary.IsNeuralLevel(neuralDefault) ? neuralDefault : "off";
                }

                plan.NeuralRequested = ShaderLibrary.IsNeuralLevel(neuralLevel)
                    ? neuralLevel.Trim().ToLowerInvariant()
                    : "off";

                // dlpp-1..4 and vsr-rtcuda are NVIDIA libraries over CUDA. NeuralRequested above keeps
                // what was asked for, so the record reads "asked for, did not run".
                neuralLevel = Withhold(plan, onIntel, "neural", neuralLevel, ShaderLibrary.IsNvidiaOnlyNeural(neuralLevel));

                // NeuralMaxSourceHeight: the ONNX levels are for small sources. 0 skips this whole
                // call, so a server that never set it takes exactly the path it always did.
                if (cfg.NeuralMaxSourceHeight > 0 && cfg.NeuralAllowed)
                {
                    neuralLevel = RouteNeuralBySourceHeight(plan, cfg, state, sh, neuralLevel, clientSaidOff);
                }

                plan.UsesCudaNeural = ShaderLibrary.IsCudaNeuralLevel(neuralLevel);

                if (plan.UsesCudaNeural)
                {
                    // CUDA-NATIVE BRANCH: dlpp-1..4 / vsr-rtcuda. Both filters need AV_PIX_FMT_CUDA
                    // frames straight from decode (RTXDLPP.md/RTXVSR.md). The Vulkan-CUDA interop
                    // this branch needs to also reach chroma/deband/scale(kernel)/refine/deblur -
                    // hwmap=derive_device=vulkan then back to hwmap=derive_device=cuda around one
                    // libplacebo pass, same node shape GpuResidentEncode's own hwmap path already
                    // uses for the Vulkan chain - was fixed 2026-09-24
                    // (ffmpeg/0007-cuda-to-vulkan-hwmap.patch, ffmpeg/0008-hwmap-chain-format.patch;
                    // see livetestbox.md for the three root causes and the real verification, incl.
                    // this exact bridge composed with dlpp_rtcuda). So this session still takes
                    // UpscaleEngine's separate CUDA hwaccel branch (see HwaccelArgs), but BuildChain
                    // now bridges through Vulkan for those five axes rather than skipping them -
                    // see BuildChain's own comment for the node order. Deblock, CPU-side denoise and
                    // the game temporal upscalers remain forced off below: none of that was part of
                    // this fix, and deblock/denoise already have their own drop-and-report path
                    // (plan.DenoiseDroppedForPatchedBinary) for a different, pre-existing reason.
                    plan.NeuralFilter = ShaderLibrary.CudaNeuralFilter(
                        neuralLevel, plan.Width, plan.Height, out string cudaNeuralUsed, cfg,
                        keepAspect: plan.SourceSideways);
                    plan.NeuralLevel = cudaNeuralUsed;
                    plan.NeuralApplied = plan.NeuralFilter != null;
                    plan.UsesCudaNeural = plan.NeuralApplied;

                    // Only optix/optix-temporal have a CUDA-hw-frame path (ARCHITECTURE.md); any
                    // other denoise choice does not carry over to this branch and is dropped, the
                    // same "reported, not silent" rule DenoiseDroppedForPatchedBinary already uses
                    // a few lines below for a different reason.
                    plan.CudaDenoiseNode = ShaderLibrary.CudaDenoiseFilter(denoiseLevel);
                    if (plan.CudaDenoiseNode == null && plan.DenoiseApplied)
                    {
                        plan.DenoiseDroppedForPatchedBinary = plan.DenoiseLevel;
                    }

                    plan.DenoiseFilter = null;
                    plan.DenoiseApplied = false;
                    plan.DenoiseLevel = "off";
                    plan.DenoiseWantsHwFrames = false;

                    // Deblock is a CPU pre-filter (ShaderLibrary.DeblockFilter), never routed
                    // into cudaNodes by BuildChain's CUDA-native branch below, which returns only
                    // CudaDenoiseNode + NeuralFilter. Left un-reset here, plan.DeblockApplied and
                    // plan.DeblockLevel would still say "applied" in the session record while
                    // BuildChain silently dropped the node from the command actually built - the
                    // exact "rendered, stored, never sent" bug this project keeps hitting.
                    plan.DeblockFilter = null;
                    plan.DeblockApplied = false;
                    plan.DeblockLevel = "off";
                    plan.DeblockWantsHwFrames = false;

                    // srLevel/refineLevel/chromaLevel/deblurLevel are NOT forced off here anymore
                    // (were, until the hwmap bridge above was fixed) - BuildChain now reaches them
                    // for a UsesCudaNeural session too, via hwmap=derive_device=vulkan around one
                    // libplacebo pass, then hwmap=derive_device=cuda back to the neural filter.
                    // Left as whatever was read above; deband/kernel below get the same treatment.
                }
                else
                {
                    // The heights go in because the weight that runs depends on the ratio: a x4
                    // network at a 2x target spends four times the pixels and libplacebo discards
                    // half of them.
                    //
                    // The device is asked for only on the Intel path (OpenVINO, or the CPU with the
                    // reason); every other server's node has no device option and so keeps the
                    // filter's default, CUDA, exactly as before.
                    var neuralDevice = NeuralDeviceNow();
                    plan.NeuralFilter = ShaderLibrary.NeuralFilter(
                        neuralLevel, plan.SourceHeight, plan.Height, out string neuralUsed, cfg, neuralDevice.Arg);
                    plan.NeuralLevel = neuralUsed;
                    plan.NeuralApplied = plan.NeuralFilter != null;
                    if (plan.NeuralApplied)
                    {
                        plan.NeuralDevice = neuralDevice.Name;
                        plan.NeuralDeviceNote = neuralDevice.Note;
                    }
                }

                // ---- game temporal upscalers (fsr2 / dlss / dlaa) --------------------------
                // Advanced, opt-in, off by default and never chosen automatically. See the
                // block above _gameFilters in ShaderLibrary for why they are labelled degraded.
                string gameDefault = clientSaidOff ? "off" : cfg.GameLevel;
                string gameLevel = cfg.GameAllowed ? (Option(state, "game") ?? gameDefault) : "off";
                if (!ShaderLibrary.IsGameLevel(gameLevel))
                {
                    gameLevel = ShaderLibrary.IsGameLevel(gameDefault) ? gameDefault : "off";
                }

                // dlss and dlaa are NVIDIA's, and fsr2 takes NVOFA motion vectors through CUDA, so on an
                // Intel GPU none of the three runs, except fsr2 on a binary built to run without CUDA
                // (GPU_VENDORS.md 5.5): NvidiaOnly says which.
                gameLevel = Withhold(plan, onIntel, "game", gameLevel, NvidiaOnly("Game", gameLevel));

                // fsr2/dlss/dlaa are Vulkan (vf_dlss.c's own header, "through NGX's Vulkan path");
                // this session's CUDA-native branch has no Vulkan device at all, so a game level
                // asked for alongside dlpp-*/vsr-rtcuda cannot run either. Same forced-off,
                // reported-not-silent treatment as srLevel/refineLevel/chromaLevel just above.
                if (plan.UsesCudaNeural)
                {
                    gameLevel = "off";
                }

                // jitter / depth / reactive are per-session on exactly the same carrier as the
                // eleven axes above: lowercase query options off the streaming request. Each falls
                // back to the dashboard setting and then to the built-in default inside
                // GameFilter, which is also where an unrecognised value is rejected. They are read
                // unconditionally and are inert by construction: with game=off no filter node is
                // built, so nothing carries them.
                plan.GameFilter = ShaderLibrary.GameFilter(
                    gameLevel,
                    plan.Width,
                    plan.Height,
                    cfg,
                    Option(state, "jitter"),
                    Option(state, "depth"),
                    Option(state, "reactive"),
                    out string gameUsed,
                    out string gameJitter,
                    out string gameDepth,
                    out string gameReactive,
                    out bool gameDepthDowngraded);
                plan.GameLevel = gameUsed;
                plan.GameApplied = plan.GameFilter != null;
                if (plan.GameApplied && onIntel)
                {
                    plan.GameMotionNote = GameMotionZeroNote;
                }

                plan.GameJitter = gameJitter;
                plan.GameDepth = gameDepth;
                plan.GameReactive = gameReactive;
                plan.GameDepthDowngraded = gameDepthDowngraded;

                // ---- two binaries, and not every filter runs on both -----------------------
                // The Vulkan denoise levels compile their shader at run time, and the patched
                // binary's shaderc is older than the stock one's: nlmeans_vulkan needs
                // GL_EXT_expect_assume, which it rejects. So the level runs on the stock binary
                // and fails on the patched one. A session asking for BOTH a Vulkan denoise and a
                // filter only the patched binary carries therefore routes to the patched binary
                // and dies with "shaderc compile status 'error'", killing the whole transcode.
                //
                // Dropping the denoise and saying so is the honest outcome: the viewer loses one
                // pass instead of the stream.
                //
                // AND IT IS NOW CONDITIONAL ON THE BINARY, NOT ASSUMED. build-ffmpeg.sh builds
                // shaderc from source, so a current binary runs the filter and this guard must not
                // fire: a permanent block would keep punishing every session for a defect that was
                // fixed. The probe RUNS one frame through nlmeans_vulkan on the patched binary
                // rather than asking whether the filter is listed, because listing it is exactly
                // what the broken build did. An older binary still degrades to one lost pass.
                bool needsPatchedBinary = plan.NeuralApplied || plan.GameApplied
                    || (plan.DenoiseApplied && ShaderLibrary.IsPatchedOnlyFilter(plan.DenoiseFilter));
                if (plan.DenoiseApplied && plan.DenoiseWantsHwFrames && needsPatchedBinary
                    && !VulkanDenoiseRunsOnPatchedBinary())
                {
                    plan.DenoiseDroppedForPatchedBinary = plan.DenoiseLevel;
                    plan.DenoiseFilter = null;
                    plan.DenoiseApplied = false;
                    plan.DenoiseLevel = "off";
                    plan.DenoiseWantsHwFrames = false;
                }

                // fsr2 and dlss produce the OUTPUT size themselves. Running an SR network as
                // well would enlarge the already-enlarged picture and let libplacebo shrink it
                // back - two upscalers fighting, which is worse than either. dlaa is exempt: it
                // is 1:1, so it is a restoration pass ahead of the normal scale, not a rival.
                if (plan.GameApplied && ShaderLibrary.GameScalesOutput(plan.GameLevel))
                {
                    srLevel = "off";
                    refineLevel = "off";
                }

                bool wantDeblur = !string.Equals(deblurLevel, "off", StringComparison.OrdinalIgnoreCase);
                bool wantRefine = !string.Equals(refineLevel, "off", StringComparison.OrdinalIgnoreCase);
                bool wantChroma = !string.Equals(chromaLevel, "off", StringComparison.OrdinalIgnoreCase);
                if (!plan.UpscaleApplied && !wantDeblur && !wantRefine && !wantChroma
                    && !plan.DenoiseApplied && !plan.DeblockApplied && !plan.NeuralApplied && !plan.GameApplied)
                {
                    return clientSaidOff
                        ? Plan.No("off-by-client", "the viewer selected Off")
                        : Plan.No("not-requested", (upscaleReason ?? "nothing requested") + WithheldNote(plan));
                }

                // ---- deband and the scaling kernel, both session-overridable ---------------
                // Same lowercase-query-parameter carrier as everything else. The kernel is checked
                // against a whitelist before it is used: an unknown string here would go straight
                // into the ffmpeg command and fail the whole job, so an unrecognised value is
                // ignored rather than tried.
                string debandOption = Option(state, "deband");
                bool wantDeband = cfg.Deband;
                if (debandOption != null)
                {
                    // Whitelisted both ways, like every other axis: an unrecognised value falls back
                    // to the dashboard default rather than being read as "on".
                    if (string.Equals(debandOption, "off", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(debandOption, "0", StringComparison.Ordinal)
                        || string.Equals(debandOption, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        wantDeband = false;
                    }
                    else if (string.Equals(debandOption, "on", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(debandOption, "1", StringComparison.Ordinal)
                        || string.Equals(debandOption, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        wantDeband = true;
                    }
                }

                // Debanding and the scaling kernel both belong to the libplacebo pass - reached for
                // a UsesCudaNeural session too now, via the hwmap bridge BuildChain builds when any
                // of deband/kernel-scale/refine/chroma/deblur is active (see the branch's own
                // comment above). Nothing forced off here anymore for that reason.
                plan.Upscaler = ShaderLibrary.CanonicalUpscaler(Option(state, "kernel"))
                    ?? ShaderLibrary.CanonicalUpscaler(cfg.Upscaler)
                    ?? "ewa_lanczos";

                plan.ShaderPath = ShaderLibrary.Resolve(
                    cfg,
                    srLevel,
                    deblurLevel,
                    refineLevel,
                    chromaLevel,
                    out string srUsed,
                    out string deblurUsed,
                    out string refineUsed,
                    out string chromaUsed);
                plan.SrLevel = srUsed;
                plan.DeblurLevel = deblurUsed;
                plan.DeblurApplied = !string.Equals(deblurUsed, "off", StringComparison.OrdinalIgnoreCase);
                plan.RefineLevel = refineUsed;
                plan.RefineApplied = !string.Equals(refineUsed, "off", StringComparison.OrdinalIgnoreCase);
                plan.ChromaLevel = chromaUsed;
                plan.ChromaApplied = !string.Equals(chromaUsed, "off", StringComparison.OrdinalIgnoreCase);
                plan.SrOwnsSharpening = wantDeblur
                    && !plan.DeblurApplied
                    && string.Equals(srUsed, "nvscaler", StringComparison.OrdinalIgnoreCase);

                // Debanding rides along on the libplacebo instance that is being built anyway, so
                // it is only "applied" when there is a chain for it to ride on.
                plan.DebandApplied = wantDeband;

                if (!plan.UpscaleApplied && !plan.DeblurApplied && !plan.RefineApplied
                    && !plan.ChromaApplied && !plan.DenoiseApplied && !plan.DeblockApplied
                    && !plan.NeuralApplied && !plan.GameApplied)
                {
                    // Sharpening was asked for but its shader is missing: nothing left to do.
                    return Plan.No("not-requested", "requested shaders unavailable" + WithheldNote(plan));
                }

                DecideHardwareDecode(plan, cfg, vs, onIntel);

                plan.Act = true;
                plan.Status = "applied";
                return plan;
            }
            catch (Exception ex)
            {
                return Plan.No("ineligible", "error: " + ex.Message);
            }
        }

        /// <summary>
        /// The VA-API surface format a decode of this source pixel format downloads in, or null when
        /// there is no hardware decode for it. The Vulkan frames keep the decoder's own sw format; for
        /// 8-bit 4:2:0 that is nv12.
        /// </summary>
        internal static string HardwareSurfaceFormat(string pixelFormat) =>
            pixelFormat == "yuv420p" ? "nv12" : null;

        /// <summary>
        /// IntelHardwareDecode (GPU_VENDORS.md 5.6): decides once whether this session decodes on the
        /// Intel GPU and maps the VA-API surfaces to Vulkan. Does nothing while the setting is off or
        /// there is no detected Intel GPU, which leaves HardwareDecodeReason null so no command,
        /// probe or record changes on any other server. Software decode is the answer to every
        /// doubt, and the reason names the first condition that failed. Never waits on the
        /// start-up test: it reads the answer the background probe has so far.
        /// </summary>
        private static void DecideHardwareDecode(Plan plan, UpscaleSettings cfg, object videoStream, bool onIntel)
        {
            if (cfg == null || !cfg.IntelHardwareDecode || !onIntel)
            {
                return;
            }

            plan.HardwareDecode = false;
            try
            {
                // VA-API frames cannot enter a CPU filter, so a plan with one keeps software decode.
                var cpu = new List<string>();
                if (plan.UsesCudaNeural)
                {
                    cpu.Add("CUDA neural");
                }

                if (plan.DeblockApplied && !plan.DeblockWantsHwFrames)
                {
                    cpu.Add("deblock");
                }

                if (plan.DenoiseApplied && !plan.DenoiseWantsHwFrames)
                {
                    cpu.Add("denoise");
                }
                else if (plan.DenoiseApplied && !VulkanDenoiseRunsOnPatchedBinary())
                {
                    // The VA-API chain runs on the patched binary, whose shader compiler may not
                    // build the Vulkan denoise shader; the stock one can.
                    cpu.Add("denoise, whose shader the patched ffmpeg cannot compile");
                }

                if (plan.NeuralApplied)
                {
                    cpu.Add("neural");
                }

                if (plan.GameApplied)
                {
                    cpu.Add("game");
                }

                if (cpu.Count > 0)
                {
                    plan.HardwareDecodeReason = "software decode: the plan has a step that cannot follow VA-API decode (" + string.Join(", ", cpu) + ")";
                    return;
                }

                if (!HardwareDecodableSource(plan, videoStream, out string codec, out string surfaceFormat))
                {
                    plan.HardwareDecodeReason = HardwareSourceReason;
                    return;
                }

                string node = Devices().RenderNode;
                if (string.IsNullOrEmpty(node))
                {
                    plan.HardwareDecodeReason = "software decode: the Intel render node has no usable path";
                    return;
                }

                // Looked up now, not cached, so a binary removed after start-up is noticed.
                string patched = PatchedFfmpeg();
                if (string.IsNullOrWhiteSpace(patched))
                {
                    plan.HardwareDecodeReason = "software decode: the patched ffmpeg is not installed, and stock ffmpeg cannot map VA-API frames to Vulkan";
                    return;
                }

                string test = "software decode: the startup test-decode of " + codec + " on " + node;
                HwDecodeAnswer answer = HardwareDecodeStarts(node);
                if (answer == null)
                {
                    plan.HardwareDecodeReason = test + " has not finished";
                    return;
                }

                bool? works = answer.For(codec);
                if (works != true)
                {
                    plan.HardwareDecodeReason = works == false ? test + " failed" : test + " could not be run";
                    return;
                }

                plan.HardwareDecode = true;
                plan.HardwareDecodeFormat = surfaceFormat;
                plan.HardwareDecodeReason = "decoded on the Intel GPU (VA-API) and mapped to Vulkan";
            }
            catch (Exception ex)
            {
                plan.HardwareDecode = false;
                plan.HardwareDecodeFormat = null;
                plan.HardwareDecodeReason = "software decode: error: " + ex.Message;
            }
        }

        private static object StreamValue(object stream, string name)
        {
            try
            {
                return stream?.GetType().GetProperty(name)?.GetValue(stream);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal const string HardwareSourceReason =
            "software decode: the source is not upright 8-bit SDR progressive 4:2:0 h264 (Main or High) or hevc (Main)";

        /// <summary>
        /// Upright 8-bit SDR progressive 4:2:0 h264 Main or High, or hevc Main. Profile and chroma
        /// format are gated because a decoder that cannot take 4:2:2, 4:4:4 or RExt hands back
        /// software frames, hwmap into Vulkan then fails, and the stream dies instead of degrading.
        /// Anything Jellyfin would tone-map, deinterlace or rotate adds a node that has to see CPU
        /// frames, and a property that cannot be read counts as "no". The codec is the normalised
        /// name (h264 or hevc), and the surface format the chain downloads in, when this returns true.
        /// </summary>
        private static bool HardwareDecodableSource(Plan plan, object stream, out string codec, out string surfaceFormat)
        {
            codec = null;
            surfaceFormat = null;
            if (plan.SourceSideways)
            {
                return false;
            }

            string name = (StreamValue(stream, "Codec") as string ?? string.Empty).Trim().ToLowerInvariant();
            if (name != "h264" && name != "hevc")
            {
                return false;
            }

            string profile = (StreamValue(stream, "Profile") as string ?? string.Empty).Trim().ToLowerInvariant();
            bool profileOk = name == "h264" ? profile == "main" || profile == "high" : profile == "main";
            if (!profileOk)
            {
                return false;
            }

            string pixelFormat = (StreamValue(stream, "PixelFormat") as string ?? string.Empty).Trim().ToLowerInvariant();
            string format = HardwareSurfaceFormat(pixelFormat);
            if (format == null)
            {
                return false;
            }

            if (!(StreamValue(stream, "BitDepth") is int depth) || depth != 8)
            {
                return false;
            }

            if (!(StreamValue(stream, "IsInterlaced") is bool interlaced) || interlaced)
            {
                return false;
            }

            string transfer = (StreamValue(stream, "ColorTransfer") as string ?? string.Empty).Trim().ToLowerInvariant();
            if (transfer == "smpte2084" || transfer == "arib-std-b67")
            {
                return false;
            }

            string range = (StreamValue(stream, "VideoRangeType")?.ToString() ?? string.Empty).Trim();
            if (range.Length != 0 && !string.Equals(range, "SDR", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(range, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            codec = name;
            surfaceFormat = format;
            return true;
        }

        /// <summary>Replaces the startup test-decode (true, false, null for inconclusive). The tests set it; nothing in production does.</summary>
        internal static Func<string, bool?> IntelHwDecodeProbeSource { get; set; }

        /// <summary>Replaces the lookup of the patched ffmpeg (null for not installed). The tests set it; nothing in production does.</summary>
        internal static Func<string> PatchedFfmpegSource { get; set; }

        /// <summary>The patched ffmpeg, or null when it is not installed. The one definition of its path is ShaderLibrary.PatchedFfmpegPath.</summary>
        private static string PatchedFfmpeg() => PatchedFfmpegSource != null ? PatchedFfmpegSource() : ShaderLibrary.PatchedFfmpegPath();

        /// <summary>
        /// What the start-up test-decode found per codec: true passed, false failed, null could not
        /// be run (no binary, no encoder for the clip, timeout). A codec the gate admits is only
        /// decoded on the GPU when its own entry is true.
        /// </summary>
        internal sealed class HwDecodeAnswer
        {
            public HwDecodeAnswer(bool? h264, bool? hevc)
            {
                H264 = h264;
                Hevc = hevc;
            }

            public bool? H264 { get; }

            public bool? Hevc { get; }

            /// <summary>Final only when every codec passed; anything else is asked again after the retry delay.</summary>
            public bool Conclusive => H264 == true && Hevc == true;

            public bool? For(string codec) => codec == "h264" ? H264 : codec == "hevc" ? Hevc : (bool?)false;
        }

        /// <summary>How long a failed or unrunnable test stands before the next one is scheduled; the same delay as GpuDetector's.</summary>
        internal static readonly TimeSpan HardwareDecodeRetryAfter = TimeSpan.FromSeconds(60);

        /// <summary>Replaces how a node's probe is built (a fake clock and a synchronous or queued scheduler). The tests set it; nothing in production does.</summary>
        internal static Func<string, CachedProbe<HwDecodeAnswer>> HardwareDecodeProbeFactory { get; set; }

        private static readonly Dictionary<string, CachedProbe<HwDecodeAnswer>> _hwDecode = new Dictionary<string, CachedProbe<HwDecodeAnswer>>();
        private static readonly object _hwDecodeLock = new object();

        /// <summary>Forgets every node's probe, so the next read builds a fresh one. For the tests.</summary>
        internal static void ResetHardwareDecodeProbes()
        {
            lock (_hwDecodeLock)
            {
                _hwDecode.Clear();
            }
        }

        private static CachedProbe<HwDecodeAnswer> HardwareDecodeProbe(string node)
        {
            lock (_hwDecodeLock)
            {
                if (!_hwDecode.TryGetValue(node, out CachedProbe<HwDecodeAnswer> probe))
                {
                    probe = (HardwareDecodeProbeFactory ?? NewHardwareDecodeProbe)(node);
                    _hwDecode[node] = probe;
                }

                return probe;
            }
        }

        private static CachedProbe<HwDecodeAnswer> NewHardwareDecodeProbe(string node)
        {
            HwDecodeAnswer last = null;
            return new CachedProbe<HwDecodeAnswer>(
                () =>
                {
                    HwDecodeAnswer fresh = RunHardwareDecodeTests(node);

                    // A codec this run could not test keeps what an earlier run found.
                    last = new HwDecodeAnswer(fresh.H264 ?? last?.H264, fresh.Hevc ?? last?.Hevc);
                    return last;
                },
                answer => answer.Conclusive,
                HardwareDecodeRetryAfter);
        }

        /// <summary>
        /// What the start-up test has found on this render node, or null while there is no answer
        /// yet. Never runs ffmpeg on the calling thread: the first read starts the probe on the
        /// thread pool and a failed answer is retried in the background (CachedProbe), so a
        /// session's plan never waits on it and one bad run is not remembered for the process.
        /// </summary>
        private static HwDecodeAnswer HardwareDecodeStarts(string node)
        {
            if (IntelHwDecodeProbeSource != null)
            {
                bool? all = IntelHwDecodeProbeSource(node);
                return new HwDecodeAnswer(all, all);
            }

            return HardwareDecodeProbe(node).Peek();
        }

        /// <summary>
        /// Starts the start-up test on a thread-pool thread when the setting is on and an Intel
        /// render node is found, so the first session finds an answer instead of starting the test.
        /// Never blocks and never throws; a server without the setting does nothing.
        /// </summary>
        internal static void PrimeHardwareDecode()
        {
            try
            {
                if (Settings?.IntelHardwareDecode != true)
                {
                    return;
                }

                DeviceInit devices = Devices();
                if (devices.Intel && !string.IsNullOrEmpty(devices.RenderNode))
                {
                    HardwareDecodeProbe(devices.RenderNode).Start();
                }
            }
            catch (Exception)
            {
                // the first session starts the probe instead
            }
        }

        /// <summary>
        /// The VA-API device, the Vulkan device derived from it, and the decode that leaves VA-API
        /// surfaces, with a leading space. The command a session runs (HwaccelArgs) and the start-up
        /// test are both built from this, so the test cannot drift from production. The decoder is
        /// bound by name (-hwaccel_device va) rather than by whichever device was created last.
        /// </summary>
        internal static string HardwareDecodeDeviceArgs(string node, string qsvArgs) =>
            " -init_hw_device vaapi=va:" + node + " -init_hw_device vulkan=vk@va" + qsvArgs
            + " -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi -filter_hw_device vk";

        private const int HardwareDecodeClipWidth = 256;

        private const int HardwareDecodeClipHeight = 144;

        /// <summary>
        /// The chain the start-up test decodes through: the production hardware-decode chain for a
        /// plain 2x upscale, so the hwmap into Vulkan, libplacebo on the imported surfaces and the
        /// download to the surface format are all exercised.
        /// </summary>
        internal static string HardwareDecodeTestChain() =>
            BuildChain(new Plan
            {
                Width = HardwareDecodeClipWidth * 2,
                Height = HardwareDecodeClipHeight * 2,
                UpscaleApplied = true,
                HardwareDecode = true,
                HardwareDecodeFormat = HardwareSurfaceFormat("yuv420p"),
            });

        /// <summary>The stock ffmpeg command that writes a tiny 8-bit 4:2:0 clip of this codec (h264 Main or hevc Main), the source the gate admits.</summary>
        internal static string HardwareDecodeClipArgs(string codec, string path) =>
            "-hide_banner -loglevel error -f lavfi -i testsrc=size=" + HardwareDecodeClipWidth + "x" + HardwareDecodeClipHeight + ":rate=5 "
            + "-frames:v 5 -pix_fmt yuv420p "
            + (codec == "hevc" ? "-c:v libx265 -profile:v main -x265-params log-level=error" : "-c:v libx264 -profile:v main")
            + " -y \"" + path + "\"";

        /// <summary>The patched ffmpeg command that decodes that clip the way a session does, through the chain.</summary>
        internal static string HardwareDecodeTestArgs(string node, string clip, string chain) =>
            "-hide_banner -loglevel error" + HardwareDecodeDeviceArgs(node, string.Empty)
            + " -i \"" + clip + "\" -vf \"" + chain + "\" -frames:v 3 -f null -";

        /// <summary>
        /// Runs the test for each codec on the patched binary (0008 and 0009 exist only there; stock
        /// ends this chain in "Conversion failed!"). Slow, so only a probe thread calls it. A clip
        /// that cannot be made, or no patched binary, is "could not be run" for that codec.
        /// </summary>
        private static HwDecodeAnswer RunHardwareDecodeTests(string node)
        {
            string patched = PatchedFfmpeg();
            string stock = StockFfmpeg();
            if (string.IsNullOrWhiteSpace(patched) || stock == null)
            {
                return new HwDecodeAnswer(null, null);
            }

            string dir = Path.Combine(Path.GetTempPath(), "jfplus-hwdecode-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string chain = HardwareDecodeTestChain();
                return new HwDecodeAnswer(
                    RunHardwareDecodeTest(patched, stock, node, dir, "h264", chain),
                    RunHardwareDecodeTest(patched, stock, node, dir, "hevc", chain));
            }
            catch (Exception)
            {
                return new HwDecodeAnswer(null, null);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception)
                {
                    // a temp directory left behind is cleaned by the OS
                }
            }
        }

        private static bool? RunHardwareDecodeTest(string patched, string stock, string node, string dir, string codec, string chain)
        {
            try
            {
                string clip = Path.Combine(dir, codec + ".mp4");
                if (RunExeProbe(stock, HardwareDecodeClipArgs(codec, clip), 20000) != true)
                {
                    return null;
                }

                return RunExeProbe(patched, HardwareDecodeTestArgs(node, clip, chain), 20000);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The size of the picture as it is displayed. A phone video is often stored sideways with a
        /// rotation of 90 or 270 degrees in its metadata, and Jellyfin turns it upright in the
        /// filter chain (transpose_cuda, transpose, or ffmpeg's own autorotate), so everything after
        /// that point sees the swapped dimensions. Reading the stored size made a 848x480 source
        /// stored sideways get a landscape 3816x2160 target for a portrait picture.
        /// </summary>
        private static (int Width, int Height) UprightSize(int width, int height, int? rotation) =>
            IsQuarterTurn(rotation) ? (height, width) : (width, height);

        /// <summary>True when the stream is stored sideways: a rotation of 90 or 270 degrees.</summary>
        private static bool IsQuarterTurn(int? rotation)
        {
            int degrees = ((rotation ?? 0) % 360 + 360) % 360;
            return degrees == 90 || degrees == 270;
        }

        /// <summary>
        /// Would an upscale actually apply to a source of this size, under the current settings?
        ///
        /// This is the same arithmetic <see cref="Decide"/> uses for the upscale target, factored
        /// out so the direct-play override asks exactly the question the engine will later answer.
        /// If the two ever disagreed, the override would force expensive transcodes for material
        /// the engine then declined to enhance, which is the worst of both worlds.
        /// </summary>
        public static bool WouldEnhanceSource(int? width, int? height, int? rotation = null)
        {
            try
            {
                UpscaleSettings cfg = Settings;
                if (cfg == null || !cfg.Enabled)
                {
                    return false;
                }

                if (width == null || height == null || width <= 0 || height <= 0)
                {
                    return false;
                }

                int sh = UprightSize(width.Value, height.Value, rotation).Height;
                if (sh > cfg.MaxSourceHeight)
                {
                    return false;
                }

                int target = Math.Min(cfg.TargetHeight, cfg.MaxTargetHeight);
                return target > sh * cfg.MinScaleFactor;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Is there capacity for another enhanced transcode right now. Counts live ffmpeg processes
        /// already running a libplacebo chain.
        /// </summary>
        public static bool HasCapacity()
        {
            try
            {
                int max = Settings?.MaxConcurrent ?? 0;
                if (max <= 0)
                {
                    return false;
                }

                // More than one patch asks this while a single ffmpeg command is being built, and
                // the walk below opens and reads /proc/<pid>/cmdline for EVERY process on the box.
                // The answer cannot meaningfully change inside one build, so it is computed once
                // and reused for a window just long enough to cover one. This is a memo, not a
                // change to the concurrency model: the count and the cap are unchanged, and a stale
                // answer can at worst admit or refuse one job at the boundary.
                lock (_capacityLock)
                {
                    if (DateTime.UtcNow - _capacityAt < CapacityWindow)
                    {
                        return _capacityLive < max;
                    }
                }

                int live = 0;
                foreach (string dir in Directory.EnumerateDirectories("/proc"))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Length == 0 || !char.IsDigit(name[0]))
                    {
                        continue;
                    }

                    try
                    {
                        string cmdline = File.ReadAllText(Path.Combine(dir, "cmdline"));
                        if (cmdline.IndexOf("libplacebo", StringComparison.Ordinal) >= 0
                            && cmdline.IndexOf("ffmpeg", StringComparison.Ordinal) >= 0)
                        {
                            live++;
                        }
                    }
                    catch (Exception)
                    {
                        // process vanished or unreadable; ignore
                    }
                }

                lock (_capacityLock)
                {
                    _capacityLive = live;
                    _capacityAt = DateTime.UtcNow;
                }

                return live < max;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly object _capacityLock = new object();
        private static readonly TimeSpan CapacityWindow = TimeSpan.FromSeconds(2);
        private static int _capacityLive;
        private static DateTime _capacityAt = DateTime.MinValue;

        /* --------------------------------------------------------------- live A/B swap (KEYED BY THE OLD PlaySessionId) */

        /// <summary>
        /// One live-apply swap in flight: the old ffmpeg job is still running, a new one has been
        /// admitted, and the old job's teardown (KillTranscodingJobs, called by stock Jellyfin when
        /// the client's re-negotiation reports the old session stopped) is being held until the new
        /// job's first segment is ready - or until this entry times out.
        /// </summary>
        private sealed class SwapState
        {
            public string DeviceId;
            public DateTime RegisteredUtc;
            public bool KillRequested;
            public TranscodeManager Manager;
            public string PendingKillDeviceId;
            public Func<string, bool> PendingDelete;
        }

        private static readonly ConcurrentDictionary<string, SwapState> _pendingSwaps =
            new ConcurrentDictionary<string, SwapState>(StringComparer.OrdinalIgnoreCase);

        private static int _activeSwapCount;

        /// <summary>
        /// How long a swap may stay pending before its deferred kill is forced through anyway. Long
        /// enough for a real transcode start (probe + first segment), short enough that a client
        /// that never re-negotiates does not hold a GPU slot indefinitely.
        /// </summary>
        private static readonly TimeSpan SwapTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Admits a live-apply swap for the OLD session id, if HasCapacity() has room for the extra
        /// process AND the separate swap cap (MaxConcurrentSwaps) is not exhausted. Total: any
        /// surprise here refuses the swap rather than risking a stuck teardown.
        /// </summary>
        public static string TryAdmitSwap(string oldSessionId, string deviceId)
        {
            try
            {
                if (string.IsNullOrEmpty(oldSessionId))
                {
                    return null;
                }

                SweepExpiredSwaps();

                int cap = Settings?.MaxConcurrentSwaps ?? 0;
                if (cap <= 0 || _activeSwapCount >= cap || !HasCapacity())
                {
                    return "swap-capacity";
                }

                var swap = new SwapState { DeviceId = deviceId, RegisteredUtc = DateTime.UtcNow };
                if (_pendingSwaps.TryAdd(oldSessionId, swap))
                {
                    Interlocked.Increment(ref _activeSwapCount);
                }

                return "swapping";
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Called from the KillTranscodingJobs Harmony prefix. Returns true (and the caller skips
        /// the real kill) only while a swap for this exact old session id is still admitted and not
        /// expired - the kill arguments are remembered so the real teardown can be replayed once the
        /// new job is ready, or by the timeout sweep if it never arrives.
        /// </summary>
        public static bool TryDeferKill(TranscodeManager manager, string oldSessionId, string deviceId, Func<string, bool> deleteFiles)
        {
            try
            {
                if (string.IsNullOrEmpty(oldSessionId))
                {
                    return false;
                }

                SweepExpiredSwaps();
                if (!_pendingSwaps.TryGetValue(oldSessionId, out var swap))
                {
                    return false;
                }

                swap.Manager = manager;
                swap.PendingKillDeviceId = deviceId;
                swap.PendingDelete = deleteFiles;
                swap.KillRequested = true;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Called from the StartFfMpeg Harmony postfix once the new ffmpeg process has produced its
        /// first segment - StartFfMpeg's own wait loop already blocks on exactly that before
        /// returning, so no separate readiness watch was needed. Finds a pending swap for the SAME
        /// DEVICE (old and new PlaySessionIds differ; DeviceId does not) and, if the old job's
        /// teardown was already requested and deferred, replays it for real now - this is the cut.
        /// </summary>
        public static void OnNewJobReady(TranscodingJob newJob)
        {
            try
            {
                string deviceId = newJob?.DeviceId;
                if (string.IsNullOrEmpty(deviceId))
                {
                    return;
                }

                string foundKey = null;
                SwapState found = null;
                foreach (var kv in _pendingSwaps)
                {
                    if (string.Equals(kv.Value.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        foundKey = kv.Key;
                        found = kv.Value;
                        break;
                    }
                }

                if (found == null)
                {
                    return;
                }

                CompleteSwap(foundKey, found, "swapped", newJob.PlaySessionId);
            }
            catch (Exception)
            {
                // never break a real cutover for a bookkeeping failure
            }
        }

        /// <summary>Sweeps swaps whose new job never arrived, forcing the deferred kill through so the old job is never orphaned or double-counted forever.</summary>
        private static void SweepExpiredSwaps()
        {
            try
            {
                foreach (var kv in _pendingSwaps)
                {
                    if (DateTime.UtcNow - kv.Value.RegisteredUtc > SwapTimeout)
                    {
                        CompleteSwap(kv.Key, kv.Value, "swap-timeout");
                    }
                }
            }
            catch (Exception)
            {
                // best effort; a swap left pending here is still bounded by MaxConcurrentSwaps
            }
        }

        private static void CompleteSwap(string oldSessionId, SwapState swap, string finalStatus, string newSessionId = null)
        {
            if (_pendingSwaps.TryRemove(oldSessionId, out _))
            {
                Interlocked.Decrement(ref _activeSwapCount);
            }

            if (swap.KillRequested && swap.Manager != null)
            {
                try
                {
                    _ = swap.Manager.KillTranscodingJobs(swap.PendingKillDeviceId, oldSessionId, swap.PendingDelete ?? (p => false));
                }
                catch (Exception)
                {
                    // the old job may now leak until its own ping timeout; better than crashing
                    // the path that would otherwise have torn it down
                }
            }

            if (_bySession.TryGetValue(oldSessionId, out var oldRecord))
            {
                oldRecord.SwapStatus = finalStatus;
            }

            if (!string.IsNullOrEmpty(newSessionId) && _bySession.TryGetValue(newSessionId, out var newRecord))
            {
                newRecord.SwapStatus = finalStatus;
            }
        }

        /// <summary>Public read of a session's own option, for callers outside this class (BuildVerdict's swapfrom marker).</summary>
        public static string OptionValue(EncodingJobInfo state, string name) => RawOption(state, name);

        /* ----------------------------------------------------------------------- GPU vendor */

        /// <summary>
        /// The GPU capability record with the Intel encode family resolved against this ffmpeg.
        /// The detector alone cannot say whether ffmpeg has a QSV encoder; this can, so a server
        /// without one reports software rather than a family the encoder choice will not use.
        /// </summary>
        public static GpuCapability Capability()
        {
            GpuCapability cap = GpuDetector.Current(Settings?.GpuVendor, Settings?.NeuralModelDirectory);
            if (cap.Vendor == GpuVendors.Intel && cap.Detected && !IntelHasQsvEncoder())
            {
                cap.EncoderFamily = "software";
                cap.Reason += "; ffmpeg lists no QSV encoder, so the encode is software (a VA-API encoder cannot take this chain's system-memory frames)";
            }

            return cap;
        }

        /// <summary>
        /// True when this server's GPU is an Intel one that was actually seen. Every Intel
        /// decision in the engine is gated on this and on nothing else, so a server with no
        /// vendor, an NVIDIA GPU, or an Intel setting with no Intel GPU behind it takes the code
        /// path that existed before the vendor setting did, byte for byte. Never throws.
        /// </summary>
        internal static bool IsIntelPath()
        {
            try
            {
                GpuCapability cap = GpuDetector.Current(Settings?.GpuVendor, Settings?.NeuralModelDirectory);
                return cap.Vendor == GpuVendors.Intel && cap.Detected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Whether the chain hands the encoder a frame straight from the GPU: the GpuResidentEncode
        /// setting, and only where the hand-off exists. It is a Vulkan-to-CUDA map (ffmpeg/0006),
        /// so it is NVENC-only; on Intel it is always off, whatever the setting says.
        /// </summary>
        private static bool GpuResidentEncodeOn() => Settings?.GpuResidentEncode == true && !IsIntelPath();

        /* ------------------------------------------------------------- the ONNX neural path */

        private const string OpenVinoMissingNote =
            "the OpenVINO provider (ort/lib/libonnxruntime_providers_openvino.so) is not installed beside the patched ffmpeg";

        /// <summary>
        /// What the ort filter is told to run on. The device option on the node (Arg), the name the
        /// record uses (Name) and, when the CPU is what an Intel server gets, why (Note).
        ///
        /// Only a detected Intel GPU changes anything: OpenVINO when the provider file is there, the
        /// CPU with the reason when it is not. Every other server gets a null Arg, so its node has no
        /// device option and the filter's default (CUDA) applies, as it always has. Never throws.
        /// </summary>
        internal static (string Arg, string Name, string Note) NeuralDeviceNow()
        {
            try
            {
                GpuCapability cap = GpuDetector.Current(Settings?.GpuVendor, Settings?.NeuralModelDirectory);
                if (cap.Vendor == GpuVendors.Intel && cap.Detected)
                {
                    return cap.OpenVinoAvailable
                        ? ("openvino", "openvino", null)
                        : ("cpu", "cpu", OpenVinoMissingNote + ", so ONNX Runtime's CPU provider runs");
                }
            }
            catch (Exception)
            {
                // fall through to the default
            }

            return (null, "cuda", null);
        }

        /// <summary>
        /// NeuralMaxSourceHeight. Returns the neural level the session should carry, after the source
        /// height has had its say, and records in plan.NeuralRouting whenever it changed or
        /// declined to change something. Only the ONNX levels are routed; dlpp-* and vsr-rtcuda are
        /// not. Called only when the setting is above 0 and neural is allowed.
        ///
        /// At or below the height a level that was already chosen stands. When nobody named one and
        /// the operator has set this value, the cheapest installed weight is chosen, but only for a
        /// session that is being upscaled anyway and not told Off, with no game upscaler in force, and
        /// never onto the CPU of an Intel server: a network on the CPU is far from real time, and
        /// choosing it for a viewer would be a dead stream. A level asked for by name still runs there.
        /// Above the height an ONNX level that was asked for is not run.
        /// </summary>
        private static string RouteNeuralBySourceHeight(
            Plan plan, UpscaleSettings cfg, EncodingJobInfo state, int sourceHeight, string neuralLevel, bool clientSaidOff)
        {
            int max = cfg.NeuralMaxSourceHeight;
            if (sourceHeight > max)
            {
                if (ShaderLibrary.IsOnnxNeuralLevel(neuralLevel))
                {
                    plan.NeuralRouting = string.Format(
                        CultureInfo.InvariantCulture,
                        "neural {0} NOT run: the source is {1}p, taller than NeuralMaxSourceHeight {2}, so the shader path ran",
                        neuralLevel.Trim().ToLowerInvariant(),
                        sourceHeight,
                        max);
                    return "off";
                }

                return neuralLevel;
            }

            bool nobodyAsked = plan.NeuralRequested == "off" && Option(state, "neural") == null;
            if (!nobodyAsked || clientSaidOff || !plan.UpscaleApplied)
            {
                return neuralLevel;
            }

            string game = cfg.GameAllowed ? (Option(state, "game") ?? cfg.GameLevel) : "off";
            if (ShaderLibrary.IsGameLevel(game)
                && !string.Equals(game.Trim(), "off", StringComparison.OrdinalIgnoreCase)
                && !IsIntelPath())
            {
                return neuralLevel;
            }

            string prefix = string.Format(
                CultureInfo.InvariantCulture,
                "the source is {0}p, at or below NeuralMaxSourceHeight {1}: ",
                sourceHeight,
                max);
            string auto = ShaderLibrary.AutoNeuralLevel(cfg);
            if (auto == null)
            {
                plan.NeuralRouting = prefix + "no neural level was chosen, none has its weights installed";
                return neuralLevel;
            }

            var device = NeuralDeviceNow();
            if (device.Name == "cpu")
            {
                plan.NeuralRouting = prefix + "no neural level was chosen, " + OpenVinoMissingNote
                    + " and a network on the CPU is far from real time (a level asked for by name still runs there)";
                return neuralLevel;
            }

            plan.NeuralRouting = prefix + "neural " + auto + " was chosen (the cheapest installed weight)";
            return auto;
        }

        /* ------------------------------------------------------------- NVIDIA-only levels */

        /// <summary>The reason a withheld level gives, as the probe and the status route serve it.</summary>
        internal const string NeedsNvidia = "needs NVIDIA";

        /// <summary>Whether a level of this axis (Denoise, Neural or Game) needs an NVIDIA GPU. A property of the level alone.</summary>
        internal static bool NvidiaOnly(string axis, string level)
        {
            switch (axis)
            {
                case "Denoise":
                    return ShaderLibrary.IsNvidiaOnlyDenoise(level);
                case "Neural":
                    return ShaderLibrary.IsNvidiaOnlyNeural(level);
                case "Game":
                    return ShaderLibrary.IsNvidiaOnlyGame(level)
                        && !(ShaderLibrary.IsCudaFreeGame(level) && NoCudaGameNow());
                default:
                    return false;
            }
        }

        /// <summary>What a game level's record says about its motion vectors on an Intel server.</summary>
        internal const string GameMotionZeroNote =
            "motion vectors are zero: this ffmpeg's fsr2 runs without CUDA, so there is no optical flow"
            + " and the reactive mask is empty (jitter is still measured)";

        /// <summary>
        /// Whether the patched ffmpeg's fsr2 was built to run without CUDA, on a detected Intel GPU.
        /// False everywhere else, so no other server's list or command changes. Never throws.
        /// </summary>
        internal static bool NoCudaGameNow()
        {
            try
            {
                GpuCapability cap = GpuDetector.Current(Settings?.GpuVendor, Settings?.NeuralModelDirectory);
                return cap.Vendor == GpuVendors.Intel && cap.Detected && cap.NoCudaGameAvailable;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The levels of one axis this server's GPU can run: the list unchanged on every server but a
        /// detected Intel one, and without the NVIDIA-only levels there. The probe is built from it,
        /// so the client never offers a level the engine will not run.
        /// </summary>
        internal static List<string> OfferedOnThisGpu(string axis, List<string> levels)
        {
            if (levels == null || !IsIntelPath())
            {
                return levels;
            }

            return levels.Where(l => !NvidiaOnly(axis, l)).ToList();
        }

        /// <summary>
        /// The levels the server would otherwise offer and withholds because this GPU is Intel,
        /// axis to level to reason. Empty on every other server, so the probe and the status route
        /// carry no new key there.
        /// </summary>
        internal static Dictionary<string, Dictionary<string, string>> WithheldLevels(UpscaleSettings cfg)
        {
            var map = new Dictionary<string, Dictionary<string, string>>();
            if (!IsIntelPath())
            {
                return map;
            }

            AddWithheld(map, "Denoise", ShaderLibrary.AvailableDenoiseLevels());
            AddWithheld(map, "Neural", cfg?.NeuralAllowed == false ? null : ShaderLibrary.AvailableNeuralLevels(cfg));
            AddWithheld(map, "Game", cfg?.GameAllowed == false ? null : ShaderLibrary.AvailableGameLevels(cfg));
            return map;
        }

        private static void AddWithheld(Dictionary<string, Dictionary<string, string>> map, string axis, List<string> levels)
        {
            if (levels == null)
            {
                return;
            }

            foreach (string level in levels.Where(l => NvidiaOnly(axis, l)))
            {
                if (!map.TryGetValue(axis, out var byLevel))
                {
                    map[axis] = byLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                byLevel[level] = NeedsNvidia;
            }
        }

        /// <summary>
        /// "off" for a level that cannot run on this GPU, with the pair recorded on the plan so the
        /// session record and its summary say so; the level as it was otherwise.
        /// </summary>
        private static string Withhold(Plan plan, bool onIntel, string axis, string level, bool nvidiaOnly)
        {
            if (!onIntel || !nvidiaOnly)
            {
                return level;
            }

            string note = axis + " " + level.Trim().ToLowerInvariant();
            plan.GpuWithheld = string.IsNullOrEmpty(plan.GpuWithheld) ? note : plan.GpuWithheld + ", " + note;
            return "off";
        }

        /// <summary>The sentence that says what an Intel GPU withheld, for a session that is left with nothing to run. Empty when nothing was.</summary>
        private static string WithheldNote(Plan plan) =>
            string.IsNullOrEmpty(plan.GpuWithheld)
                ? string.Empty
                : "; " + plan.GpuWithheld + " not run: needs an NVIDIA GPU and this server's is Intel";

        /* ------------------------------------------------------------------ device selection */

        private static readonly System.Text.RegularExpressions.Regex RenderNodePath =
            new System.Text.RegularExpressions.Regex("^/dev/dri/renderD([0-9]{1,4})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// The device initialisation a session's command carries, and the words the record uses for
        /// it. The default is the one every server had before the vendor setting: the first Vulkan
        /// device and no QSV device, so only a detected Intel GPU ever differs (GPU_VENDORS.md 5.3).
        /// </summary>
        internal sealed class DeviceInit
        {
            public bool Intel { get; set; }

            /// <summary>The -init_hw_device argument that makes the Vulkan device "vk", with its leading space.</summary>
            public string VulkanArgs { get; set; } = " -init_hw_device vulkan=vk:0";

            public string VulkanText { get; set; } = "vk:0";

            /// <summary>A QSV device argument, with its leading space, or empty.</summary>
            public string QsvArgs { get; set; } = string.Empty;

            /// <summary>Null off Intel.</summary>
            public string QsvText { get; set; }

            /// <summary>
            /// The Intel render node whenever its path is usable, else null (hardware decode needs
            /// it). Set even when the Vulkan-from-DRM test failed: hardware decode derives its own
            /// Vulkan device from VA-API and does not depend on that test.
            /// </summary>
            public string RenderNode { get; set; }
        }

        /// <summary>
        /// Resolves the device arguments for this server. On a detected Intel GPU with a usable render
        /// node the Vulkan device is derived from the node (drm=dr:NODE, vulkan=vk@dr) after a one-time
        /// test that it starts, and falls back to vk:0 if the test fails; the QSV device is added only
        /// where the encoder's own search cannot find the node (above renderD135). Everything else is
        /// the default above. RenderNode is set whenever the path is usable, also when that test
        /// failed and the software-decode Vulkan device is vk:0. Never throws.
        /// </summary>
        internal static DeviceInit Devices()
        {
            var d = new DeviceInit();
            try
            {
                if (!IsIntelPath())
                {
                    return d;
                }

                d.Intel = true;
                d.QsvText = "implicit";
                GpuCapability cap = GpuDetector.Current(Settings?.GpuVendor, Settings?.NeuralModelDirectory);
                string node = cap.RenderNode;
                var match = RenderNodePath.Match(node ?? string.Empty);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                {
                    d.VulkanText = "vk:0 (the Intel render node has no usable path)";
                    return d;
                }

                d.RenderNode = node;
                bool? starts = DrmVulkanStarts(node);
                if (starts == false)
                {
                    d.VulkanText = "vk:0 (a test start from " + node + " failed, so the first Vulkan device)";
                }
                else
                {
                    d.VulkanArgs = " -init_hw_device drm=dr:" + node + " -init_hw_device vulkan=vk@dr";
                    d.VulkanText = "render node " + node + (starts == true ? string.Empty : " (not tested: ffmpeg could not be run)");
                }

                if (!IntelHasQsvEncoder())
                {
                    d.QsvText = "none (ffmpeg lists no QSV encoder)";
                }
                else if (number >= 128 && number <= 135)
                {
                    // vaapi_device_create walks renderD128 to renderD135 and takes the first Intel one,
                    // which is the detector's node (GPU_VENDORS.md 5.3).
                    d.QsvText = "implicit (the first Intel render node, " + node + ")";
                }
                else
                {
                    d.QsvArgs = " -init_hw_device qsv=qs:hw_any,child_device=" + node;
                    d.QsvText = node;
                }
            }
            catch (Exception)
            {
                // a failure here is the default arguments, which is today's command
            }

            return d;
        }

        /// <summary>Replaces the test that a Vulkan device can be started from a render node (true, false, null for inconclusive). The tests set it; nothing in production does.</summary>
        internal static Func<string, bool?> DrmVulkanProbeSource { get; set; }

        private static readonly Dictionary<string, bool?> _drmVulkan = new Dictionary<string, bool?>();
        private static readonly object _drmVulkanLock = new object();

        /// <summary>
        /// Whether ffmpeg can start a Vulkan device derived from this render node, tested once per
        /// node and remembered whatever the answer (an inconclusive one is remembered too, so a
        /// server where ffmpeg cannot be run does not spawn a process per session). False is a
        /// definite failure, null is "could not tell".
        /// </summary>
        private static bool? DrmVulkanStarts(string node)
        {
            if (DrmVulkanProbeSource != null)
            {
                return DrmVulkanProbeSource(node);
            }

            lock (_drmVulkanLock)
            {
                if (_drmVulkan.TryGetValue(node, out bool? known))
                {
                    return known;
                }

                bool? result = RunFfmpegProbe(DrmVulkanProbeArgs(node), 20000);
                _drmVulkan[node] = result;
                return result;
            }
        }

        /// <summary>The test command: the session's own device initialisation and one frame through hwupload and hwdownload.</summary>
        internal static string DrmVulkanProbeArgs(string node) =>
            "-hide_banner -loglevel error -init_hw_device drm=dr:" + node + " -init_hw_device vulkan=vk@dr "
            + "-filter_hw_device vk -f lavfi -i testsrc=size=64x64:rate=1 -frames:v 1 "
            + "-vf format=yuv420p,hwupload,hwdownload,format=yuv420p -f null -";

        /// <summary>
        /// Runs the first ffmpeg that exists with these arguments: true on exit 0, false on any other
        /// exit, null when none could be run or it timed out. Both pipes are drained before the wait,
        /// as in the other probes here.
        /// </summary>
        private static bool? RunFfmpegProbe(string args, int timeoutMs)
        {
            foreach (string exe in StockFfmpegCandidates())
            {
                try
                {
                    if (!File.Exists(exe))
                    {
                        continue;
                    }

                    return RunExeProbe(exe, args, timeoutMs);
                }
                catch (Exception)
                {
                    // try the next candidate
                }
            }

            return null;
        }

        private static IEnumerable<string> StockFfmpegCandidates()
        {
            if (!string.IsNullOrWhiteSpace(_ffmpegPath))
            {
                yield return _ffmpegPath;
            }

            yield return "/usr/lib/jellyfin-ffmpeg/ffmpeg";
            yield return "/usr/bin/ffmpeg";
        }

        /// <summary>The first stock ffmpeg that exists, or null.</summary>
        private static string StockFfmpeg()
        {
            foreach (string exe in StockFfmpegCandidates())
            {
                if (File.Exists(exe))
                {
                    return exe;
                }
            }

            return null;
        }

        /// <summary>Runs one executable: true on exit 0, false on any other exit, null on a timeout. Throws when it cannot be started.</summary>
        private static bool? RunExeProbe(string exe, string args, int timeoutMs)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                var stdoutRead = proc.StandardOutput.ReadToEndAsync();
                var stderrRead = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(timeoutMs))
                {
                    try
                    {
                        proc.Kill();
                    }
                    catch (Exception)
                    {
                        // already gone
                    }

                    return null;
                }

                System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stdoutRead, stderrRead }, 5000);
                return proc.ExitCode == 0;
            }
        }

        private static readonly string[] IntelCodecs = { "h264", "hevc", "av1" };

        /// <summary>
        /// True when ffmpeg can be shown to lack a QSV encoder. An unreadable list is not "lacks":
        /// the safe response to not knowing is the usual QSV choice, as with every other probe here.
        /// </summary>
        private static bool IntelHasQsvEncoder()
        {
            HashSet<string> available = AvailableEncoders();
            return available == null || IntelCodecs.Any(c => available.Contains(c + "_qsv"));
        }

        /// <summary>
        /// The encoder an Intel server uses for a codec, with a note when it is not the plain QSV
        /// one. QSV, because the chain's frames are in system memory (GPU_VENDORS.md, 5.1). With
        /// no QSV encoder listed it is the software encoder for the codec, if ffmpeg has one.
        /// </summary>
        private static string IntelEncoderFor(string codec, out string note)
        {
            note = null;
            if (codec == null)
            {
                return null;
            }

            string qsv = codec + "_qsv";
            HashSet<string> available = AvailableEncoders();
            if (available == null || available.Contains(qsv))
            {
                return qsv;
            }

            string software = codec == "h264" ? "libx264" : codec == "hevc" ? "libx265" : "libsvtav1";
            if (available.Contains(software))
            {
                note = "ffmpeg has no " + qsv + ", so " + software + " (software)";
                return software;
            }

            note = "ffmpeg lists neither " + qsv + " nor " + software + "; " + qsv + " will fail to open";
            return qsv;
        }

        /// <summary>
        /// Whether an encoder can take the frames the Intel chain ends in (system memory): a QSV
        /// encoder or a software one. A VA-API encoder needs VA-API frames and an NVENC one is for
        /// the wrong GPU; either would be a dead stream, so neither is kept when Jellyfin chose it.
        /// </summary>
        internal static bool EncoderFitsIntelChain(string encoder)
        {
            if (string.IsNullOrWhiteSpace(encoder))
            {
                return false;
            }

            string e = encoder.Trim();
            return e.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)
                || e.StartsWith("lib", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the encoder name is an NVENC one (<c>hevc_nvenc</c>).</summary>
        internal static bool IsNvencEncoder(string encoder)
            => !string.IsNullOrWhiteSpace(encoder) && encoder.Trim().EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase);

        /* ------------------------------------------------------------------- encoder selection */

        /// <summary>The ffmpeg binary Jellyfin is using, captured from EncodingOptions when seen.</summary>
        private static string _ffmpegPath;

        private static HashSet<string> _encoders;

        /// <summary>
        /// True once the probe has been attempted, whatever it returned. A failure has to be
        /// remembered SEPARATELY from the result: without this, "I could not ask" re-spawned up to
        /// three ffmpeg processes on the transcode path for every session on a server where the
        /// probe cannot work at all.
        /// </summary>
        private static bool _encodersProbed;
        private static readonly object _encodersLock = new object();

        /// <summary>
        /// Replaces the ffmpeg encoder list (null result = unreadable). The tests set it so the
        /// encoder choice does not depend on the machine's ffmpeg; nothing in production does.
        /// </summary>
        internal static Func<HashSet<string>> EncoderListSource { get; set; }

        public static void NoteFfmpegPath(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && _ffmpegPath == null)
            {
                _ffmpegPath = path;
            }
        }

        /// <summary>
        /// The set of encoder names this ffmpeg build actually has, or null if it could not be
        /// asked. Null is deliberately different from empty: "I do not know" must not be read as
        /// "the encoder is missing", because the safe response to not knowing is to leave the
        /// client-negotiated codec alone.
        /// </summary>
        private static HashSet<string> AvailableEncoders()
        {
            if (EncoderListSource != null)
            {
                return EncoderListSource();
            }

            if (_encodersProbed)
            {
                return _encoders;
            }

            lock (_encodersLock)
            {
                if (_encodersProbed)
                {
                    return _encoders;
                }

                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(_ffmpegPath))
                {
                    candidates.Add(_ffmpegPath);
                }

                candidates.Add("/usr/lib/jellyfin-ffmpeg/ffmpeg");
                candidates.Add("/usr/bin/ffmpeg");

                foreach (string exe in candidates)
                {
                    try
                    {
                        if (!File.Exists(exe))
                        {
                            continue;
                        }

                        var psi = new System.Diagnostics.ProcessStartInfo(exe, "-hide_banner -loglevel quiet -encoders")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                        };

                        using (var proc = System.Diagnostics.Process.Start(psi))
                        {
                            // BOTH pipes are drained before the wait. Reading stdout to the end
                            // with stderr redirected and never read deadlocks the moment the child
                            // fills the stderr pipe - forever, with no timeout, on a transcode
                            // thread holding the probe lock.
                            var stdoutRead = proc.StandardOutput.ReadToEndAsync();
                            var stderrRead = proc.StandardError.ReadToEndAsync();
                            if (!proc.WaitForExit(15000))
                            {
                                // Leaving it running would strand an ffmpeg per attempt.
                                try
                                {
                                    proc.Kill();
                                    proc.WaitForExit(2000);
                                }
                                catch (Exception)
                                {
                                    // already gone
                                }

                                continue;
                            }

                            if (!System.Threading.Tasks.Task.WaitAll(new[] { stdoutRead, stderrRead }, 5000))
                            {
                                continue;
                            }

                            string stdout = stdoutRead.Result;

                            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (string line in stdout.Split('\n'))
                            {
                                // " V....D hevc_nvenc           NVIDIA NVENC hevc encoder"
                                string t = line.Trim();
                                int sp = t.IndexOf(' ');
                                if (sp <= 0 || t.Length < sp + 2)
                                {
                                    continue;
                                }

                                string rest = t.Substring(sp + 1).TrimStart();
                                int sp2 = rest.IndexOf(' ');
                                string name = sp2 > 0 ? rest.Substring(0, sp2) : rest;
                                if (name.Length > 0 && name.IndexOf('-') < 0)
                                {
                                    found.Add(name);
                                }
                            }

                            if (found.Count > 0)
                            {
                                _encoders = found;
                                _encodersProbed = true;
                                return _encoders;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // try the next candidate
                    }
                }

                // Every candidate failed. Remember that, so the next session does not spawn the
                // same three processes again; null still means "unknown", not "missing".
                _encodersProbed = true;
                return null;
            }
        }

        private static HashSet<string> _filters;
        private static bool _filtersProbed;
        private static readonly object _filtersLock = new object();

        /// <summary>
        /// Does this ffmpeg build carry a filter of this name?
        ///
        /// Asked once and remembered, the way the encoder probe is, and for the same reason: this
        /// runs on the transcode path and must never spawn a process per session. The probe failing
        /// is remembered separately from its result, so a server where it cannot work does not
        /// re-spawn three processes for every playback.
        ///
        /// UNKNOWN COUNTS AS MISSING HERE, which is the opposite of AvailableEncoders' rule and is
        /// deliberate. A refused encoder still plays the video; a filter name ffmpeg does not know
        /// fails the whole job. So when the binary cannot be asked, the caller is told the filter is
        /// not there and the level is not offered.
        /// </summary>
        public static bool HasFilter(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var filters = AvailableFilters();
            return filters != null && filters.Contains(name.Trim());
        }

        /// <summary>
        /// The command the Vulkan-denoise probe runs: one 64x64 frame through nlmeans_vulkan, on the
        /// same Vulkan device a session will get (vk:0 everywhere but a detected Intel GPU), so the
        /// probe tests the device that will be used.
        /// </summary>
        internal static string VulkanDenoiseProbeArgs() =>
            "-hide_banner -loglevel error" + Devices().VulkanArgs + " -filter_hw_device vk -f lavfi -i testsrc=size=64x64:rate=1 -frames:v 1 "
            + "-vf \"format=yuv420p,hwupload,nlmeans_vulkan,hwdownload,format=yuv420p\" -f null -";

        private static readonly object _vulkanDenoiseLock = new object();
        private static bool _vulkanDenoiseProbed;
        private static bool _vulkanDenoiseWorks;

        /// <summary>
        /// Can the PATCHED binary actually run a Vulkan denoise, as opposed to merely listing it?
        ///
        /// Presence and capability are different questions here, and the difference cost a dead
        /// stream: nlmeans_vulkan compiles its shader at run time, and a build whose shaderc
        /// predates GL_EXT_expect_assume lists the filter, accepts the chain, and then fails the
        /// whole transcode. So this RUNS one frame through it rather than asking whether it exists.
        ///
        /// Asked once per process and remembered, like the encoder and filter probes, because this
        /// sits on the transcode path. Unknown counts as NOT WORKING, the same rule HasFilter uses
        /// and for the same reason: the cost of being wrong in that direction is one dropped pass,
        /// and the cost of being wrong in the other is the viewer's stream.
        /// </summary>
        public static bool VulkanDenoiseRunsOnPatchedBinary()
        {
            if (_vulkanDenoiseProbed)
            {
                return _vulkanDenoiseWorks;
            }

            lock (_vulkanDenoiseLock)
            {
                if (_vulkanDenoiseProbed)
                {
                    return _vulkanDenoiseWorks;
                }

                _vulkanDenoiseProbed = true;
                _vulkanDenoiseWorks = false;

                try
                {
                    string exe = ShaderLibrary.PatchedFfmpegPath();
                    if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                    {
                        return _vulkanDenoiseWorks;
                    }

                    // One 64x64 frame of synthetic input: enough to force the shader to compile,
                    // small enough that a server under load does not notice it happening.
                    string args = VulkanDenoiseProbeArgs();

                    var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };

                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        var outRead = proc.StandardOutput.ReadToEndAsync();
                        var errRead = proc.StandardError.ReadToEndAsync();
                        if (!proc.WaitForExit(20000))
                        {
                            try
                            {
                                proc.Kill();
                            }
                            catch (Exception)
                            {
                                // Already gone.
                            }

                            return _vulkanDenoiseWorks;
                        }

                        System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { outRead, errRead }, 5000);
                        _vulkanDenoiseWorks = proc.ExitCode == 0;
                    }
                }
                catch (Exception)
                {
                    _vulkanDenoiseWorks = false;
                }

                return _vulkanDenoiseWorks;
            }
        }

        /// <summary>The filter names this ffmpeg build has, or null when it could not be asked.</summary>
        private static HashSet<string> AvailableFilters()
        {
            if (_filtersProbed)
            {
                return _filters;
            }

            lock (_filtersLock)
            {
                if (_filtersProbed)
                {
                    return _filters;
                }

                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(_ffmpegPath))
                {
                    candidates.Add(_ffmpegPath);
                }

                candidates.Add("/usr/lib/jellyfin-ffmpeg/ffmpeg");
                candidates.Add("/usr/bin/ffmpeg");

                foreach (string exe in candidates)
                {
                    try
                    {
                        if (!File.Exists(exe))
                        {
                            continue;
                        }

                        var psi = new System.Diagnostics.ProcessStartInfo(exe, "-hide_banner -loglevel quiet -filters")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                        };

                        using (var proc = System.Diagnostics.Process.Start(psi))
                        {
                            // Both pipes drained before the wait, for the deadlock the encoder
                            // probe documents: stderr left unread fills and stops the child while
                            // this thread waits forever on a transcode path holding the lock.
                            var stdoutRead = proc.StandardOutput.ReadToEndAsync();
                            var stderrRead = proc.StandardError.ReadToEndAsync();
                            if (!proc.WaitForExit(15000))
                            {
                                try
                                {
                                    proc.Kill();
                                    proc.WaitForExit(2000);
                                }
                                catch (Exception)
                                {
                                    // already gone
                                }

                                continue;
                            }

                            if (!System.Threading.Tasks.Task.WaitAll(new[] { stdoutRead, stderrRead }, 5000))
                            {
                                continue;
                            }

                            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (string line in stdoutRead.Result.Split('\n'))
                            {
                                // " T. fspp              V->V       Apply Fast Simple Post-processing filter."
                                // Flags first, then the name. The header lines carry no flags token
                                // followed by a name, so they fall out of the arrow check below.
                                string[] parts = line.Trim().Split(
                                    new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length < 3 || parts[2].IndexOf("->", StringComparison.Ordinal) < 0)
                                {
                                    continue;
                                }

                                if (parts[1].Length > 0)
                                {
                                    found.Add(parts[1]);
                                }
                            }

                            if (found.Count > 0)
                            {
                                _filters = found;
                                _filtersProbed = true;
                                return _filters;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // try the next candidate
                    }
                }

                _filtersProbed = true;
                return null;
            }
        }

        /// <summary>The codec family an encoder name produces, or null if it is not one we know.</summary>
        private static string CodecOf(string encoder)
        {
            if (string.IsNullOrWhiteSpace(encoder))
            {
                return null;
            }

            string e = encoder.ToLowerInvariant();
            if (e.Contains("hevc") || e.Contains("h265") || e.Contains("x265"))
            {
                return "hevc";
            }

            if (e.Contains("av1"))
            {
                return "av1";
            }

            if (e.Contains("vp9"))
            {
                return "vp9";
            }

            if (e.Contains("h264") || e.Contains("x264") || e.Contains("avc"))
            {
                return "h264";
            }

            return null;
        }

        /// <summary>
        /// The codecs the SESSION declared it can play, normalised.
        ///
        /// This is the honest capability list and it is not the same thing as the negotiated
        /// codec. EncodingJobInfo.SupportedVideoCodecs carries what the device profile said it
        /// accepts; BaseRequest.VideoCodec carries the single codec Jellyfin then picked. Checking
        /// the configured encoder against the negotiated codec alone can only ever succeed when
        /// the two already agree, which would leave the Encoder setting inert - the exact bug
        /// being fixed.
        /// </summary>
        private static HashSet<string> SupportedCodecs(EncodingJobInfo state)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string[] supported = state?.SupportedVideoCodecs;
                if (supported != null)
                {
                    foreach (string c in supported)
                    {
                        string n = Normalise(c);
                        if (n != null)
                        {
                            set.Add(n);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // fall through to whatever the request itself names
            }

            string negotiated = ClientCodec(state);
            if (negotiated != null)
            {
                set.Add(negotiated);
            }

            return set;
        }

        private static string Normalise(string codec)
        {
            if (string.IsNullOrWhiteSpace(codec))
            {
                return null;
            }

            string c = codec.Trim().ToLowerInvariant();
            if (c.Contains("hevc") || c.Contains("h265"))
            {
                return "hevc";
            }

            if (c.Contains("av1"))
            {
                return "av1";
            }

            if (c.Contains("vp9"))
            {
                return "vp9";
            }

            if (c.Contains("h264") || c.Contains("avc"))
            {
                return "h264";
            }

            return null;
        }

        /// <summary>The codec the client negotiated, normalised, or null when it said nothing.</summary>
        private static string ClientCodec(EncodingJobInfo state)
        {
            string requested = state?.BaseRequest?.VideoCodec ?? string.Empty;
            if (requested.IndexOf("h264", StringComparison.OrdinalIgnoreCase) >= 0
                || requested.IndexOf("avc", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "h264";
            }

            if (requested.IndexOf("hevc", StringComparison.OrdinalIgnoreCase) >= 0
                || requested.IndexOf("h265", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "hevc";
            }

            if (requested.IndexOf("av1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "av1";
            }

            return null;
        }

        /// <summary>
        /// The encoder to substitute for a stream copy.
        ///
        /// The configured Encoder setting is honoured, which it previously was not - the plugin
        /// followed the client's negotiated codec and quietly ignored the dashboard field. It is
        /// honoured with two guards, because handing a client a codec it cannot decode is not a
        /// worse picture, it is no picture:
        ///
        ///   1. ffmpeg must actually have that encoder. If the encoder list cannot be read at all,
        ///      that counts as "unknown", not as "missing", and the configured encoder is refused
        ///      rather than gambled on.
        ///   2. The session must have declared the encoder's codec playable. Jellyfin puts the
        ///      codecs the client will accept in the request's VideoCodec list; if the configured
        ///      encoder's codec is not among them, the client-negotiated codec wins.
        ///
        /// Blank or "auto" means "follow the client", which is the old behaviour on request.
        /// </summary>
        public static string EncoderFor(EncodingJobInfo state, out string reason)
        {
            string chosen = ChooseEncoder(state, out reason);
            string guarded = GuardAv1(state, chosen, out string guardReason);
            if (guardReason != null)
            {
                reason = guardReason + "; " + reason;
            }

            return guarded;
        }

        /// <summary>
        /// Swaps av1_nvenc for hevc_nvenc (h264_nvenc when the session cannot take HEVC) on a GPU
        /// without an AV1 encoder. ffmpeg lists av1_nvenc whenever it is compiled in, so the
        /// encoder list cannot catch this; the encoder only fails at open, killing the transcode.
        /// reason is null when nothing was swapped.
        /// </summary>
        public static string GuardAv1(EncodingJobInfo state, string encoder, out string reason)
        {
            reason = null;
            if (IsIntelPath())
            {
                return GuardAv1Intel(state, encoder, out reason);
            }

            if (!string.Equals(encoder, "av1_nvenc", StringComparison.OrdinalIgnoreCase) || Av1NvencUsable() != false)
            {
                return encoder;
            }

            var supported = SupportedCodecs(state);
            string swap = supported.Count == 0 || supported.Contains("hevc") ? "hevc_nvenc" : "h264_nvenc";
            reason = "GPU has no AV1 encoder (needs compute capability 8.9+), used " + swap + " instead of av1_nvenc";
            return swap;
        }

        /// <summary>
        /// The Intel counterpart: av1_qsv is listed by any build that compiles it, but only an Arc
        /// or newer GPU can open it, and it fails at open, which kills the transcode. So one frame
        /// is test-encoded (once, remembered) and a failure swaps to hevc_qsv, or h264_qsv when the
        /// session cannot take HEVC. An inconclusive probe leaves the choice alone.
        /// </summary>
        private static string GuardAv1Intel(EncodingJobInfo state, string encoder, out string reason)
        {
            reason = null;
            if (!string.Equals(encoder, "av1_qsv", StringComparison.OrdinalIgnoreCase) || Av1QsvUsable() != false)
            {
                return encoder;
            }

            var supported = SupportedCodecs(state);
            string swap = supported.Count == 0 || supported.Contains("hevc") ? "hevc_qsv" : "h264_qsv";
            reason = "a test encode with av1_qsv failed on this GPU, used " + swap + " instead of av1_qsv";
            return swap;
        }

        /// <summary>The test encode, on the same QSV device a session will use (the implicit one unless the command pins it).</summary>
        internal static string Av1QsvProbeArgs() =>
            "-hide_banner -loglevel error" + Devices().QsvArgs
            + " -f lavfi -i color=c=black:s=640x360:r=1 -frames:v 1 -vf format=nv12 -c:v av1_qsv -f null -";

        private static bool? _av1Qsv;
        private static readonly object _av1QsvLock = new object();

        /// <summary>Replaces the one-frame AV1 test encode (null result = inconclusive). For the tests.</summary>
        internal static Func<bool?> Av1QsvProbeSource { get; set; }

        /// <summary>
        /// Whether av1_qsv can open on this GPU, found by encoding one frame. Null when it could
        /// not be tried (no ffmpeg, a timeout): the caller leaves the choice alone rather than
        /// guess, and the answer is not remembered so a later session asks again.
        /// </summary>
        private static bool? Av1QsvUsable()
        {
            if (Av1QsvProbeSource != null)
            {
                return Av1QsvProbeSource();
            }

            lock (_av1QsvLock)
            {
                if (_av1Qsv.HasValue)
                {
                    return _av1Qsv;
                }

                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(_ffmpegPath))
                {
                    candidates.Add(_ffmpegPath);
                }

                candidates.Add("/usr/lib/jellyfin-ffmpeg/ffmpeg");
                candidates.Add("/usr/bin/ffmpeg");

                foreach (string exe in candidates)
                {
                    try
                    {
                        if (!File.Exists(exe))
                        {
                            continue;
                        }

                        var psi = new System.Diagnostics.ProcessStartInfo(exe, Av1QsvProbeArgs())
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                        };

                        using (var proc = System.Diagnostics.Process.Start(psi))
                        {
                            // Both pipes drained before the wait, as in the other probes here.
                            var stdoutRead = proc.StandardOutput.ReadToEndAsync();
                            var stderrRead = proc.StandardError.ReadToEndAsync();
                            if (!proc.WaitForExit(10000))
                            {
                                try
                                {
                                    proc.Kill();
                                }
                                catch (Exception)
                                {
                                    // already gone
                                }

                                return null;
                            }

                            _av1Qsv = proc.ExitCode == 0;
                            return _av1Qsv;
                        }
                    }
                    catch (Exception)
                    {
                        // try the next candidate
                    }
                }

                return null;
            }
        }

        private static bool? _av1Nvenc;
        private static readonly object _av1Lock = new object();

        /// <summary>
        /// Whether the GPU can encode AV1 (Ada, compute capability 8.9, or newer). Null when
        /// nvidia-smi cannot say; the caller leaves the choice alone rather than guessing.
        /// </summary>
        private static bool? Av1NvencUsable()
        {
            lock (_av1Lock)
            {
                if (_av1Nvenc.HasValue)
                {
                    return _av1Nvenc;
                }

                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("nvidia-smi", "--query-gpu=compute_cap --format=csv,noheader")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };

                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        var stdoutRead = proc.StandardOutput.ReadToEndAsync();
                        var stderrRead = proc.StandardError.ReadToEndAsync();
                        if (!proc.WaitForExit(5000))
                        {
                            try
                            {
                                proc.Kill();
                            }
                            catch (Exception)
                            {
                                // already gone
                            }

                            return null;
                        }

                        string first = stdoutRead.Result.Split('\n')[0].Trim();
                        if (proc.ExitCode == 0
                            && double.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cap))
                        {
                            _av1Nvenc = cap >= 8.9;
                        }
                    }
                }
                catch (Exception)
                {
                    // unknown stays null and is not cached
                }

                return _av1Nvenc;
            }
        }

        private static string ChooseEncoder(EncodingJobInfo state, out string reason)
        {
            // On an Intel server the encoder setting's NVENC default cannot run, so it is treated
            // as auto, and the reason says so. The NVIDIA path below is the code that existed
            // before the vendor setting did, and is reached unchanged by every other server.
            bool intel = IsIntelPath();
            string configured = Settings?.Encoder;
            string foreign = null;
            if (intel && IsNvencEncoder(configured))
            {
                foreign = "configured '" + configured.Trim() + "' is an NVIDIA encoder and this server's GPU is Intel; treated as auto: ";
                configured = "auto";
            }

            string chosen = ChooseEncoderFor(state, intel, configured, out reason);
            if (foreign != null)
            {
                reason = foreign + reason;
            }

            return chosen;
        }

        private static string ChooseEncoderFor(EncodingJobInfo state, bool intel, string configured, out string reason)
        {
            string clientCodec = ClientCodec(state);
            string pickNote = null;
            string clientEncoder = intel
                ? IntelEncoderFor(clientCodec, out pickNote)
                : clientCodec == "h264" ? "h264_nvenc"
                : clientCodec == "hevc" ? "hevc_nvenc"
                : clientCodec == "av1" ? "av1_nvenc"
                : null;

            if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            {
                if (intel)
                {
                    string picked = clientEncoder == null ? IntelEncoderFor("hevc", out pickNote) : clientEncoder;
                    reason = (clientEncoder != null
                        ? "Encoder set to auto; following the client-negotiated " + clientCodec + " (" + clientEncoder + ")"
                        : "Encoder set to auto and the client named no codec; using " + picked)
                        + (pickNote == null ? string.Empty : "; " + pickNote);
                    return picked;
                }

                reason = clientEncoder != null
                    ? "Encoder set to auto; following the client-negotiated " + clientCodec
                    : "Encoder set to auto and the client named no codec; using hevc_nvenc";
                return clientEncoder ?? "hevc_nvenc";
            }

            configured = configured.Trim();
            string fallback = clientEncoder ?? configured;

            var available = AvailableEncoders();
            if (available == null)
            {
                reason = "could not read the ffmpeg encoder list; refused configured '" + configured + "' and used " + fallback;
                return fallback;
            }

            if (!available.Contains(configured))
            {
                reason = "ffmpeg has no encoder '" + configured + "'; used " + fallback;
                return fallback;
            }

            if (intel && !EncoderFitsIntelChain(configured))
            {
                reason = "configured '" + configured + "' cannot take the system-memory frames this chain ends in (a VA-API encoder needs VA-API frames); used " + fallback;
                return fallback;
            }

            string configuredCodec = CodecOf(configured);
            if (configuredCodec == null)
            {
                reason = "cannot tell what codec '" + configured + "' produces, so cannot check the client supports it; used " + fallback;
                return fallback;
            }

            var supported = SupportedCodecs(state);
            if (supported.Count > 0 && !supported.Contains(configuredCodec))
            {
                reason = "session supports [" + string.Join(",", supported.OrderBy(x => x))
                    + "] and not " + configuredCodec + "; refused configured '" + configured
                    + "' and used " + fallback;
                return fallback;
            }

            reason = "configured encoder '" + configured + "' applied";
            return configured;
        }

        /// <summary>
        /// The hwaccel device arguments this plan's chain needs: Vulkan for the normal libplacebo
        /// chain, or CUDA (plus, always, a Vulkan device too - see below) for a session running
        /// one of the CUDA-native neural levels (dlpp-1..4, vsr-rtcuda) - see Plan.UsesCudaNeural
        /// and INTEGRATION_DESIGN.md section 2. The CUDA form also asks for the OUTPUT format on
        /// the decoder itself (-hwaccel_output_format cuda), unlike the Vulkan form, because this
        /// branch never leaves GPU memory: decode hands CUDA frames straight to the filter chain.
        ///
        /// A UsesCudaNeural session also always gets "-init_hw_device vulkan=vk:0 -filter_hw_device
        /// vk" alongside the CUDA hwaccel, even when this particular plan's chain never touches
        /// libplacebo. Confirmed by direct testing this is required, not optional, for BuildChain's
        /// hwmap bridge (see its own comment) to actually reach libplacebo: hwmap derives its own
        /// device ad hoc mid-graph and that alone is enough for hwmap<->hwmap round trips (the
        /// original three-bug fix's own repro has no top-level Vulkan device either and works),
        /// but libplacebo specifically needs a Vulkan device already attached to the filtergraph
        /// itself (AVFilterContext.hw_device_ctx, which only -filter_hw_device sets) to negotiate
        /// at all - without it FFmpeg auto-inserts a generic software "scale" filter ahead of
        /// libplacebo that then can't accept the incoming Vulkan hw frames and fails with
        /// "Impossible to convert" / ENOSYS, regardless of what hwmap_query_formats declares (see
        /// ffmpeg/0009-hwmap-query-formats.patch's own comment - that fix was necessary but not
        /// sufficient on its own). Retested with both devices present and no libplacebo in the
        /// chain (plain dlpp-alone): unaffected, still EXIT 0, no added cost - declaring a device
        /// the graph never references costs nothing at run time. See livetestbox.md.
        ///
        /// The Vulkan device is "vk:0" on every server but a detected Intel one, where it is derived
        /// from that GPU's render node and a QSV device is added only when the encoder cannot find
        /// the node itself (see Devices). An Intel server never gets the CUDA form: Decide has
        /// already dropped the levels that use it.
        /// </summary>
        public static string HwaccelArgs(Plan plan)
        {
            DeviceInit devices = Devices();
            if (plan != null && plan.HardwareDecode && devices.Intel && !string.IsNullOrEmpty(devices.RenderNode))
            {
                // IntelHardwareDecode: the Vulkan device is derived from the VA-API one the decoder
                // uses, so the surfaces can be mapped without a copy (GPU_VENDORS.md 5.6).
                return HardwareDecodeDeviceArgs(devices.RenderNode, devices.QsvArgs);
            }

            bool cuda = plan != null && plan.UsesCudaNeural && !devices.Intel;
            return devices.VulkanArgs + devices.QsvArgs + " -filter_hw_device vk"
                + (cuda ? " -hwaccel cuda -hwaccel_output_format cuda" : string.Empty);
        }

        /// <summary>
        /// The libplacebo chain for this plan.
        ///
        /// Ordering inside the shader file is decided by the hook points, not by this method -
        /// see ShaderLibrary.Compose. Both FSRCNNX and RCAS hook LUMA, so the sharpening runs on
        /// the network's enlarged luma plane, before the final scale, and therefore at 2x the
        /// source size rather than at output size. That is what makes RCAS nearly free.
        /// The one thing this method does own is that DENOISE runs first, before the upscale.
        /// </summary>
        public static string BuildChain(Plan plan)
        {
            UpscaleSettings cfg = Settings;

            if (plan.UsesCudaNeural)
            {
                // CUDA-NATIVE BRANCH. Decode already produced AV_PIX_FMT_CUDA frames (see
                // HwaccelArgs), so this never touches "format=yuv420p" and never hwuploads -
                // whatever a system-memory round trip would mean here, there is none: even the
                // bridge below stays on GPU memory throughout (verified this session, host-callback
                // counters showed +0 allocs and +0 host-to-device copies after init, across every
                // frame, for dlpp_rtcuda and vsr_rtcuda both with and without the bridge active).
                //
                // Whether it ALSO reaches libplacebo depends on whether this plan asked for any of
                // deband/kernel-scale/refine/chroma/deblur - those five live on one libplacebo
                // instance the CUDA-native branch could not reach until the chained hwmap bridge
                // was fixed (ffmpeg/0007-cuda-to-vulkan-hwmap.patch,
                // ffmpeg/0008-hwmap-chain-format.patch, livetestbox.md). When none of them is
                // active this returns exactly what it always did - CudaDenoiseNode, then the
                // neural filter, nothing else - because there is nothing for the bridge to buy: no
                // sense adding two hwmap hops and a libplacebo instance whose own node would be a
                // no-op. When at least one is active: decode cuda -> [CudaDenoiseNode] ->
                // hwmap=derive_device=vulkan -> the same libplacebo instance the Vulkan branch
                // below builds (AppendLibplaceboSegment) -> hwmap=derive_device=cuda -> [the neural
                // filter] -> NVENC takes the CUDA frame directly, same as GpuResidentEncode's own
                // hwmap path does for the plain Vulkan chain.
                bool wantsLibplacebo = plan.UpscaleApplied || plan.DeblurApplied
                    || plan.RefineApplied || plan.ChromaApplied || plan.DebandApplied;

                var cudaNodes = new List<string>();
                if (!string.IsNullOrEmpty(plan.CudaDenoiseNode))
                {
                    cudaNodes.Add(plan.CudaDenoiseNode);
                }

                if (wantsLibplacebo)
                {
                    cudaNodes.Add("hwmap=derive_device=vulkan");

                    // AppendLibplaceboSegment's own leading "," (every node append in this class
                    // carries its own, so a StringBuilder that already has content just grows) is
                    // wrong here: this segment becomes its own single element in cudaNodes, joined
                    // with "," below, so a leading comma inside the element would render as an
                    // empty node ("...,,libplacebo=..."). Strip it - the segment always starts with
                    // that one leading comma and nothing else does, so TrimStart(',') is exact, not
                    // a heuristic.
                    var bridgeSb = new StringBuilder();
                    AppendLibplaceboSegment(bridgeSb, plan, cfg, keepInputSize: plan.NeuralApplied);
                    cudaNodes.Add(bridgeSb.ToString().TrimStart(','));

                    cudaNodes.Add("hwmap=derive_device=cuda");
                }

                if (plan.NeuralApplied && !string.IsNullOrEmpty(plan.NeuralFilter))
                {
                    cudaNodes.Add(plan.NeuralFilter);
                }

                return string.Join(",", cudaNodes);
            }

            if (plan.HardwareDecode)
            {
                // Decode left VA-API surfaces, so there is no format=yuv420p and no hwupload: the
                // frames are mapped to Vulkan as they are. Decide only sets this for a plan with no
                // CPU-side node, so the only nodes that can sit here are the Vulkan-side ones.
                var hw = new StringBuilder();
                if (plan.RangeUntagged)
                {
                    hw.Append("setparams=range=tv,");
                }

                hw.Append("hwmap=derive_device=vulkan");
                if (plan.DeblockApplied && plan.DeblockWantsHwFrames)
                {
                    hw.Append(',').Append(plan.DeblockFilter);
                }

                if (plan.DenoiseApplied && plan.DenoiseWantsHwFrames)
                {
                    hw.Append(',').Append(plan.DenoiseFilter);
                }

                AppendLibplaceboSegment(hw, plan, cfg);
                hw.Append(",hwdownload,format=").Append(plan.HardwareDecodeFormat ?? throw new InvalidOperationException("hardware decode without a surface format"));
                return hw.ToString();
            }

            var sb = new StringBuilder();
            sb.Append("format=yuv420p");

            // Declared before anything reads the pixels. Only for a source that said nothing: a
            // file that declares full range is left alone, because forcing tv on genuinely full
            // material is the same error in the other direction.
            if (plan.RangeUntagged)
            {
                sb.Append(",setparams=range=tv");
            }

            var cpuNodes = new List<string>();

            // Deblocking runs FIRST of everything, at source resolution, before denoise and long
            // before the scale. The SR networks were trained on clean downsampled images, so a DCT
            // block edge or mosquito ringing reaching one is reconstructed as detail - the pass
            // amplifies the artefact. There is no later point that undoes that, which is why this
            // node is the head of the chain rather than an option somewhere in the middle. Every
            // level is a CPU filter today (this build has no deblock_vulkan), so it lands before
            // hwupload; the hardware branch below exists so the invariant holds if one ever does.
            if (plan.DeblockApplied && !plan.DeblockWantsHwFrames)
            {
                cpuNodes.Add(plan.DeblockFilter);
            }

            // Denoise runs BEFORE the upscale, always: denoising after enlargement would be asked
            // to remove noise the network has already turned into structure. Which SIDE of
            // hwupload it lands on is decided by the filter, not by the level name: atadenoise is
            // a CPU filter and goes before hwupload, while the nlmeans_vulkan levels take Vulkan
            // frames and go after it, still ahead of libplacebo. See the routing invariant at
            // ShaderLibrary._denoiseFilters.
            if (plan.DenoiseApplied && !plan.DenoiseWantsHwFrames)
            {
                cpuNodes.Add(plan.DenoiseFilter);
            }

            // Neural super-resolution runs AFTER denoise and BEFORE hwupload. After denoise for
            // the reason denoise runs first at all - a network handed noise turns it into
            // structure. Before hwupload because the "ort" filter takes planar float RGB on the
            // CPU, exactly as oidn does; libplacebo then scales whatever the network produced to
            // the size the session actually asked for, so the network's own factor never has to
            // match the target ratio.
            if (plan.NeuralApplied)
            {
                cpuNodes.Add(plan.NeuralFilter);
            }

            // The game temporal upscalers run last on the CPU side, after denoise and after
            // neural SR, immediately before hwupload. fsr2 and dlss hand libplacebo a picture
            // that is already at the target size, so the libplacebo scale becomes a no-op;
            // dlaa hands it the source size and libplacebo still does the scaling.
            if (plan.GameApplied)
            {
                cpuNodes.Add(plan.GameFilter);
            }

            AppendCpuNodes(sb, cpuNodes);

            sb.Append(",hwupload");

            if (plan.DeblockApplied && plan.DeblockWantsHwFrames)
            {
                sb.Append(',').Append(plan.DeblockFilter);
            }

            if (plan.DenoiseApplied && plan.DenoiseWantsHwFrames)
            {
                sb.Append(',').Append(plan.DenoiseFilter);
            }

            AppendLibplaceboSegment(sb, plan, cfg);

            // Default: bring the frame back to system memory so NVENC re-uploads it itself.
            // GpuResidentEncode instead derives a CUDA device from the existing Vulkan one and
            // hands NVENC the frame without leaving the GPU - see the config page for why this
            // is opt-in rather than the default.
            //
            // On an Intel GPU there is no such hand-off (it is a Vulkan-to-CUDA map), so the tail
            // is always the download, whatever GpuResidentEncode says. It stays yuv420p: hwdownload
            // yields the Vulkan frames' own format, which libplacebo leaves as the input's, and the
            // QSV encoder's nv12 is reached by the conversion ffmpeg inserts at the graph output.
            // The tail is therefore the string NVIDIA's non-resident path already uses.
            sb.Append(GpuResidentEncodeOn()
                ? ",hwmap=derive_device=cuda"
                : ",hwdownload,format=yuv420p");
            return sb.ToString();
        }

        /// <summary>
        /// Appends one libplacebo node - the scale/kernel, deband and the composed
        /// deblur/refine/chroma shader - to <paramref name="sb"/>. Shared between the plain
        /// Vulkan chain (which already has Vulkan frames from hwupload when this runs) and the
        /// CUDA-native branch's hwmap bridge (which derives Vulkan frames from CUDA immediately
        /// before this runs, then bridges back to CUDA immediately after) - same node, same
        /// options, same shader resolution either way, because chroma/deband/scale/refine/deblur
        /// don't care which side of a hwmap bridge produced the Vulkan frames they're reading.
        /// Every append here starts with its own leading "," (matching this class's convention
        /// elsewhere), so the caller's StringBuilder can already hold content (the Vulkan branch)
        /// or start empty (the CUDA bridge, which strips the one resulting leading comma before
        /// using this as a single filtergraph node - see BuildChain).
        /// </summary>
        private static void AppendLibplaceboSegment(
            StringBuilder sb, Plan plan, UpscaleSettings cfg, bool keepInputSize = false)
        {
            // keepInputSize: a neural session lets dlpp_rtcuda and vsr_rtcuda own the resize, so
            // libplacebo only runs its shaders at the size it is handed. Giving it the final target
            // as well upscaled the picture first, then dlpp doubled that, then vsr shrank it back.
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                ",libplacebo=w={0}:h={1}:upscaler={2}",
                keepInputSize ? "iw" : plan.Width.ToString(CultureInfo.InvariantCulture),
                keepInputSize ? "ih" : plan.Height.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(plan.Upscaler) ? "ewa_lanczos" : plan.Upscaler);

            // Debanding. grain=0 is not a detail: libplacebo defaults it to 6, which dithers
            // synthetic grain over the picture. That is wrong for this content, which is already
            // noisy - the point of debanding here is the flat dark gradients, not texture.
            //
            // Both numbers are clamped to libplacebo's documented 0-1000 range for these two
            // AVOptions rather than passed through: a value outside it is rejected when the filter
            // is opened, which fails the whole transcode instead of producing a worse picture.
            if (plan.DebandApplied)
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    ":deband=1:deband_threshold={0}:deband_grain={1}",
                    Math.Max(0, Math.Min(1000, cfg?.DebandThreshold ?? 3)),
                    Math.Max(0, Math.Min(1000, cfg?.DebandGrain ?? 0)));
            }

            if (!string.IsNullOrEmpty(plan.ShaderPath) && File.Exists(plan.ShaderPath))
            {
                sb.Append(":custom_shader_path=").Append(plan.ShaderPath);

                // Without a cache, every ffmpeg process compiles the composed GLSL again - and
                // FSRCNNX plus RCAS is a lot of it - so the first segment stalls, and so does the
                // restart after every seek. The prefix is per shader combination, because a cache
                // keyed on one combination is useless to another.
                string cachePrefix = ShaderLibrary.ShaderCachePrefix(cfg, plan.ShaderPath);
                if (!string.IsNullOrEmpty(cachePrefix))
                {
                    sb.Append(":shader_cache=").Append(cachePrefix);
                }
            }
        }

        /// <summary>
        /// Appends the CPU-side nodes, converting into planar float ONCE around a run of nodes
        /// that want it rather than once per node.
        ///
        /// oidn, optix, ort, fsr2 and dlss each carry their own format=gbrpf32le,...,format=yuv420p
        /// because each has to work in float. Emitted per node, two of them in a row (denoise=oidn
        /// plus a neural level) converted back to yuv420p and straight into float again BETWEEN the
        /// two passes, which re-subsamples chroma to 4:2:0 and requantises to 8 bit in the middle
        /// of the group. The wrappers are stripped and the conversions emitted around the run, so
        /// the group stays in float throughout. A single float node, or one with a non-float node
        /// such as atadenoise beside it, produces exactly the string it produced before.
        /// </summary>
        private static void AppendCpuNodes(StringBuilder sb, List<string> nodes)
        {
            const string floatIn = "format=gbrpf32le,";
            const string floatOut = ",format=yuv420p";

            bool inFloat = false;
            foreach (string node in nodes)
            {
                string body = node;
                bool wantsFloat = body.StartsWith(floatIn, StringComparison.Ordinal)
                    && body.EndsWith(floatOut, StringComparison.Ordinal);
                if (wantsFloat)
                {
                    body = body.Substring(floatIn.Length, body.Length - floatIn.Length - floatOut.Length);
                }

                if (wantsFloat && !inFloat)
                {
                    sb.Append(",format=gbrpf32le");
                    inFloat = true;
                }
                else if (!wantsFloat && inFloat)
                {
                    sb.Append(",format=yuv420p");
                    inFloat = false;
                }

                sb.Append(',').Append(body);
            }

            if (inFloat)
            {
                sb.Append(",format=yuv420p");
            }
        }

        /// <summary>
        /// Splits an ffmpeg filter chain on the commas that separate nodes, leaving commas that
        /// are escaped or inside parentheses (as in Jellyfin's scale=trunc(min(...)) expressions) alone.
        /// </summary>
        public static List<string> SplitFilters(string chain)
        {
            var nodes = new List<string>();
            if (string.IsNullOrEmpty(chain))
            {
                return nodes;
            }

            int depth = 0;
            var cur = new StringBuilder();
            char prev = '\0';
            foreach (char ch in chain)
            {
                if (ch == '(' && prev != '\\')
                {
                    depth++;
                }
                else if (ch == ')' && prev != '\\')
                {
                    depth--;
                }

                if (ch == ',' && depth == 0 && prev != '\\')
                {
                    nodes.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(ch);
                }

                prev = ch;
            }

            if (cur.Length > 0)
            {
                nodes.Add(cur.ToString());
            }

            return nodes.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        }

        /// <summary>
        /// The filter list Jellyfin's kept nodes and our chain make together. Software decode: kept
        /// nodes first, on the frames Jellyfin decoded. Hardware decode: our chain first, because the
        /// decoded frames are VA-API surfaces no kept node can read, so the kept nodes run after the
        /// download on the nv12 frame. They are CPU nodes either way: the gate excludes the sources
        /// that make Jellyfin add a hardware-side tone-map, deinterlace or rotate node. The hwaccel
        /// arguments come from a separate patch that has already run, so this cannot fall back to
        /// software decode when a kept node turns out not to suit nv12.
        /// </summary>
        internal static string JoinChain(Plan plan, string chain, List<string> kept)
        {
            if (kept == null || kept.Count == 0)
            {
                return chain;
            }

            return plan.HardwareDecode
                ? chain + "," + string.Join(",", kept)
                : string.Join(",", kept) + "," + chain;
        }

        private static readonly string[] _replacedNodes = { "scale", "hwupload", "hwdownload", "hwmap", "format", "libplacebo" };

        /// <summary>
        /// Keeps Jellyfin's own non-scaling filter nodes (colour tagging and friends) and drops the
        /// scaling / hardware-frame nodes that our chain replaces.
        /// </summary>
        public static List<string> KeepNonScalingNodes(string chain)
        {
            var kept = new List<string>();
            foreach (string node in SplitFilters(chain))
            {
                string trimmed = node.Trim();
                int eq = trimmed.IndexOf('=');
                string name = eq >= 0 ? trimmed.Substring(0, eq) : trimmed;
                if (!_replacedNodes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    kept.Add(trimmed);
                }
            }

            return kept;
        }
    }
}
