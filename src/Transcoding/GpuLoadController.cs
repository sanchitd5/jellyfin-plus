using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Plus
{
    /// <summary>
    /// Cheap GPU/transcode load snapshot so background encoders can back off while people watch.
    /// NVIDIA is read from nvidia-smi (the NVENC session count, as ever). Intel is read from the
    /// kernel's per-client DRM fdinfo and sysfs, which needs no tool and no privilege but sees only
    /// this server's own processes (docs/architecture/GPU_VENDORS.md, 5.2).
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("JellyfinPlus")]
    public class GpuLoadController : ControllerBase
    {
        private static readonly object NvencLock = new object();
        private static readonly SemaphoreSlim ProbeGate = new SemaphoreSlim(1, 1);
        private static int? _nvencCached;
        private static DateTime _nvencAtUtc = DateTime.MinValue;

        private static readonly object IntelLock = new object();
        private static IntelLoad _intelCached;
        private static DateTime _intelAtUtc = DateTime.MinValue;
        private static IntelSample _intelPrevious;

        private readonly ISessionManager _sessions;

        /// <summary>Initializes a new instance of the <see cref="GpuLoadController"/> class.</summary>
        public GpuLoadController(ISessionManager sessions)
        {
            _sessions = sessions;
        }

        /// <summary>Gets active transcodes and NVENC encoder sessions. Any authenticated caller, API key included.</summary>
        [HttpGet("Gpu/Load")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<GpuLoad>> GetLoad()
        {
            int active = 0, video = 0;
            try
            {
                var transcoding = _sessions.Sessions.Where(s => s.TranscodingInfo != null).ToList();
                active = transcoding.Count;
                video = transcoding.Count(s => !s.TranscodingInfo.IsVideoDirect);
            }
            catch (Exception)
            {
                // counts stay 0
            }

            string vendor = null;
            string renderNode = null;
            try
            {
                JsonObject gpu = PatcherLoader.GpuRecord();
                bool detected = gpu?["Detected"]?.GetValue<bool>() == true;
                string v = gpu?["Vendor"]?.GetValue<string>();
                if (detected && (v == "nvidia" || v == "intel"))
                {
                    vendor = v;
                    renderNode = gpu["RenderNode"]?.GetValue<string>();
                }
            }
            catch (Exception)
            {
                // unknown stays null
            }

            if (vendor == "intel")
            {
                // No NVIDIA GPU to ask: nvencSessions is null, and nvidia-smi is not spawned.
                IntelLoad intel = IntelLoadNow(renderNode);
                return new GpuLoad
                {
                    ActiveTranscodes = active,
                    ActiveVideoTranscodes = video,
                    EnhancedSessions = null,
                    NvencSessions = null,
                    Vendor = "intel",
                    EncoderSessions = intel?.VideoClients,
                    GpuBusyPercent = intel?.BusyPercent,
                    GpuIdlePercent = intel?.IdlePercent,
                    LoadSource = intel == null ? null : "drm-fdinfo (this server's processes only)",
                };
            }

            int? nvenc = await NvencCountAsync().ConfigureAwait(false);
            return new GpuLoad
            {
                ActiveTranscodes = active,
                ActiveVideoTranscodes = video,
                EnhancedSessions = null, // patcher exposes no running-session count, only history
                NvencSessions = nvenc,

                // Additive. On NVIDIA the encoder sessions are the NVENC ones; busy and idle are
                // not read from nvidia-smi here, so they are null rather than guessed. A server
                // whose vendor is unknown but whose nvidia-smi answered is NVIDIA all the same.
                Vendor = vendor ?? (nvenc != null ? "nvidia" : "none"),
                EncoderSessions = nvenc,
                LoadSource = nvenc != null ? "nvidia-smi" : null,
            };
        }

        /* ---- Intel: DRM fdinfo and sysfs ------------------------------------------------- */

        private const int SampleGapMs = 300;
        private static readonly TimeSpan IntelCacheFor = TimeSpan.FromSeconds(5);

        private sealed class IntelSample
        {
            public DateTime AtUtc { get; set; }

            public Dictionary<string, DrmClient> Clients { get; set; }

            public Dictionary<string, long> Residency { get; set; }
        }

        private sealed class IntelLoad
        {
            public int VideoClients { get; set; }

            public double? BusyPercent { get; set; }

            public double? IdlePercent { get; set; }
        }

        // A figure older than this is not worth showing: it is reported as unavailable instead.
        private static readonly TimeSpan IntelStaleAfter = TimeSpan.FromSeconds(60);

        private static int _intelRefreshing;
        private static Task _intelRefresh = Task.CompletedTask;

        /// <summary>The refresh in flight, or the last one. The tests await it; nothing else reads it.</summary>
        internal static Task IntelRefreshTask => _intelRefresh;

        // The request thread never walks /proc and never sleeps between samples. It returns the last
        // figure (null when there is none, or when it is older than IntelStaleAfter) and, once the
        // 5 s cache has lapsed, starts ONE background refresh, so a poll costs one /proc walk per
        // window however often it is asked and never waits for it.
        private static IntelLoad IntelLoadNow(string renderNode)
        {
            IntelLoad value;
            DateTime at;
            lock (IntelLock)
            {
                value = _intelCached;
                at = _intelAtUtc;
            }

            TimeSpan age = DateTime.UtcNow - at;
            if (age >= IntelCacheFor)
            {
                StartIntelRefresh(renderNode);
            }

            return age < IntelStaleAfter ? value : null;
        }

        private static void StartIntelRefresh(string renderNode)
        {
            if (Interlocked.CompareExchange(ref _intelRefreshing, 1, 0) != 0)
            {
                return;
            }

            _intelRefresh = Task.Run(async () =>
            {
                try
                {
                    IntelLoad result = null;
                    try
                    {
                        result = await MeasureIntelAsync(renderNode).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // null: unavailable, never an error on a route that exists to be cheap
                    }

                    lock (IntelLock)
                    {
                        _intelCached = result;
                        _intelAtUtc = DateTime.UtcNow;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _intelRefreshing, 0);
                }
            });
        }

        private static IntelSample TakeIntelSample(string renderNode)
        {
            var clients = DrmFdinfo.Scan("/proc", renderNode);
            if (clients == null)
            {
                return null;
            }

            string name = string.IsNullOrEmpty(renderNode) ? null : Path.GetFileName(renderNode);
            return new IntelSample
            {
                AtUtc = DateTime.UtcNow,
                Clients = clients,
                Residency = GpuIdleResidency.Read("/sys/class/drm", name),
            };
        }

        // Two samples make a rate. The previous call's sample is reused when it is recent enough to
        // be a fair window; otherwise the first call waits SampleGapMs between two reads.
        private static async Task<IntelLoad> MeasureIntelAsync(string renderNode)
        {
            IntelSample now = TakeIntelSample(renderNode);
            if (now == null)
            {
                return null;
            }

            IntelSample before;
            lock (IntelLock)
            {
                before = _intelPrevious;
            }

            double age = before == null ? double.MaxValue : (now.AtUtc - before.AtUtc).TotalSeconds;
            if (age < 0.5 || age > 30)
            {
                before = now;
                await Task.Delay(SampleGapMs).ConfigureAwait(false);
                now = TakeIntelSample(renderNode);
                if (now == null)
                {
                    return null;
                }
            }

            lock (IntelLock)
            {
                _intelPrevious = now;
            }

            double seconds = (now.AtUtc - before.AtUtc).TotalSeconds;
            DrmLoadResult load = DrmFdinfo.Compute(before.Clients, now.Clients, seconds);
            return new IntelLoad
            {
                VideoClients = load.VideoClients,
                BusyPercent = load.BusyPercent,
                IdlePercent = GpuIdleResidency.IdlePercent(before.Residency, now.Residency, seconds),
            };
        }

        private static bool TryCached(out int? value)
        {
            lock (NvencLock)
            {
                value = _nvencCached;
                return DateTime.UtcNow - _nvencAtUtc < TimeSpan.FromSeconds(5);
            }
        }

        private static async Task<int?> NvencCountAsync()
        {
            if (TryCached(out var cached))
            {
                return cached;
            }

            // Single-flight: waiters re-check the cache once the running probe (2 s cap) finishes.
            await ProbeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (TryCached(out cached))
                {
                    return cached;
                }

                return await ProbeAsync().ConfigureAwait(false);
            }
            finally
            {
                ProbeGate.Release();
            }
        }

        private static async Task<int?> ProbeAsync()
        {
            int? result = null;
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=encoder.stats.sessionCount --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        string output = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                        await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                        if (proc.ExitCode == 0)
                        {
                            int sum = 0;
                            bool ok = true;
                            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                                {
                                    sum += n;
                                }
                                else
                                {
                                    ok = false;
                                    break;
                                }
                            }

                            if (ok)
                            {
                                result = sum;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        try
                        {
                            proc.Kill(true);
                        }
                        catch (Exception)
                        {
                            // already gone
                        }
                    }
                }
            }
            catch (Exception)
            {
                // no nvidia-smi etc: null
            }

            lock (NvencLock)
            {
                _nvencCached = result;
                _nvencAtUtc = DateTime.UtcNow;
            }

            return result;
        }

        /// <summary>Load snapshot.</summary>
        public class GpuLoad
        {
            /// <summary>Gets or sets sessions currently transcoding.</summary>
            [JsonPropertyName("activeTranscodes")]
            public int ActiveTranscodes { get; set; }

            /// <summary>Gets or sets transcodes whose video is being encoded.</summary>
            [JsonPropertyName("activeVideoTranscodes")]
            public int ActiveVideoTranscodes { get; set; }

            /// <summary>Gets or sets running enhancement sessions, null when unknown.</summary>
            [JsonPropertyName("enhancedSessions")]
            public int? EnhancedSessions { get; set; }

            /// <summary>Gets or sets NVENC sessions across GPUs, null when unavailable (always null on Intel).</summary>
            [JsonPropertyName("nvencSessions")]
            public int? NvencSessions { get; set; }

            /// <summary>Gets or sets the GPU vendor the figures below come from: nvidia, intel or none.</summary>
            [JsonPropertyName("vendor")]
            public string Vendor { get; set; }

            /// <summary>
            /// Gets or sets encoder sessions. NVIDIA: the NVENC session count. Intel: clients whose
            /// video-engine time moved in the window, which is encode on this plugin's chain
            /// (software decode) and would also count a client decoding in hardware. Null when
            /// unavailable.
            /// </summary>
            [JsonPropertyName("encoderSessions")]
            public int? EncoderSessions { get; set; }

            /// <summary>
            /// Gets or sets the busiest engine class's busy percent (0-100) from DRM fdinfo, over
            /// this server's own processes only. Intel only; null on NVIDIA and when unavailable.
            /// </summary>
            [JsonPropertyName("gpuBusyPercent")]
            public double? GpuBusyPercent { get; set; }

            /// <summary>
            /// Gets or sets the percent of the window the least-idle GT spent asleep (its idle
            /// RC6 residency), from sysfs, which covers every process on the GPU. Intel only; null
            /// when sysfs offers no counter. "Idle" is not the inverse of busy, so it is its own field.
            /// </summary>
            [JsonPropertyName("gpuIdlePercent")]
            public double? GpuIdlePercent { get; set; }

            /// <summary>Gets or sets where the vendor figures came from, null when none were read.</summary>
            [JsonPropertyName("loadSource")]
            public string LoadSource { get; set; }
        }
    }
}
