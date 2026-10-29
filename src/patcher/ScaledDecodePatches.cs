using System;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.Plus.Patcher
{
    /// <summary>
    /// Decodes a large JPEG at reduced scale when Jellyfin is about to shrink it a lot anyway.
    ///
    /// THE COST. SkiaEncoder.EncodeImage (12.3) always decodes the whole source: GetBitmap -> Decode
    /// (SKCodec.GetPixels at full size, or SKBitmap.Decode) and only then ResizeImage. On the live
    /// library the sources are up to 3840x4602 JPEGs, so a cold 720 px poster still pays the full
    /// decode, which is the floor of a 0.4-2.3 s cold resize.
    ///
    /// THE PATCH. A JPEG decoder can emit 1/2, 1/4 or 1/8 scale straight from the DCT, which costs a
    /// fraction of the full decode. Three patches cooperate through one thread-static context (the
    /// whole encode runs synchronously on one thread):
    ///
    ///   1. SkiaEncoder.EncodeImage, prefix + finalizer. The prefix opens the context for a .jpg/.jpeg
    ///      input (ASCII path only, so stock's special-character temp-copy hack is never involved).
    ///      The finalizer closes it and VERIFIES: the output file must have exactly the dimensions
    ///      stock would have produced. If it does not (or the encode threw after a scaled decode),
    ///      the output is deleted, this feature is switched off for the rest of the process, and the
    ///      encode is re-run once through the untouched stock path.
    ///
    ///   2. SkiaEncoder.Decode, prefix. For the one decode of the input path, it reads the header,
    ///      works out the final size exactly as stock does (ImageHelper.GetNewImageSize on the full,
    ///      orientation-corrected size), and picks the largest of 1/8, 1/4, 1/2 whose decoded size is
    ///      still at least the final size in both dimensions (never an upscale). It then decodes at that scale into a
    ///      bitmap of the same kind stock builds. Any doubt (not a JPEG, animated, no gain, options
    ///      that would make stock return the original file, a failed decode) returns to stock.
    ///
    ///   3. ImageHelper.GetNewImageSize, prefix. EncodeImage derives the output size from the bitmap
    ///      it was handed. With a scaled bitmap that would shrink the output, so while the context
    ///      says "scaled" the call that receives the scaled size is given the full size instead. The
    ///      resize then runs from the scaled bitmap to the same target size stock would use. If the
    ///      JIT ever inlines GetNewImageSize into EncodeImage this prefix does not run, the output
    ///      is the wrong size, and the finalizer's check catches it.
    ///
    /// Orientation, crop, fill, blur, layers, indicators, sharpening and the output encoder are stock's
    /// and untouched. Output pixels differ slightly (the DCT scaling is a box filter ahead of stock's
    /// bilinear+mipmap downscale). The DCT scaling is an area average, a downscale in its own right,
    /// and stock's resize still runs from at least the target size. A 2x margin was tried first and
    /// left the common card (990 px from a 3840 source: 1920 &lt; 1980) on the full decode.
    /// Output dimensions are identical.
    ///
    /// Fail-soft: every prefix catches and returns to stock; each patch is installed independently
    /// and the context is only ever opened by patch 1, installed last, so a partial install is inert.
    /// </summary>
    internal static class ScaledDecodePatches
    {
        private const int Margin = 1;
        private static readonly int[] Denominators = { 8, 4, 2 };

        [ThreadStatic]
        private static Context _ctx;

        [ThreadStatic]
        private static bool _bypass;

        private static ILogger _logger;
        private static int _disabled;
        private static long _scaled;

        /// <summary>True when all three patches are installed.</summary>
        public static bool Available { get; private set; }

        /// <summary>Number of decodes done at reduced scale since start.</summary>
        public static long ScaledCount => Interlocked.Read(ref _scaled);

        private sealed class Context
        {
            public string Path;
            public bool AutoOrient;
            public ImageProcessingOptions Options;
            public bool Tried;
            public bool Scaled;
            public bool Hooked;
            public int ScaledW;
            public int ScaledH;
            public int FullW;
            public int FullH;
            public int OutW;
            public int OutH;
        }

        public static void Apply(Harmony harmony, ILogger logger)
        {
            _logger = logger;

            // Resolved BY NAME: no compile-time reference to Jellyfin.Drawing.Skia.
            var skia = AccessTools.TypeByName("Jellyfin.Drawing.Skia.SkiaEncoder");
            var decode = skia == null ? null : AccessTools.Method(
                skia, "Decode", new[] { typeof(string), typeof(bool), typeof(ImageOrientation?), typeof(SKEncodedOrigin).MakeByRefType() });
            var encode = skia == null ? null : FindEncode(skia);
            var newSize = AccessTools.Method(typeof(ImageHelper), nameof(ImageHelper.GetNewImageSize));

            if (decode == null || encode == null || newSize == null
                || decode.ReturnType != typeof(SKBitmap) || encode.ReturnType != typeof(string)
                || newSize.ReturnType != typeof(ImageDimensions))
            {
                logger?.LogWarning(
                    "JellyfinPlus: could not resolve SkiaEncoder.Decode / EncodeImage or ImageHelper.GetNewImageSize; image sources are decoded at full size (stock).");
                return;
            }

            const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
            harmony.Patch(decode, prefix: new HarmonyMethod(typeof(ScaledDecodePatches).GetMethod(nameof(DecodePrefix), Flags)));
            harmony.Patch(newSize, prefix: new HarmonyMethod(typeof(ScaledDecodePatches).GetMethod(nameof(NewSizePrefix), Flags)));
            harmony.Patch(
                encode,
                prefix: new HarmonyMethod(typeof(ScaledDecodePatches).GetMethod(nameof(EncodePrefix), Flags)),
                finalizer: new HarmonyMethod(typeof(ScaledDecodePatches).GetMethod(nameof(EncodeFinalizer), Flags)));
            Available = true;

            logger?.LogInformation("JellyfinPlus: patched SkiaEncoder (large JPEG sources are decoded at 1/2, 1/4 or 1/8 scale when the decoded size still covers the output)");
        }

        private static MethodInfo FindEncode(Type skia)
        {
            foreach (var m in skia.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "EncodeImage")
                {
                    continue;
                }

                var p = m.GetParameters();
                if (p.Length == 8 && p[0].Name == "inputPath" && p[2].Name == "outputPath" && p[3].Name == "autoOrient"
                    && p[6].Name == "options" && p[6].ParameterType == typeof(ImageProcessingOptions))
                {
                    return m;
                }
            }

            return null;
        }

        private static void EncodePrefix(string inputPath, bool autoOrient, ImageProcessingOptions options)
        {
            try
            {
                _ctx = null;
                if (_bypass || Volatile.Read(ref _disabled) != 0 || options == null || string.IsNullOrEmpty(inputPath))
                {
                    return;
                }

                var ext = Path.GetExtension(inputPath);
                if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                foreach (var ch in inputPath)
                {
                    if (ch > 127)
                    {
                        return; // stock copies such paths to a temp file first; leave them alone
                    }
                }

                _ctx = new Context { Path = inputPath, AutoOrient = autoOrient, Options = options };
            }
            catch (Exception ex)
            {
                _ctx = null;
                _logger?.LogDebug(ex, "JellyfinPlus: scaled decode could not open a context");
            }
        }

        private static bool Transposes(SKEncodedOrigin o) =>
            o is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        private static bool DecodePrefix(string path, bool forceCleanBitmap, out SKEncodedOrigin origin, ref SKBitmap __result)
        {
            origin = SKEncodedOrigin.TopLeft;
            var c = _ctx;
            if (c == null || c.Tried || !string.Equals(path, c.Path, StringComparison.Ordinal))
            {
                return true;
            }

            c.Tried = true;
            SKBitmap bitmap = null;
            try
            {
                if (forceCleanBitmap != c.AutoOrient || !File.Exists(path))
                {
                    return true;
                }

                using var codec = SKCodec.Create(path, out var res);
                if (res != SKCodecResult.Success || codec == null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg || codec.FrameCount != 0)
                {
                    return true;
                }

                var enc = codec.EncodedOrigin;
                var swap = c.AutoOrient && Transposes(enc);
                var info = codec.Info;
                var fullW = swap ? info.Height : info.Width;
                var fullH = swap ? info.Width : info.Height;
                var full = new ImageDimensions(fullW, fullH);

                // What stock would end up with, and the cases where stock returns the original file.
                if (c.Options.HasDefaultOptions(path, full))
                {
                    return true;
                }

                var target = ImageHelper.GetNewImageSize(c.Options, full);
                if (target.Width <= 0 || target.Height <= 0)
                {
                    return true;
                }

                foreach (var d in Denominators)
                {
                    var s = codec.GetScaledDimensions(1f / d);
                    var sw = swap ? s.Height : s.Width;
                    var sh = swap ? s.Width : s.Height;
                    if (s.Width <= 0 || s.Height <= 0 || (s.Width >= info.Width && s.Height >= info.Height))
                    {
                        continue; // this denominator is not a real reduction
                    }

                    if (sw < Margin * target.Width || sh < Margin * target.Height)
                    {
                        continue;
                    }

                    if (c.Options.HasDefaultOptions(path, new ImageDimensions(sw, sh)))
                    {
                        continue;
                    }

                    bitmap = new SKBitmap(s.Width, s.Height, true);
                    if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                    {
                        bitmap.Dispose();
                        bitmap = null;
                        return true;
                    }

                    c.Scaled = true;
                    c.ScaledW = sw;
                    c.ScaledH = sh;
                    c.FullW = fullW;
                    c.FullH = fullH;
                    c.OutW = target.Width;
                    c.OutH = target.Height;
                    origin = forceCleanBitmap ? enc : SKEncodedOrigin.TopLeft;
                    __result = bitmap;
                    Interlocked.Increment(ref _scaled);
                    _logger?.LogDebug(
                        "JellyfinPlus: scaled decode {Fw}x{Fh} -> {Sw}x{Sh} (1/{D}) for a {Tw}x{Th} output",
                        fullW, fullH, sw, sh, d, target.Width, target.Height);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                bitmap?.Dispose();
                c.Scaled = false;
                _logger?.LogDebug(ex, "JellyfinPlus: scaled decode failed; decoding at full size");
                return true;
            }
        }

        private static void NewSizePrefix(ref ImageDimensions originalImageSize)
        {
            try
            {
                var c = _ctx;
                if (c != null && c.Scaled && !c.Hooked
                    && originalImageSize.Width == c.ScaledW && originalImageSize.Height == c.ScaledH)
                {
                    c.Hooked = true;
                    originalImageSize = new ImageDimensions(c.FullW, c.FullH);
                }
            }
            catch (Exception)
            {
                // leave the size alone; the finalizer's check catches a wrong output
            }
        }

        private static Exception EncodeFinalizer(
            Exception __exception, ref string __result, object __instance, MethodBase __originalMethod, object[] __args, string outputPath)
        {
            var c = _ctx;
            _ctx = null;
            if (c == null || !c.Scaled)
            {
                return __exception;
            }

            string problem = null;
            if (__exception != null)
            {
                problem = "encode threw after a scaled decode: " + __exception.GetType().Name;
            }
            else if (string.Equals(__result, outputPath, StringComparison.Ordinal))
            {
                problem = CheckOutput(c, outputPath);
            }

            if (problem == null)
            {
                return __exception;
            }

            Interlocked.Exchange(ref _disabled, 1);
            _logger?.LogWarning(
                "JellyfinPlus: scaled JPEG decode produced a bad result ({Problem}); switching it off and re-running the encode the stock way.",
                problem);
            try
            {
                if (!string.IsNullOrEmpty(outputPath) && File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                _bypass = true;
                __result = (string)__originalMethod.Invoke(__instance, __args);
                return null;
            }
            catch (TargetInvocationException tie)
            {
                return tie.InnerException ?? tie;
            }
            catch (Exception ex)
            {
                return ex;
            }
            finally
            {
                _bypass = false;
                _ctx = null;
            }
        }

        /// <summary>Null when the written file has the dimensions stock would have produced.</summary>
        private static string CheckOutput(Context c, string outputPath)
        {
            try
            {
                if (!c.Hooked)
                {
                    return "output size hook did not run";
                }

                using var codec = SKCodec.Create(outputPath, out var res);
                if (codec == null || (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput))
                {
                    return "output unreadable (" + res + ")";
                }

                return codec.Info.Width == c.OutW && codec.Info.Height == c.OutH
                    ? null
                    : "output is " + codec.Info.Width + "x" + codec.Info.Height + ", expected " + c.OutW + "x" + c.OutH;
            }
            catch (Exception ex)
            {
                return "output check failed: " + ex.GetType().Name;
            }
        }
    }
}
