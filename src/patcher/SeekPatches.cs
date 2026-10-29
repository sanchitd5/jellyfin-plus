using System;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus.Patcher
{
    /// <summary>
    /// Faster seeks in HLS transcodes. A seek past what is transcoded makes stock Jellyfin stop the
    /// running ffmpeg and start a new one with an input -ss. Measured on CT114 (12.3), two costs sit in
    /// that path besides the encode itself:
    ///
    ///   1. TranscodingJob.Stop writes "q" and waits up to 5 s for ffmpeg to exit: 0.6-0.7 s with a
    ///      GPU filter chain draining, every seek. The output is discarded anyway (the seek deletes the
    ///      last segment; a stop deletes the partial files). Stop prefix: arm a kill 200 ms after the
    ///      "q", HLS jobs of the plugin's ffmpeg only. Stock Stop runs unchanged; the kill only makes its
    ///      WaitForExit return sooner.
    ///
    ///   2. The input modifier carries -analyzeduration 200M -probesize 1G (stock defaults), so the new
    ///      ffmpeg reads deep into the file before the first frame: 0.6-1 s per seek. When the job starts
    ///      mid-item on a local file in a container whose header describes its streams (mkv/mp4/webm/mov)
    ///      and Jellyfin already probed the streams, GetInputModifier's result gets a 3 s / 10 MB probe.
    ///      Never raised above what was configured. A first play from 0 keeps the stock values.
    ///
    /// A shorter first segment after a seek was considered and not done: the VOD playlist is a fixed
    /// grid of SegmentLength segments, so a shorter one would break segment numbering.
    /// Fail-soft: every hook catches and leaves stock behaviour.
    /// </summary>
    internal static class SeekPatches
    {
        internal const int QuickExitMs = 200;
        internal const long SeekAnalyzeDurationUs = 3_000_000;
        internal const long SeekProbeSizeBytes = 10_000_000;

        private static readonly string[] HeaderContainers = { "mkv", "matroska", "webm", "mp4", "m4v", "mov" };
        private static readonly Regex AnalyzeArg = new Regex(@"-analyzeduration\s+(\S+)", RegexOptions.CultureInvariant);
        private static readonly Regex ProbeArg = new Regex(@"-probesize\s+(\S+)", RegexOptions.CultureInvariant);

        private static ILogger _logger;

        public static bool StopAvailable { get; private set; }

        public static bool ProbeAvailable { get; private set; }

        public static void Apply(Harmony harmony, ILogger logger)
        {
            _logger = logger;
            var flags = BindingFlags.Static | BindingFlags.NonPublic;

            var stop = AccessTools.Method(typeof(TranscodingJob), "Stop", Type.EmptyTypes);
            if (stop != null)
            {
                harmony.Patch(stop, prefix: new HarmonyMethod(typeof(SeekPatches).GetMethod(nameof(StopPrefix), flags)));
                StopAvailable = true;
            }

            var modifier = AccessTools.Method(
                typeof(EncodingHelper),
                "GetInputModifier",
                new[] { typeof(EncodingJobInfo), typeof(MediaBrowser.Model.Configuration.EncodingOptions), typeof(string) });
            if (modifier != null)
            {
                harmony.Patch(modifier, postfix: new HarmonyMethod(typeof(SeekPatches).GetMethod(nameof(InputModifierPostfix), flags)));
                ProbeAvailable = true;
            }

            logger?.LogInformation(
                "JellyfinPlus: seek patches: quick stop {Stop}, small probe on mid-item starts {Probe}",
                StopAvailable ? "on" : "unavailable",
                ProbeAvailable ? "on" : "unavailable");
        }

        private static void StopPrefix(TranscodingJob __instance)
        {
            try
            {
                var process = __instance?.Process;
                if (__instance == null || process == null || __instance.HasExited
                    || __instance.Type != TranscodingJobType.Hls
                    || !IsPluginFfmpeg(process.StartInfo?.FileName))
                {
                    return;
                }

                var pid = process.Id;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(QuickExitMs).ConfigureAwait(false);
                    try
                    {
                        if (!process.HasExited && process.Id == pid)
                        {
                            process.Kill();
                            _logger?.LogDebug("JellyfinPlus: quick stop killed ffmpeg {Pid} {QuickExitMs} ms after q", pid, QuickExitMs);
                        }
                    }
                    catch (Exception)
                    {
                        // already gone, or disposed by stock: nothing to do
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "JellyfinPlus: quick stop skipped");
            }
        }

        internal static bool IsPluginFfmpeg(string fileName)
            => !string.IsNullOrEmpty(fileName)
               && (fileName.Contains("jellyfin-ffmpeg-upscale", StringComparison.Ordinal)
                   || string.Equals(fileName, ShaderLibrary.PatchedFfmpegPath(), StringComparison.Ordinal));

        private static void InputModifierPostfix(EncodingJobInfo state, ref string __result)
        {
            try
            {
                if (string.IsNullOrEmpty(__result) || state == null || !IsMidItemLocalStart(state))
                {
                    return;
                }

                __result = SmallProbe(__result);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "JellyfinPlus: small probe skipped");
            }
        }

        private static bool IsMidItemLocalStart(EncodingJobInfo state)
        {
            var source = state.MediaSource;
            return state.IsInputVideo
                   && (state.StartTimeTicks ?? 0) > 0
                   && source != null
                   && source.Protocol == MediaProtocol.File
                   && !source.IsInfiniteStream
                   && state.VideoStream != null
                   && IsHeaderContainer(source.Container);
        }

        internal static bool IsHeaderContainer(string container)
        {
            if (string.IsNullOrEmpty(container))
            {
                return false;
            }

            foreach (var part in container.Split(','))
            {
                if (Array.IndexOf(HeaderContainers, part.Trim().ToLowerInvariant()) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Lowers -analyzeduration/-probesize in an input modifier, never raising them.</summary>
        internal static string SmallProbe(string modifier)
        {
            modifier = AnalyzeArg.Replace(modifier, m => Lower("-analyzeduration ", m.Groups[1].Value, SeekAnalyzeDurationUs));
            return ProbeArg.Replace(modifier, m => Lower("-probesize ", m.Groups[1].Value, SeekProbeSizeBytes));
        }

        private static string Lower(string flag, string value, long target)
        {
            var current = ParseSize(value);
            return current.HasValue && current.Value > target
                ? flag + target.ToString(CultureInfo.InvariantCulture)
                : flag + value;
        }

        /// <summary>ffmpeg size syntax: plain number, or K/M/G suffix (powers of 1000), optional trailing "B".</summary>
        internal static long? ParseSize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            var v = value.Trim().TrimEnd('B', 'b');
            long mult = 1;
            if (v.Length > 0)
            {
                switch (char.ToUpperInvariant(v[^1]))
                {
                    case 'K': mult = 1_000; v = v[..^1]; break;
                    case 'M': mult = 1_000_000; v = v[..^1]; break;
                    case 'G': mult = 1_000_000_000; v = v[..^1]; break;
                }
            }

            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0
                ? (long)(d * mult)
                : null;
        }
    }
}
