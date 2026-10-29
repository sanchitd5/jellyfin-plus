using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus.Patcher
{
    /// <summary>
    /// Gzip for HLS variant playlists. A VOD variant playlist lists every segment of the item, each
    /// line carrying the full request query (~1.1 KB): 2.9 MB for a 2 h 9 min recording at 3 s
    /// segments. Jellyfin 12.3 enables response compression with ASP.NET's default MIME list, which
    /// does not include application/vnd.apple.mpegurl, so it goes out uncompressed. Measured over a
    /// 264 ms / ~2 Mbps link: 10.5 s of a 15.4 s start-up was that one download.
    ///
    /// Postfix on DynamicHlsController.GetVariantPlaylistInternal (resolved by name, as Jellyfin.Api
    /// is not referenced): when its FileContentResult is over 16 KB and the request accepts gzip,
    /// the body is gzipped and Content-Encoding/Vary set. The compression middleware skips a response
    /// that already has Content-Encoding. Any failure returns the stock result unchanged.
    /// </summary>
    internal static class PlaylistGzipPatches
    {
        internal const int MinBytes = 16 * 1024;

        private static ILogger _logger;
        private static int _loggedOnce;

        public static bool Available { get; private set; }

        public static void Apply(Harmony harmony, ILogger logger)
        {
            _logger = logger;
            var type = AccessTools.TypeByName("Jellyfin.Api.Controllers.DynamicHlsController");
            var target = type == null ? null : AccessTools.Method(type, "GetVariantPlaylistInternal");
            if (target == null || target.ReturnType != typeof(Task<ActionResult>))
            {
                logger?.LogWarning("JellyfinPlus: playlist gzip unavailable (DynamicHlsController.GetVariantPlaylistInternal not found)");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PlaylistGzipPatches).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
            Available = true;
            logger?.LogInformation("JellyfinPlus: patched DynamicHlsController.GetVariantPlaylistInternal (variant playlists gzipped)");
        }

        private static void Postfix(ControllerBase __instance, ref Task<ActionResult> __result)
        {
            if (__instance == null || __result == null)
            {
                return;
            }

            __result = Wrap(__instance, __result);
        }

        private static async Task<ActionResult> Wrap(ControllerBase controller, Task<ActionResult> inner)
        {
            var result = await inner.ConfigureAwait(false);
            try
            {
                if (result is not FileContentResult file || file.FileContents == null || file.FileContents.Length < MinBytes)
                {
                    return result;
                }

                var http = controller.HttpContext;
                if (http == null || !AcceptsGzip(http.Request.Headers.AcceptEncoding.ToString())
                    || http.Response.HasStarted || http.Response.Headers.ContainsKey("Content-Encoding"))
                {
                    return result;
                }

                var gz = Gzip(file.FileContents);
                http.Response.Headers["Content-Encoding"] = "gzip";
                var vary = http.Response.Headers.Vary.ToString();
                http.Response.Headers.Vary = string.IsNullOrEmpty(vary) ? "Accept-Encoding" : vary + ", Accept-Encoding";
                if (System.Threading.Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                {
                    _logger?.LogInformation(
                        "JellyfinPlus: playlist gzip active ({Raw} -> {Gz} bytes on the first one)",
                        file.FileContents.Length,
                        gz.Length);
                }

                return new FileContentResult(gz, file.ContentType);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "JellyfinPlus: playlist gzip skipped");
                return result;
            }
        }

        /// <summary>True when an Accept-Encoding header allows gzip (q=0 refuses it).</summary>
        internal static bool AcceptsGzip(string acceptEncoding)
        {
            if (string.IsNullOrEmpty(acceptEncoding))
            {
                return false;
            }

            foreach (var part in acceptEncoding.Split(','))
            {
                var bits = part.Split(';');
                var name = bits[0].Trim();
                if (!name.Equals("gzip", StringComparison.OrdinalIgnoreCase) && name != "*")
                {
                    continue;
                }

                for (var i = 1; i < bits.Length; i++)
                {
                    var p = bits[i].Trim();
                    if (p.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(p.AsSpan(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q)
                        && q <= 0)
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        internal static byte[] Gzip(byte[] data)
        {
            using var ms = new MemoryStream(data.Length / 8 + 64);
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                gz.Write(data, 0, data.Length);
            }

            return ms.ToArray();
        }
    }
}
