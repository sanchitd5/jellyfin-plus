using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.Plus.Patcher.Harness
{
    /// <summary>An in-memory machine for the detection decision: nodes, a driver flag, nvidia-smi.</summary>
    internal sealed class FakeGpuEnvironment : IGpuEnvironment
    {
        public List<KeyValuePair<string, string>> Nodes = new List<KeyValuePair<string, string>>();

        public bool DriverLoaded;

        public bool SmiLists;

        public int SmiCalls;

        public HashSet<string> Files = new HashSet<string>();

        public IReadOnlyList<string> RenderNodes() => Nodes.Select(n => n.Key).ToList();

        public string ReadVendorId(string node) => Nodes.First(n => n.Key == node).Value;

        public string DevicePath(string node) => "/dev/dri/" + node;

        public bool NvidiaDriverLoaded() => DriverLoaded;

        public bool NvidiaSmiLists()
        {
            SmiCalls++;
            return SmiLists;
        }

        public bool FileExists(string path) => Files.Contains(path);
    }

    internal static class Program
    {
        private const string Models = "/usr/lib/jellyfin-ffmpeg-oidn/models";

        private const string OpenVino = "/usr/lib/jellyfin-ffmpeg-oidn/ort/lib/libonnxruntime_providers_openvino.so";

        private static int _failed;

        private static int _passed;

        private static void Check(string name, bool ok, string detail = null)
        {
            if (ok)
            {
                _passed++;
                Console.WriteLine("PASS  " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("FAIL  " + name + (detail == null ? string.Empty : "  (" + detail + ")"));
            }
        }

        private static void Equal(string name, string expected, string actual)
            => Check(name, string.Equals(expected, actual, StringComparison.Ordinal), "expected '" + expected + "', got '" + actual + "'");

        private static FakeGpuEnvironment Machine(params string[] nodeAndId)
        {
            var env = new FakeGpuEnvironment();
            for (int i = 0; i < nodeAndId.Length; i += 2)
            {
                env.Nodes.Add(new KeyValuePair<string, string>(nodeAndId[i], nodeAndId[i + 1]));
            }

            return env;
        }

        private static int Main()
        {
            // The render-node test runs on the calling thread, so every Devices() call below sees its answer.
            UpscaleEngine.DrmVulkanInline = true;
            PciIds();
            Settings();
            Decision();
            SysfsReader();
            NvidiaStaysByteIdentical();
            IntelEncoder();
            IntelChain();
            IntelDevices();
            DrmProbeCache();
            TwoGpuMachines();
            NvidiaOnlyLevels();
            Fdinfo();
            IdleResidency();
            LoadRoute();
            CapabilityJson();

            if (_rig != null)
            {
                try
                {
                    Directory.Delete(_rig, true);
                }
                catch (Exception)
                {
                    // the temp dir is the OS's to clean
                }
            }

            Console.WriteLine();
            Console.WriteLine(_passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void PciIds()
        {
            Equal("pci 0x10de is nvidia", "nvidia", GpuVendors.FromPciId("0x10de"));
            Equal("pci 0x10DE with newline is nvidia", "nvidia", GpuVendors.FromPciId("0x10DE\n"));
            Equal("pci 10de without prefix is nvidia", "nvidia", GpuVendors.FromPciId("10de"));
            Equal("pci 0x8086 is intel", "intel", GpuVendors.FromPciId("0x8086"));
            Equal("pci 0x8086 padded is intel", "intel", GpuVendors.FromPciId("  0x8086  \n"));
            Check("pci 0x1002 (AMD) is not claimed", GpuVendors.FromPciId("0x1002") == null);
            Check("pci blank is null", GpuVendors.FromPciId(string.Empty) == null && GpuVendors.FromPciId(null) == null);
            Check("pci garbage is null", GpuVendors.FromPciId("nvidia") == null);
        }

        private static void Settings()
        {
            Equal("default setting is auto", "auto", new UpscaleSettings().GpuVendor);
            Equal("blank normalises to auto", "auto", GpuVendors.NormaliseSetting("  ", out bool r1));
            Check("blank is recognised", r1);
            Equal("INTEL normalises to intel", "intel", GpuVendors.NormaliseSetting("INTEL", out bool r2));
            Check("INTEL is recognised", r2);
            Equal("unknown normalises to auto", "auto", GpuVendors.NormaliseSetting("amd", out bool r3));
            Check("unknown is reported as unrecognised", !r3);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            Equal("settings JSON without GpuVendor keeps auto", "auto", JsonSerializer.Deserialize<UpscaleSettings>("{\"Enabled\":true}", opts).GpuVendor);
            Equal("settings JSON carries GpuVendor across the boundary", "intel", JsonSerializer.Deserialize<UpscaleSettings>("{\"GpuVendor\":\"intel\"}", opts).GpuVendor);
        }

        private static void Decision()
        {
            var nv = GpuDetector.Resolve("auto", Models, Machine("renderD128", "0x10de"));
            Equal("auto, NVIDIA node: vendor", "nvidia", nv.Vendor);
            Check("auto, NVIDIA node: detected", nv.Detected);
            Equal("auto, NVIDIA node: render node", "/dev/dri/renderD128", nv.RenderNode);
            Equal("auto, NVIDIA node: encoder", "nvenc", nv.EncoderFamily);
            Equal("auto, NVIDIA node: decode", "cuda", nv.DecodeType);
            Check("auto, NVIDIA node: CUDA-only levels", nv.CudaOnlyLevelsAvailable);
            Check("auto, NVIDIA node: no OpenVINO", !nv.OpenVinoAvailable);

            var il = GpuDetector.Resolve("auto", Models, Machine("renderD128", "0x8086"));
            Equal("auto, Intel node: vendor", "intel", il.Vendor);
            Equal("auto, Intel node: render node", "/dev/dri/renderD128", il.RenderNode);
            Equal("auto, Intel node: encoder", "qsv", il.EncoderFamily);
            Equal("auto, Intel node: decode", "vaapi", il.DecodeType);
            Check("auto, Intel node: no CUDA-only levels", !il.CudaOnlyLevelsAvailable);
            Check("auto, Intel node, no provider file: no OpenVINO", !il.OpenVinoAvailable);

            var ilov = Machine("renderD128", "0x8086");
            ilov.Files.Add(OpenVino);
            Check("auto, Intel node + OpenVINO provider file: OpenVINO available", GpuDetector.Resolve("auto", Models, ilov).OpenVinoAvailable);
            var nvov = Machine("renderD128", "0x10de");
            nvov.Files.Add(OpenVino);
            Check("NVIDIA with an OpenVINO provider file: still no OpenVINO", !GpuDetector.Resolve("auto", Models, nvov).OpenVinoAvailable);

            var both = Machine("renderD128", "0x8086", "renderD129", "0x10de");
            var bothAuto = GpuDetector.Resolve("auto", Models, both);
            Equal("auto, both: NVIDIA first", "nvidia", bothAuto.Vendor);
            Equal("auto, both: the NVIDIA node, not the first node", "/dev/dri/renderD129", bothAuto.RenderNode);
            Check("auto, both: the reason names the unused Intel GPU", bothAuto.Reason.Contains("Intel"));
            Check("auto, both: both devices listed", bothAuto.Devices.Count == 2);
            Equal("intel overrides auto on a mixed box", "intel", GpuDetector.Resolve("intel", Models, both).Vendor);
            Equal("intel override uses the Intel node", "/dev/dri/renderD128", GpuDetector.Resolve("intel", Models, both).RenderNode);
            Equal("nvidia override uses the NVIDIA node", "/dev/dri/renderD129", GpuDetector.Resolve("nvidia", Models, both).RenderNode);
            var none = GpuDetector.Resolve("none", Models, both);
            Equal("none overrides detection", "none", none.Vendor);
            Check("none offers nothing", !none.Detected && !none.CudaOnlyLevelsAvailable && !none.OpenVinoAvailable && none.RenderNode == null);
            Equal("none: encoder is software", "software", none.EncoderFamily);

            var nothing = GpuDetector.Resolve("auto", Models, Machine());
            Equal("auto, nothing found: none", "none", nothing.Vendor);
            Check("auto, nothing found: not detected", !nothing.Detected);

            var amd = GpuDetector.Resolve("auto", Models, Machine("renderD128", "0x1002"));
            Equal("auto, only an AMD node: none", "none", amd.Vendor);
            Check("an AMD node is still listed", amd.Devices.Count == 1 && amd.Devices[0].Vendor == null);

            // NVIDIA without a render node (no nvidia_drm): the tie-breakers decide.
            var driver = Machine("renderD128", "0x8086");
            driver.DriverLoaded = true;
            var viaDriver = GpuDetector.Resolve("auto", Models, driver);
            Equal("tie-breaker /proc/driver/nvidia beats an Intel node", "nvidia", viaDriver.Vendor);
            Check("tie-breaker: no render node reported for it", viaDriver.RenderNode == null && viaDriver.Detected);
            Check("tie-breaker: nvidia-smi not run when the driver answered", driver.SmiCalls == 0);

            var smi = Machine();
            smi.SmiLists = true;
            Equal("tie-breaker nvidia-smi alone finds NVIDIA", "nvidia", GpuDetector.Resolve("auto", Models, smi).Vendor);
            Check("tie-breaker nvidia-smi asked once", smi.SmiCalls == 1);

            var node = Machine("renderD128", "0x10de");
            GpuDetector.Resolve("auto", Models, node);
            Check("nvidia-smi not run when a node answered", node.SmiCalls == 0);

            var chosenMissing = GpuDetector.Resolve("nvidia", Models, Machine("renderD128", "0x8086"));
            Equal("nvidia chosen but absent: still nvidia (the setting wins)", "nvidia", chosenMissing.Vendor);
            Check("nvidia chosen but absent: honest negative", !chosenMissing.Detected && !chosenMissing.CudaOnlyLevelsAvailable && chosenMissing.RenderNode == null);
            Check("nvidia chosen but absent: the reason says so", chosenMissing.Reason.Contains("no NVIDIA GPU was found"));

            var intelMissing = GpuDetector.Resolve("intel", Models, Machine("renderD128", "0x10de"));
            Check("intel chosen but absent: honest negative", intelMissing.Vendor == "intel" && !intelMissing.Detected && intelMissing.RenderNode == null);

            var junk = GpuDetector.Resolve("banana", Models, Machine("renderD128", "0x8086"));
            Equal("unrecognised setting is treated as auto", "intel", junk.Vendor);
            Equal("unrecognised setting reports auto", "auto", junk.Setting);
            Check("unrecognised setting is said in the reason", junk.Reason.Contains("banana"));
        }

        private static void SysfsReader()
        {
            string root = Path.Combine(Path.GetTempPath(), "gpu-vendor-fake-" + Guid.NewGuid().ToString("N"));
            try
            {
                string drm = Path.Combine(root, "sys", "class", "drm");
                string dev = Path.Combine(root, "dev", "dri");
                Directory.CreateDirectory(dev);
                Fake(drm, "renderD128", "0x8086\n", dev, true);
                Fake(drm, "renderD129", "0x10de\n", dev, true);
                Fake(drm, "renderD1000", "0x8086\n", dev, true);
                Fake(drm, "renderD130", "0x10de\n", dev, false);
                Directory.CreateDirectory(Path.Combine(drm, "card0"));

                var env = new SystemGpuEnvironment(drm, dev, Path.Combine(root, "proc", "driver", "nvidia"), () => false);
                var nodes = env.RenderNodes();
                Check("sysfs reader lists nodes that have a /dev entry, in numeric order, and not card0",
                    nodes.Count == 3 && nodes[0] == "renderD128" && nodes[1] == "renderD129" && nodes[2] == "renderD1000", string.Join(",", nodes));
                Equal("sysfs reader trims the vendor id", "0x8086", env.ReadVendorId("renderD128"));
                Check("sysfs reader: unreadable vendor is null", env.ReadVendorId("renderD999") == null);
                Check("sysfs reader: no /proc/driver/nvidia", !env.NvidiaDriverLoaded());
                Directory.CreateDirectory(Path.Combine(root, "proc", "driver", "nvidia"));
                Check("sysfs reader: /proc/driver/nvidia present", env.NvidiaDriverLoaded());

                var cap = GpuDetector.Resolve("auto", Models, env);
                Equal("fake sysfs tree, auto: NVIDIA first", "nvidia", cap.Vendor);
                Equal("fake sysfs tree, auto: its node", Path.Combine(dev, "renderD129"), cap.RenderNode);

                var missing = new SystemGpuEnvironment(Path.Combine(root, "nope"), dev, Path.Combine(root, "nope2"), () => false);
                Check("no sysfs at all: empty, not an error", missing.RenderNodes().Count == 0);
                Equal("no sysfs at all: none", "none", GpuDetector.Resolve("auto", Models, missing).Vendor);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception)
                {
                    // the temp dir is the OS's to clean
                }
            }
        }

        private static void Fake(string drm, string node, string vendor, string dev, bool withDevNode)
        {
            string deviceDir = Path.Combine(drm, node, "device");
            Directory.CreateDirectory(deviceDir);
            File.WriteAllText(Path.Combine(deviceDir, "vendor"), vendor);
            if (withDevNode)
            {
                File.WriteAllText(Path.Combine(dev, node), string.Empty);
            }
        }

        // The NVIDIA guarantee. An Intel decision needs an Intel GPU that was actually seen, so on
        // every machine without one, whatever GpuVendor says, each of the strings the NVIDIA path
        // produces must be the exact string it produced before the setting existed. The expected
        // strings are written out by hand from the code, not computed. Run on a machine with no
        // GPU, an NVIDIA one, and a mixed one.
        private static void NvidiaStaysByteIdentical()
        {
            var nvidia = Machine("renderD128", "0x10de");
            var mixed = Machine("renderD128", "0x8086", "renderD129", "0x10de");
            var neither = Machine();
            var intelOnly = Machine("renderD128", "0x8086");

            string[] all = { "nvidia", "auto", "intel", "none", "banana" };
            foreach (string vendor in all)
            {
                Goldens("no GPU", neither, vendor);
                Goldens("NVIDIA box", nvidia, vendor);
            }

            Goldens("mixed box", mixed, "auto");
            Goldens("mixed box", mixed, "nvidia");
            Goldens("mixed box", mixed, "none");

            // An explicit setting naming a vendor that is not there is the honest negative: the
            // engine does not move to a path for hardware it never saw.
            Goldens("Intel-only box, nvidia chosen", intelOnly, "nvidia");
            Goldens("Intel-only box, none chosen", intelOnly, "none");

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
        }

        private static void Goldens(string machine, FakeGpuEnvironment env, string vendor)
        {
            GpuDetector.UseEnvironment(env);
            UpscaleEngine.EncoderListSource = () => new HashSet<string> { "h264_nvenc", "hevc_nvenc", "av1_nvenc", "libx264", "libx265", "hevc_qsv", "h264_qsv" };
            string tag = " [" + machine + ", GpuVendor=" + vendor + "]";
            {
                UpscaleEngine.Settings = new UpscaleSettings { Encoder = "auto", GpuVendor = vendor, GpuResidentEncode = true };

                Equal("HwaccelArgs, Vulkan chain" + tag,
                    " -init_hw_device vulkan=vk:0 -filter_hw_device vk",
                    UpscaleEngine.HwaccelArgs(null));
                Equal("HwaccelArgs, CUDA-native chain" + tag,
                    " -init_hw_device vulkan=vk:0 -filter_hw_device vk -hwaccel cuda -hwaccel_output_format cuda",
                    UpscaleEngine.HwaccelArgs(new UpscaleEngine.Plan { UsesCudaNeural = true }));

                Equal("BuildChain, plain upscale, GPU-resident encode" + tag,
                    "format=yuv420p,hwupload,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos,hwmap=derive_device=cuda",
                    UpscaleEngine.BuildChain(new UpscaleEngine.Plan { Width = 1920, Height = 1080, UpscaleApplied = true }));

                UpscaleEngine.Settings = new UpscaleSettings { Encoder = "auto", GpuVendor = vendor, GpuResidentEncode = false };
                Equal("BuildChain, deband, download for encode" + tag,
                    "format=yuv420p,hwupload,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos:deband=1:deband_threshold=3:deband_grain=0,hwdownload,format=yuv420p",
                    UpscaleEngine.BuildChain(new UpscaleEngine.Plan { Width = 1920, Height = 1080, UpscaleApplied = true, DebandApplied = true }));

                Equal("BuildChain, CUDA-native, no libplacebo" + tag,
                    "cudadenoise,neuralfilter",
                    UpscaleEngine.BuildChain(new UpscaleEngine.Plan
                    {
                        UsesCudaNeural = true,
                        NeuralApplied = true,
                        NeuralFilter = "neuralfilter",
                        CudaDenoiseNode = "cudadenoise",
                    }));
                Equal("BuildChain, CUDA-native bridged through libplacebo" + tag,
                    "hwmap=derive_device=vulkan,libplacebo=w=iw:h=ih:upscaler=ewa_lanczos,hwmap=derive_device=cuda,neuralfilter",
                    UpscaleEngine.BuildChain(new UpscaleEngine.Plan
                    {
                        UsesCudaNeural = true,
                        NeuralApplied = true,
                        NeuralFilter = "neuralfilter",
                        UpscaleApplied = true,
                        Upscaler = "ewa_lanczos",
                    }));

                // Encoder=auto and a request naming no codec reads no ffmpeg and no session.
                string encoder = UpscaleEngine.EncoderFor(null, out string reason);
                Equal("EncoderFor, auto, no client codec" + tag, "hevc_nvenc", encoder);
                Equal("EncoderFor reason, auto, no client codec" + tag,
                    "Encoder set to auto and the client named no codec; using hevc_nvenc", reason);

                // The client's codec maps to its NVENC encoder; the default setting is honoured.
                foreach (var codec in new[] { "h264", "hevc" })
                {
                    string expect = codec + "_nvenc";
                    UpscaleEngine.Settings = new UpscaleSettings { Encoder = "auto", GpuVendor = vendor };
                    Equal("EncoderFor, auto, client " + codec + tag, expect, UpscaleEngine.EncoderFor(Job(codec, codec), out reason));
                    Equal("EncoderFor reason, auto, client " + codec + tag, "Encoder set to auto; following the client-negotiated " + codec, reason);

                    UpscaleEngine.Settings = new UpscaleSettings { Encoder = "hevc_nvenc", GpuVendor = vendor };
                    string wanted = UpscaleEngine.EncoderFor(Job(codec, "h264", "hevc"), out reason);
                    Equal("EncoderFor, default hevc_nvenc, client " + codec + tag, "hevc_nvenc", wanted);
                    Equal("EncoderFor reason, default hevc_nvenc, client " + codec + tag, "configured encoder 'hevc_nvenc' applied", reason);
                }

                // A refused named encoder falls back to the client's NVENC encoder, as before.
                UpscaleEngine.Settings = new UpscaleSettings { Encoder = "hevc_nvenc", GpuVendor = vendor };
                Equal("EncoderFor, hevc_nvenc, client only takes h264" + tag, "h264_nvenc", UpscaleEngine.EncoderFor(Job("h264", "h264"), out reason));
                Equal("EncoderFor reason, hevc_nvenc, client only takes h264" + tag,
                    "session supports [h264] and not hevc; refused configured 'hevc_nvenc' and used h264_nvenc", reason);

                // A named QSV encoder on a box with no Intel GPU is honoured exactly as it always was.
                UpscaleEngine.Settings = new UpscaleSettings { Encoder = "hevc_qsv", GpuVendor = vendor };
                Equal("EncoderFor, hevc_qsv named on a non-Intel box" + tag, "hevc_qsv", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));
                Equal("EncoderFor reason, hevc_qsv named on a non-Intel box" + tag, "configured encoder 'hevc_qsv' applied", reason);

                Check("IsIntelPath is false" + tag, !UpscaleEngine.IsIntelPath());

                var plan = new UpscaleEngine.Plan { Act = true, Width = 1920, Height = 1080, SourceWidth = 1280, SourceHeight = 720 };
                UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = vendor, GpuResidentEncode = true };
                Equal("record: GpuResidentEncode on when asked" + tag, "on", UpscaleEngine.Describe(null, plan, "applied", "x").GpuResidentEncode);
                Equal("record: Pipeline says GPU-resident when asked" + tag, "Vulkan (GPU-resident)", UpscaleEngine.Describe(null, plan, "applied", "x").Pipeline);
                UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = vendor, GpuResidentEncode = false };
                Equal("record: GpuResidentEncode off when not asked" + tag, "off", UpscaleEngine.Describe(null, plan, "applied", "x").GpuResidentEncode);
                Equal("record: Pipeline plain Vulkan when not asked" + tag, "Vulkan", UpscaleEngine.Describe(null, plan, "applied", "x").Pipeline);

                // PR 3: the device and the levels. Every string the NVIDIA path produced before the
                // render-node change, written out by hand, and the render-node test never run.
                int probed = 0;
                UpscaleEngine.DrmVulkanProbeSource = n =>
                {
                    probed++;
                    return true;
                };
                UpscaleEngine.Settings = RigSettings(vendor);
                UpscaleEngine.DeviceInit devices = UpscaleEngine.Devices();
                Equal("Devices: the first Vulkan device" + tag, " -init_hw_device vulkan=vk:0", devices.VulkanArgs);
                Check("Devices: no QSV device, not the Intel path" + tag, devices.QsvArgs == string.Empty && devices.QsvText == null && !devices.Intel);
                Equal("HwaccelArgs, Vulkan chain, after the device change" + tag, " -init_hw_device vulkan=vk:0 -filter_hw_device vk", UpscaleEngine.HwaccelArgs(new UpscaleEngine.Plan()));
                Equal("Vulkan-denoise probe command" + tag,
                    "-hide_banner -loglevel error -init_hw_device vulkan=vk:0 -filter_hw_device vk -f lavfi -i testsrc=size=64x64:rate=1 -frames:v 1 -vf \"format=yuv420p,hwupload,nlmeans_vulkan,hwdownload,format=yuv420p\" -f null -",
                    UpscaleEngine.VulkanDenoiseProbeArgs());
                Equal("AV1 QSV test-encode command" + tag,
                    "-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:r=1 -frames:v 1 -vf format=nv12 -c:v av1_qsv -f null -",
                    UpscaleEngine.Av1QsvProbeArgs());
                SessionRecord enhanced = UpscaleEngine.Describe(null, plan, "applied", "x");
                Equal("record: VulkanDevice is vk:0" + tag, "vk:0", enhanced.VulkanDevice);
                Check("record: no QSV device and nothing withheld" + tag, enhanced.QsvDevice == null && enhanced.GpuWithheld == null);
                Check("record: a session that was not enhanced reports no device" + tag, UpscaleEngine.Describe(null, new UpscaleEngine.Plan(), "not-requested", "x").VulkanDevice == null);
                Check("the render-node test never ran" + tag, probed == 0);
                UpscaleEngine.DrmVulkanProbeSource = null;

                // The probe, level by level, is what ShaderLibrary lists, with no new key; the status route likewise.
                JsonElement levels = Probe();
                Equal("probe: Denoise is the full list" + tag, string.Join(",", ShaderLibrary.AvailableDenoiseLevels()), string.Join(",", Strings(levels, "Denoise")));
                Equal("probe: Neural is the full list" + tag, string.Join(",", ShaderLibrary.AvailableNeuralLevels(UpscaleEngine.Settings)), string.Join(",", Strings(levels, "Neural")));
                Equal("probe: Game is the full list" + tag, string.Join(",", ShaderLibrary.AvailableGameLevels(UpscaleEngine.Settings)), string.Join(",", Strings(levels, "Game")));
                Equal("probe: GameOptionLevels is the full list" + tag, string.Join(",", ShaderLibrary.GameOptionLevels(UpscaleEngine.Settings)), string.Join(",", Strings(levels, "GameOptionLevels")));
                Check("probe: the OptiX, DLPP, VSR and game levels are all still offered" + tag,
                    new[] { "optix", "optix-temporal" }.All(Strings(levels, "Denoise").Contains)
                    && new[] { "vsr-rtcuda", "dlpp-1", "dlpp-4" }.All(Strings(levels, "Neural").Contains)
                    && new[] { "fsr2", "dlss", "dlaa" }.All(Strings(levels, "Game").Contains));
                Equal("probe: the same keys as before PR 3 (no Withheld)" + tag, ProbeKeys, string.Join(",", levels.EnumerateObject().Select(pp => pp.Name).OrderBy(n => n, StringComparer.Ordinal)));
                using (JsonDocument status = JsonDocument.Parse(PatcherHost.StatusJson()))
                {
                    Check("status: no Withheld key" + tag, !status.RootElement.TryGetProperty("Withheld", out _));
                }
            }
        }

        // Every key the probe carried before PR 3, sorted: the NVIDIA probe must still have exactly these.
        private const string ProbeKeys =
            "Chroma,Deblock,Deblur,Denoise,Game,GameDepth,GameDepthLabels,GameJitter,GameJitterLabels,GameLabels,GameOptionLevels,GameReactive,GameReactiveLabels,Gpu,MaxSourceHeight,MaxTargetHeight,MinScaleFactor,Neural,NeuralLabels,Refine,Sr,SrMinScaleFactor,Upscalers";

        // A directory with the files that make the NVIDIA-only levels, and one ONNX level that is not, offerable.
        private static string _rig;

        private static string Rig()
        {
            if (_rig != null)
            {
                return _rig;
            }

            string root = Path.Combine(Path.GetTempPath(), "gpu-vendor-rig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "models"));
            Directory.CreateDirectory(Path.Combine(root, "dlss"));
            File.WriteAllText(Path.Combine(root, "models", "realesr-animevideo-x2-fp16.onnx"), "x");
            File.WriteAllText(Path.Combine(root, "dlpp.dll"), "x");
            File.WriteAllText(Path.Combine(root, "vsr.dll"), "x");
            File.WriteAllText(Path.Combine(root, "dlss", "libnvidia-ngx-dlss.so.3"), "x");
            _rig = root;
            return _rig;
        }

        private static UpscaleSettings RigSettings(string vendor) => new UpscaleSettings
        {
            Encoder = "auto",
            GpuVendor = vendor,
            NeuralModelDirectory = Path.Combine(Rig(), "models"),
            RtxDlppDllPath = Path.Combine(Rig(), "dlpp.dll"),
            RtxVsrDllPath = Path.Combine(Rig(), "vsr.dll"),
            DlssRuntimeDirectory = Path.Combine(Rig(), "dlss"),
        };

        private static JsonElement Probe()
        {
            using (JsonDocument doc = JsonDocument.Parse(PatcherHost.SessionJson("harness-no-such-session")))
            {
                return doc.RootElement.GetProperty("Levels").Clone();
            }
        }

        private static List<string> Strings(JsonElement levels, string key) => levels.GetProperty(key).EnumerateArray().Select(e => e.GetString()).ToList();

        private static List<string> Keys(JsonElement obj) => obj.EnumerateObject().Select(pp => pp.Name).ToList();

        // A job with a negotiated codec and the codecs the device profile says it can play.
        private static EncodingJobInfo Job(string negotiated, params string[] supported)
        {
            return new FakeJob
            {
                BaseRequest = new MediaBrowser.Controller.MediaEncoding.BaseEncodingJobOptions { VideoCodec = negotiated },
                SupportedVideoCodecs = supported,
            };
        }

        private sealed class FakeJob : EncodingJobInfo
        {
            public FakeJob()
                : base(TranscodingJobType.Hls)
            {
            }
        }

        private static HashSet<string> Encoders(params string[] names) => new HashSet<string>(names);

        private static readonly string[] FullList =
        {
            "h264_qsv", "hevc_qsv", "av1_qsv", "h264_vaapi", "hevc_vaapi", "av1_vaapi",
            "h264_nvenc", "hevc_nvenc", "av1_nvenc", "libx264", "libx265", "libsvtav1",
        };

        private static void OnIntel(string setting, string encoder, params string[] list)
        {
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.EncoderListSource = () => Encoders(list);
            UpscaleEngine.Av1QsvProbeSource = _ => true;
            UpscaleEngine.DrmVulkanProbeSource = n => true;
            UpscaleEngine.Settings = new UpscaleSettings { Encoder = encoder, GpuVendor = setting };
        }

        // PR 2: what an Intel server encodes with. Every case here is an Intel GPU that was seen.
        private static void IntelEncoder()
        {
            string reason;
            OnIntel("auto", "hevc_nvenc", FullList);
            Check("an Intel box is the Intel path", UpscaleEngine.IsIntelPath());
            Equal("the default setting hevc_nvenc, client hevc: hevc_qsv", "hevc_qsv", UpscaleEngine.EncoderFor(Job("hevc", "hevc", "h264"), out reason));
            Equal("the default setting is treated as auto, and the reason says so",
                "configured 'hevc_nvenc' is an NVIDIA encoder and this server's GPU is Intel; treated as auto: Encoder set to auto; following the client-negotiated hevc (hevc_qsv)", reason);
            Equal("hevc_nvenc setting, client h264: h264_qsv", "h264_qsv", UpscaleEngine.EncoderFor(Job("h264", "h264"), out reason));
            Equal("hevc_nvenc setting, client av1 (test encode passes): av1_qsv", "av1_qsv", UpscaleEngine.EncoderFor(Job("av1", "av1", "hevc"), out reason));
            Equal("hevc_nvenc setting, no client codec: hevc_qsv", "hevc_qsv", UpscaleEngine.EncoderFor(null, out reason));
            Equal("hevc_nvenc setting, no client codec: the reason",
                "configured 'hevc_nvenc' is an NVIDIA encoder and this server's GPU is Intel; treated as auto: Encoder set to auto and the client named no codec; using hevc_qsv", reason);

            OnIntel("auto", "av1_nvenc", FullList);
            Equal("any *_nvenc setting is foreign on Intel", "h264_qsv", UpscaleEngine.EncoderFor(Job("h264", "h264"), out reason));
            Check("av1_nvenc setting: said so", reason.StartsWith("configured 'av1_nvenc' is an NVIDIA encoder"), reason);

            OnIntel("auto", "auto", FullList);
            Equal("auto, client hevc: hevc_qsv", "hevc_qsv", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));
            Equal("auto, client hevc: the reason", "Encoder set to auto; following the client-negotiated hevc (hevc_qsv)", reason);
            OnIntel("auto", string.Empty, FullList);
            Equal("blank setting is auto too", "h264_qsv", UpscaleEngine.EncoderFor(Job("h264", "h264"), out reason));

            // AV1 without an AV1 encoder in the GPU: the test encode fails and the choice swaps.
            OnIntel("auto", "auto", FullList);
            UpscaleEngine.Av1QsvProbeSource = _ => false;
            Equal("av1_qsv fails its test encode, client takes hevc: hevc_qsv", "hevc_qsv", UpscaleEngine.EncoderFor(Job("av1", "av1", "hevc"), out reason));
            Equal("av1_qsv swap: the reason", "a test encode with av1_qsv failed on this GPU, used hevc_qsv instead of av1_qsv; Encoder set to auto; following the client-negotiated av1 (av1_qsv)", reason);
            Equal("av1_qsv fails its test encode, client takes only h264: h264_qsv", "h264_qsv", UpscaleEngine.EncoderFor(Job("av1", "av1", "h264"), out reason));
            UpscaleEngine.Av1QsvProbeSource = _ => null;
            Equal("an inconclusive AV1 test leaves av1_qsv alone", "av1_qsv", UpscaleEngine.EncoderFor(Job("av1", "av1", "hevc"), out reason));
            Equal("GuardAv1 on Intel leaves other encoders alone", "hevc_qsv", UpscaleEngine.GuardAv1(null, "hevc_qsv", out reason));
            Check("GuardAv1 on Intel: nothing swapped, no reason", reason == null);
            UpscaleEngine.Av1QsvProbeSource = _ => false;
            Equal("GuardAv1 on Intel swaps a test-encode failure", "hevc_qsv", UpscaleEngine.GuardAv1(null, "av1_qsv", out reason));

            // The plan's DeviceInit is the one the test encode runs on; GuardAv1Intel must not resolve its own.
            var planDevices = new UpscaleEngine.DeviceInit { QsvArgs = " -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD999" };
            UpscaleEngine.DeviceInit probed = null;
            UpscaleEngine.Av1QsvProbeSource = d => { probed = d; return false; };
            UpscaleEngine.GuardAv1(null, "av1_qsv", out reason, planDevices);
            Check("GuardAv1 on Intel probes on the plan's DeviceInit, not a fresh Devices()", ReferenceEquals(probed, planDevices));
            probed = null;
            UpscaleEngine.EncoderFor(Job("av1", "av1", "hevc"), out reason, planDevices);
            Check("EncoderFor hands the plan's DeviceInit to GuardAv1", ReferenceEquals(probed, planDevices));
            UpscaleEngine.Av1QsvProbeSource = _ => false;

            // The encoder list decides between QSV and software, and never produces VA-API or NVENC.
            OnIntel("auto", "auto", "libx264", "libx265", "hevc_vaapi", "h264_vaapi");
            Equal("no QSV, ffmpeg has libx265, client hevc: libx265", "libx265", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));
            Equal("no QSV: the reason", "Encoder set to auto; following the client-negotiated hevc (libx265); ffmpeg has no hevc_qsv, so libx265 (software)", reason);
            Equal("no QSV, client h264: libx264", "libx264", UpscaleEngine.EncoderFor(Job("h264", "h264"), out reason));
            Equal("no QSV: the record's family is software", "software", UpscaleEngine.Capability().EncoderFamily);
            Check("no QSV: the record's reason says VA-API cannot be fed", UpscaleEngine.Capability().Reason.Contains("VA-API encoder cannot take this chain"));
            OnIntel("auto", "auto", "hevc_vaapi");
            Equal("no QSV and no software encoder: hevc_qsv, with the failure said", "hevc_qsv", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));
            Check("...and the reason says it will fail to open", reason.Contains("ffmpeg lists neither hevc_qsv nor libx265"), reason);
            UpscaleEngine.EncoderListSource = () => null;
            Equal("unreadable ffmpeg list: the usual QSV choice", "hevc_qsv", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));
            Equal("unreadable ffmpeg list: record family stays qsv", "qsv", UpscaleEngine.Capability().EncoderFamily);
            OnIntel("auto", "auto", FullList);
            Equal("a QSV encoder listed: record family qsv", "qsv", UpscaleEngine.Capability().EncoderFamily);

            // A named encoder.
            OnIntel("auto", "hevc_vaapi", FullList);
            Equal("hevc_vaapi named: refused, hevc_qsv used", "hevc_qsv", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));
            Check("hevc_vaapi named: the reason says it cannot take system-memory frames", reason.Contains("cannot take the system-memory frames"), reason);
            OnIntel("auto", "libx265", FullList);
            Equal("libx265 named: applied", "libx265", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));
            OnIntel("auto", "hevc_qsv", FullList);
            Equal("hevc_qsv named: applied", "hevc_qsv", UpscaleEngine.EncoderFor(Job("hevc", "hevc", "h264"), out reason));
            Equal("hevc_qsv named: the reason", "configured encoder 'hevc_qsv' applied", reason);
            Equal("hevc_qsv named, the client only takes h264: h264_qsv", "h264_qsv", UpscaleEngine.EncoderFor(Job("h264", "h264"), out reason));
            OnIntel("auto", "hevc_qsv", "h264_qsv");
            Equal("hevc_qsv named but not in ffmpeg: the client's encoder", "hevc_qsv", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));

            // Never NVENC, never CUDA, whatever the setting and whatever the client says.
            bool leaked = false;
            string leakedWhat = null;
            foreach (string setting in new[] { "auto", string.Empty, "hevc_nvenc", "h264_nvenc", "av1_nvenc", "hevc_qsv", "h264_qsv", "av1_qsv", "libx264", "banana" })
            {
                foreach (string codec in new string[] { null, "h264", "hevc", "av1" })
                {
                    foreach (bool resident in new[] { false, true })
                    {
                        OnIntel("auto", setting, FullList);
                        UpscaleEngine.Settings.GpuResidentEncode = resident;
                        string e = UpscaleEngine.EncoderFor(codec == null ? null : Job(codec, "h264", "hevc", "av1"), out reason);
                        string chain = UpscaleEngine.BuildChain(new UpscaleEngine.Plan { Width = 1920, Height = 1080, UpscaleApplied = true });
                        string hw = UpscaleEngine.HwaccelArgs(new UpscaleEngine.Plan());
                        string all = (e + "|" + chain + "|" + hw).ToLowerInvariant();
                        if (all.Contains("nvenc") || all.Contains("cuda") || all.Contains("vaapi"))
                        {
                            leaked = true;
                            leakedWhat = setting + "/" + codec + "/" + resident + ": " + all;
                        }
                    }
                }
            }

            Check("Intel never emits nvenc, cuda or vaapi in the encoder, the chain or the device arguments", !leaked, leakedWhat);

            // The Intel setting with no Intel GPU behind it is not the Intel path.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.Settings = new UpscaleSettings { Encoder = "hevc_nvenc", GpuVendor = "intel" };
            Check("Intel chosen on an NVIDIA-only box is not the Intel path", !UpscaleEngine.IsIntelPath());
            Equal("...and the encoder is the NVIDIA one", "hevc_nvenc", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));

            // A mixed box: auto stays NVIDIA, intel moves to Intel.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086", "renderD129", "0x10de"));
            UpscaleEngine.Settings = new UpscaleSettings { Encoder = "hevc_nvenc", GpuVendor = "auto" };
            Equal("mixed box, auto: NVENC", "hevc_nvenc", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));
            UpscaleEngine.Settings = new UpscaleSettings { Encoder = "hevc_nvenc", GpuVendor = "intel" };
            Equal("mixed box, intel: QSV", "hevc_qsv", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason));

            // What Jellyfin's own choice may stay.
            Check("Jellyfin's hevc_qsv fits the chain", UpscaleEngine.EncoderFitsIntelChain("hevc_qsv"));
            Check("Jellyfin's libx264 fits the chain", UpscaleEngine.EncoderFitsIntelChain("libx264"));
            Check("Jellyfin's h264_vaapi does not (VA-API frames)", !UpscaleEngine.EncoderFitsIntelChain("h264_vaapi"));
            Check("Jellyfin's hevc_nvenc does not", !UpscaleEngine.EncoderFitsIntelChain("hevc_nvenc"));
            Check("copy, blank and null do not", !UpscaleEngine.EncoderFitsIntelChain("copy") && !UpscaleEngine.EncoderFitsIntelChain(" ") && !UpscaleEngine.EncoderFitsIntelChain(null));

            Check("an NVENC name is foreign, any other is not", UpscaleEngine.IsNvencEncoder("HEVC_NVENC") && !UpscaleEngine.IsNvencEncoder("hevc_qsv") && !UpscaleEngine.IsNvencEncoder(null));

            // Jellyfin chose its own QSV encoder and the setting is the NVENC default: kept.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.Settings = new UpscaleSettings { Encoder = "hevc_nvenc", GpuVendor = "auto" };
            string jellyfinChoice = "h264_qsv";
            bool handled = UpscalePatches.ApplyIntelEncoder(Job("h264", "h264"), UpscaleEngine.Settings, ref jellyfinChoice);
            Check("Jellyfin's own h264_qsv is kept over the NVENC default", handled && jellyfinChoice == "h264_qsv", jellyfinChoice);
            jellyfinChoice = "libx264";
            handled = UpscalePatches.ApplyIntelEncoder(Job("h264", "h264"), new UpscaleSettings { Encoder = "auto", GpuVendor = "auto" }, ref jellyfinChoice);
            Check("auto keeps Jellyfin's libx264", handled && jellyfinChoice == "libx264", jellyfinChoice);
            jellyfinChoice = "hevc_qsv";
            handled = UpscalePatches.ApplyIntelEncoder(Job("hevc", "hevc"), new UpscaleSettings { Encoder = "libx265", GpuVendor = "auto" }, ref jellyfinChoice);
            Check("a named non-NVENC encoder is left to the usual path", !handled && jellyfinChoice == "hevc_qsv", jellyfinChoice);
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            jellyfinChoice = "h264_nvenc";
            handled = UpscalePatches.ApplyIntelEncoder(Job("h264", "h264"), new UpscaleSettings { Encoder = "hevc_nvenc", GpuVendor = "auto" }, ref jellyfinChoice);
            Check("on NVIDIA the Intel branch does not run", !handled && jellyfinChoice == "h264_nvenc", jellyfinChoice);

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
        }

        // PR 2: what the chain ends in on Intel. The GPU-resident hand-off is a Vulkan-to-CUDA map and
        // exists only for NVENC.
        private static void IntelChain()
        {
            foreach (bool resident in new[] { true, false })
            {
                OnIntel("auto", "hevc_nvenc", FullList);
                UpscaleEngine.Settings.GpuResidentEncode = resident;
                string tag = " [GpuResidentEncode=" + resident + "]";
                Equal("Intel BuildChain ends in a download, never the CUDA hand-off" + tag,
                    "format=yuv420p,hwupload,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos,hwdownload,format=yuv420p",
                    UpscaleEngine.BuildChain(new UpscaleEngine.Plan { Width = 1920, Height = 1080, UpscaleApplied = true }));
                Equal("Intel HwaccelArgs starts the Vulkan device from the render node, never vk:0, and pins QSV to it" + tag,
                    " -init_hw_device drm=dr:/dev/dri/renderD128 -init_hw_device vulkan=vk@dr -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD128 -filter_hw_device vk",
                    UpscaleEngine.HwaccelArgs(null));

                var plan = new UpscaleEngine.Plan { Act = true, Width = 1920, Height = 1080, SourceWidth = 1280, SourceHeight = 720 };
                SessionRecord record = UpscaleEngine.Describe(null, plan, "applied", "x");
                Equal("Intel record: GpuResidentEncode is n/a" + tag, "n/a", record.GpuResidentEncode);
                Equal("Intel record: Pipeline is plain Vulkan" + tag, "Vulkan", record.Pipeline);
                Equal("Intel record: the capability reports qsv" + tag, "qsv", record.Gpu.EncoderFamily);
                Equal("Intel record: the vendor" + tag, "intel", record.Gpu.Vendor);

                var cuda = UpscaleEngine.Describe(null, new UpscaleEngine.Plan { Act = true, UsesCudaNeural = true, Width = 1920, Height = 1080 }, "applied", "x");
                Check("a CUDA-native session reports no GpuResidentEncode value" + tag, cuda.GpuResidentEncode == null);
                var off = UpscaleEngine.Describe(null, new UpscaleEngine.Plan(), "not-requested", "x");
                Check("a session that was not enhanced reports no GpuResidentEncode value" + tag, off.GpuResidentEncode == null);
            }

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
        }

        // PR 3: the Vulkan and QSV device on a detected Intel GPU. The expected strings are written
        // out by hand from the ffmpeg syntax in GPU_VENDORS.md 5.3.
        private static void IntelDevices()
        {
            const string Drm128 = " -init_hw_device drm=dr:/dev/dri/renderD128 -init_hw_device vulkan=vk@dr";
            const string Qsv128 = " -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD128";
            var asked = new List<string>();

            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.DrmVulkanProbeSource = n =>
            {
                asked.Add(n);
                return true;
            };
            UpscaleEngine.Settings = new UpscaleSettings { Encoder = "auto", GpuVendor = "auto" };

            Equal("one Intel GPU: HwaccelArgs", Drm128 + Qsv128 + " -filter_hw_device vk", UpscaleEngine.HwaccelArgs(null));
            Check("one Intel GPU: never vk:0", !UpscaleEngine.HwaccelArgs(null).Contains("vk:0"));
            Equal("one Intel GPU: the QSV device is pinned to the detected node", Qsv128, UpscaleEngine.Devices().QsvArgs);
            Equal("one Intel GPU: the encoder's device in the record", "/dev/dri/renderD128", UpscaleEngine.Devices().QsvText);
            Equal("one Intel GPU: the Vulkan device in the record", "render node /dev/dri/renderD128", UpscaleEngine.Devices().VulkanText);
            Check("the render-node test was asked about the node, and only that node", asked.Count > 0 && asked.All(n => n == "/dev/dri/renderD128"));
            Equal("the render-node test command",
                "-hide_banner -loglevel error -init_hw_device drm=dr:/dev/dri/renderD128 -init_hw_device vulkan=vk@dr -filter_hw_device vk -f lavfi -i testsrc=size=64x64:rate=1 -frames:v 1 -vf format=yuv420p,hwupload,hwdownload,format=yuv420p -f null -",
                UpscaleEngine.DrmVulkanProbeArgs("/dev/dri/renderD128"));
            Equal("Vulkan-denoise probe tests the session's device",
                "-hide_banner -loglevel error" + Drm128 + " -filter_hw_device vk -f lavfi -i testsrc=size=64x64:rate=1 -frames:v 1 -vf \"format=yuv420p,hwupload,nlmeans_vulkan,hwdownload,format=yuv420p\" -f null -",
                UpscaleEngine.VulkanDenoiseProbeArgs());
            Equal("AV1 QSV test encode uses the pinned QSV device",
                "-hide_banner -loglevel error" + Qsv128 + " -f lavfi -i color=c=black:s=640x360:r=1 -frames:v 1 -vf format=nv12 -c:v av1_qsv -f null -",
                UpscaleEngine.Av1QsvProbeArgs());

            // A CUDA-native plan cannot reach an Intel server (Decide drops those levels), and if one
            // ever did, the command still carries no CUDA.
            string cuda = UpscaleEngine.HwaccelArgs(new UpscaleEngine.Plan { UsesCudaNeural = true });
            Equal("Intel: a CUDA-native plan still gets the Vulkan and QSV devices only", Drm128 + Qsv128 + " -filter_hw_device vk", cuda);

            // The record says which device the command used.
            var plan = new UpscaleEngine.Plan { Act = true, Width = 1920, Height = 1080, SourceWidth = 1280, SourceHeight = 720 };
            SessionRecord record = UpscaleEngine.Describe(null, plan, "applied", "x");
            Equal("record: VulkanDevice names the node", "render node /dev/dri/renderD128", record.VulkanDevice);
            Equal("record: QsvDevice names the pinned node", "/dev/dri/renderD128", record.QsvDevice);
            Check("record: a session that was not enhanced names no device",
                UpscaleEngine.Describe(null, new UpscaleEngine.Plan(), "not-requested", "x").VulkanDevice == null
                && UpscaleEngine.Describe(null, new UpscaleEngine.Plan(), "not-requested", "x").QsvDevice == null);

            // The test fails: the first Vulkan device, and the record says why. Never a dead stream from this change.
            UpscaleEngine.DrmVulkanProbeSource = n => false;
            Equal("the render-node test fails: vk:0, QSV still pinned", " -init_hw_device vulkan=vk:0" + Qsv128 + " -filter_hw_device vk", UpscaleEngine.HwaccelArgs(null));
            Equal("the render-node test fails: the record says so", "vk:0 (a test start from /dev/dri/renderD128 failed, so the first Vulkan device)", UpscaleEngine.Devices().VulkanText);
            Equal("the render-node test fails: the Vulkan-denoise probe follows", "-hide_banner -loglevel error -init_hw_device vulkan=vk:0 -filter_hw_device vk -f lavfi -i testsrc=size=64x64:rate=1 -frames:v 1 -vf \"format=yuv420p,hwupload,nlmeans_vulkan,hwdownload,format=yuv420p\" -f null -", UpscaleEngine.VulkanDenoiseProbeArgs());

            // The test cannot tell (no ffmpeg, a timeout): the node is trusted, and the record says it was not tested.
            UpscaleEngine.DrmVulkanProbeSource = n => null;
            Equal("the render-node test is inconclusive: the node is used", Drm128 + Qsv128 + " -filter_hw_device vk", UpscaleEngine.HwaccelArgs(null));
            Equal("the render-node test is inconclusive: the record says it was not tested", "render node /dev/dri/renderD128 (not tested: ffmpeg could not be run)", UpscaleEngine.Devices().VulkanText);
            UpscaleEngine.DrmVulkanProbeSource = n => true;

            // The QSV device is pinned to the detected node whatever its number: the encoder's own search is not trusted to pick it.
            foreach (int number in new[] { 128, 129, 135, 136, 140, 191 })
            {
                string node = "/dev/dri/renderD" + number;
                GpuDetector.UseEnvironment(Machine("renderD" + number, "0x8086"));
                Equal("renderD" + number + ": HwaccelArgs pins the QSV device to the same node",
                    " -init_hw_device drm=dr:" + node + " -init_hw_device vulkan=vk@dr -init_hw_device qsv=qs:hw_any,child_device=" + node + " -filter_hw_device vk",
                    UpscaleEngine.HwaccelArgs(null));
                Equal("renderD" + number + ": the record names the pinned node", node, UpscaleEngine.Devices().QsvText);
                Equal("renderD" + number + ": the AV1 test encode uses the same QSV device",
                    "-hide_banner -loglevel error -init_hw_device qsv=qs:hw_any,child_device=" + node + " -f lavfi -i color=c=black:s=640x360:r=1 -frames:v 1 -vf format=nv12 -c:v av1_qsv -f null -",
                    UpscaleEngine.Av1QsvProbeArgs());
            }

            // ...and not when ffmpeg lists no QSV encoder: nothing would use it, and a QSV device that cannot start must not end a software session.
            GpuDetector.UseEnvironment(Machine("renderD140", "0x8086"));
            UpscaleEngine.EncoderListSource = () => Encoders("libx264", "libx265");
            Equal("renderD140, no QSV encoder: no QSV device", " -init_hw_device drm=dr:/dev/dri/renderD140 -init_hw_device vulkan=vk@dr -filter_hw_device vk", UpscaleEngine.HwaccelArgs(null));
            Equal("renderD140, no QSV encoder: the record says none", "none (ffmpeg lists no QSV encoder)", UpscaleEngine.Devices().QsvText);
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);

            // A node path that is not a plain render node is never put on a command line.
            GpuDetector.UseEnvironment(Machine("render X;rm", "0x8086"));
            Equal("an odd node name: vk:0", " -init_hw_device vulkan=vk:0 -filter_hw_device vk", UpscaleEngine.HwaccelArgs(null));
            Equal("an odd node name: the record says why", "vk:0 (the Intel render node has no usable path)", UpscaleEngine.Devices().VulkanText);

            // The same device on every command line it appears on, across the Intel settings that mean Intel.
            bool stray = false;
            foreach (string setting in new[] { "auto", "intel" })
            {
                GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
                UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = setting };
                string all = (UpscaleEngine.HwaccelArgs(null) + UpscaleEngine.VulkanDenoiseProbeArgs() + UpscaleEngine.Av1QsvProbeArgs()).ToLowerInvariant();
                stray |= all.Contains("nvenc") || all.Contains("cuda") || all.Contains("vaapi");
            }

            Check("Intel's device arguments and probes name no nvenc, cuda or vaapi", !stray);

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
        }

        private static bool WaitFor(Func<bool> done)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!done())
            {
                if (clock.ElapsedMilliseconds > 5000)
                {
                    return false;
                }

                System.Threading.Thread.Sleep(10);
            }

            return true;
        }

        // Review of PR 3: the render-node test runs in the background, true is kept for good, false
        // and inconclusive results are tested again after a window, and the stale one is served meanwhile.
        private static void DrmProbeCache()
        {
            const string Node = "/dev/dri/renderD128";
            const string Found = "render node " + Node;
            const string Failed = "vk:0 (a test start from " + Node + " failed, so the first Vulkan device)";
            const string Untested = Found + " (not tested: ffmpeg could not be run)";
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.Settings = new UpscaleSettings { Encoder = "auto", GpuVendor = "auto" };
            UpscaleEngine.DrmVulkanClock = () => now;

            // A session-path call never waits for the test, and concurrent callers share one test.
            UpscaleEngine.DrmVulkanInline = false;
            var release = new System.Threading.ManualResetEventSlim(false);
            var started = new System.Threading.ManualResetEventSlim(false);
            int asks = 0;
            UpscaleEngine.DrmVulkanProbeSource = n =>
            {
                System.Threading.Interlocked.Increment(ref asks);
                started.Set();
                release.Wait(10000);
                return true;
            };
            var timer = System.Diagnostics.Stopwatch.StartNew();
            UpscaleEngine.DeviceInit first = UpscaleEngine.Devices();
            timer.Stop();
            Check("a first call returns at once while the test is blocked", timer.ElapsedMilliseconds < 2000, timer.ElapsedMilliseconds + " ms");
            Equal("the untested node is used and the record says the test is pending", Found + " (test pending)", first.VulkanText);
            Equal("...the command uses the node", " -init_hw_device drm=dr:" + Node + " -init_hw_device vulkan=vk@dr", first.VulkanArgs);
            Check("the test was started in the background", started.Wait(5000));
            System.Threading.Tasks.Parallel.For(0, 8, _ => UpscaleEngine.Devices());
            Equal("concurrent callers share one test", "1", asks.ToString());
            release.Set();
            Check("the answer arrives and is used", WaitFor(() => UpscaleEngine.Devices().VulkanText == Found));
            Equal("a true result is one test only", "1", asks.ToString());
            now = now.AddHours(24);
            UpscaleEngine.DrmVulkanInline = true;
            UpscaleEngine.Devices();
            Equal("a true result is never asked again", "1", asks.ToString());

            // False: kept inside the window, tested again after it, and the stale value is served during the re-test.
            System.Threading.ManualResetEventSlim gate = null;
            var retestStarted = new System.Threading.ManualResetEventSlim(false);
            bool? answer = false;
            asks = 0;
            Func<string, bool?> probe = n =>
            {
                System.Threading.Interlocked.Increment(ref asks);
                var g = gate;
                if (g != null)
                {
                    retestStarted.Set();
                    g.Wait(10000);
                }

                return answer;
            };
            now = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            UpscaleEngine.DrmVulkanProbeSource = probe;
            Equal("false: vk:0 and the record says why", Failed, UpscaleEngine.Devices().VulkanText);
            Equal("false: tested once", "1", asks.ToString());
            now = now.AddMinutes(4);
            UpscaleEngine.Devices();
            Equal("false: not asked again inside the window", "1", asks.ToString());
            now = now.AddMinutes(2);
            UpscaleEngine.DrmVulkanInline = false;
            gate = new System.Threading.ManualResetEventSlim(false);
            answer = true;
            timer.Restart();
            string stale = UpscaleEngine.Devices().VulkanText;
            timer.Stop();
            Equal("false: after the window the stale value is served while it is tested again", Failed, stale);
            Check("false: and the call did not wait for the re-test", timer.ElapsedMilliseconds < 2000, timer.ElapsedMilliseconds + " ms");
            Check("false: the re-test started in the background", retestStarted.Wait(5000));
            System.Threading.Tasks.Parallel.For(0, 8, _ => UpscaleEngine.Devices());
            Equal("false: one re-test however many callers", "2", asks.ToString());
            gate.Set();
            Check("false: a later true replaces it", WaitFor(() => UpscaleEngine.Devices().VulkanText == Found));
            gate = null;
            now = now.AddHours(24);
            UpscaleEngine.DrmVulkanInline = true;
            UpscaleEngine.Devices();
            Equal("false then true: kept for good", "2", asks.ToString());

            // Inconclusive (null): same window, same replacement.
            asks = 0;
            answer = null;
            UpscaleEngine.DrmVulkanProbeSource = probe;
            Equal("null: the node is used and the record says it was not tested", Untested, UpscaleEngine.Devices().VulkanText);
            now = now.AddMinutes(1);
            UpscaleEngine.Devices();
            Equal("null: not asked again inside the window", "1", asks.ToString());
            now = now.AddMinutes(5);
            answer = true;
            Equal("null: asked again after the window, and a true replaces it", Found, UpscaleEngine.Devices().VulkanText);
            Equal("null: two tests in all", "2", asks.ToString());

            // Start-up warming runs the test on Intel and not on NVIDIA.
            asks = 0;
            UpscaleEngine.DrmVulkanProbeSource = probe;
            UpscaleEngine.WarmDevices();
            Equal("WarmDevices asks the render-node test on an Intel server", "1", asks.ToString());
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            UpscaleEngine.DrmVulkanProbeSource = probe;
            asks = 0;
            UpscaleEngine.WarmDevices();
            Equal("WarmDevices asks nothing on an NVIDIA server", "0", asks.ToString());

            // One DeviceInit per plan: the command and the record keep the answer the plan was decided with.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            answer = true;
            UpscaleEngine.DrmVulkanProbeSource = probe;
            var plan = new UpscaleEngine.Plan { Act = true, Width = 1920, Height = 1080, SourceWidth = 1280, SourceHeight = 720, Devices = UpscaleEngine.Devices() };
            string decided = UpscaleEngine.HwaccelArgs(plan);
            answer = false;
            UpscaleEngine.DrmVulkanProbeSource = probe;
            Equal("a plan keeps its device when the test answer changes", decided, UpscaleEngine.HwaccelArgs(plan));
            Equal("...and the record names the same device", Found, UpscaleEngine.Describe(null, plan, "applied", "x").VulkanDevice);
            Check("...while a plan decided now gets vk:0", UpscaleEngine.HwaccelArgs(new UpscaleEngine.Plan()).Contains("vulkan=vk:0"));

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
            UpscaleEngine.DrmVulkanClock = null;
            UpscaleEngine.DrmVulkanInline = true;
        }

        // PR 3: a machine with two GPUs gets the node of the vendor that was chosen. Auto is NVIDIA
        // first, and NVIDIA keeps vk:0 byte for byte (the NVIDIA rule); intel moves to the Intel node.
        private static void TwoGpuMachines()
        {
            const string Vk0 = " -init_hw_device vulkan=vk:0 -filter_hw_device vk";
            const string Qsv128 = " -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD128";
            const string Qsv129 = " -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD129";
            var asked = new List<string>();
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.DrmVulkanProbeSource = n =>
            {
                asked.Add(n);
                return true;
            };

            var intelFirst = Machine("renderD128", "0x8086", "renderD129", "0x10de");
            var nvidiaFirst = Machine("renderD128", "0x10de", "renderD129", "0x8086");

            GpuDetector.UseEnvironment(intelFirst);
            UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = "intel" };
            Equal("Intel at 128, NVIDIA at 129, intel chosen: the Intel node", " -init_hw_device drm=dr:/dev/dri/renderD128 -init_hw_device vulkan=vk@dr" + Qsv128 + " -filter_hw_device vk", UpscaleEngine.HwaccelArgs(null));
            UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = "auto" };
            asked.Clear();
            Equal("Intel at 128, NVIDIA at 129, auto: NVIDIA, vk:0 as before", Vk0, UpscaleEngine.HwaccelArgs(null));
            UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = "nvidia" };
            Equal("Intel at 128, NVIDIA at 129, nvidia chosen: vk:0 as before", Vk0, UpscaleEngine.HwaccelArgs(null));
            Check("...and the Intel render-node test was not run for NVIDIA", asked.Count == 0);

            GpuDetector.UseEnvironment(nvidiaFirst);
            UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = "intel" };
            Equal("NVIDIA at 128, Intel at 129, intel chosen: the Intel node, not the first", " -init_hw_device drm=dr:/dev/dri/renderD129 -init_hw_device vulkan=vk@dr" + Qsv129 + " -filter_hw_device vk", UpscaleEngine.HwaccelArgs(null));
            Check("...the test was asked about renderD129", asked.Count > 0 && asked.All(n => n == "/dev/dri/renderD129"));
            Equal("...its encoder is pinned to the same node", "/dev/dri/renderD129", UpscaleEngine.Devices().QsvText);
            Equal("...and the record says so", "render node /dev/dri/renderD129", UpscaleEngine.Describe(null, new UpscaleEngine.Plan { Act = true }, "applied", "x").VulkanDevice);
            UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = "auto" };
            Equal("NVIDIA at 128, Intel at 129, auto: vk:0 as before", Vk0, UpscaleEngine.HwaccelArgs(null));

            // Two Intel GPUs: the first Intel node, which is the encoder's own pick too.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086", "renderD129", "0x8086"));
            UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = "auto" };
            Equal("two Intel GPUs: the first Intel node, for Vulkan and QSV", " -init_hw_device drm=dr:/dev/dri/renderD128 -init_hw_device vulkan=vk@dr" + Qsv128 + " -filter_hw_device vk", UpscaleEngine.HwaccelArgs(null));

            // An AMD node beside the Intel one is skipped, and the QSV device is pinned to the Intel node, not to renderD128.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x1002", "renderD129", "0x8086"));
            UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = "intel" };
            Equal("AMD at 128, Intel at 129: the Intel node for Vulkan and QSV", " -init_hw_device drm=dr:/dev/dri/renderD129 -init_hw_device vulkan=vk@dr" + Qsv129 + " -filter_hw_device vk", UpscaleEngine.HwaccelArgs(null));
            Check("AMD at 128, Intel at 129: no QSV device on the AMD node", !UpscaleEngine.HwaccelArgs(null).Contains("child_device=/dev/dri/renderD128"));

            // Intel chosen with no Intel GPU on the machine: the NVIDIA path, vk:0.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = "intel" };
            Equal("Intel chosen, none present: vk:0", Vk0, UpscaleEngine.HwaccelArgs(null));

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
        }

        private static void SetFilters(params string[] names)
        {
            var type = typeof(UpscaleEngine);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            type.GetField("_filters", flags).SetValue(null, new HashSet<string>(names));
            type.GetField("_filtersProbed", flags).SetValue(null, true);
        }

        private static EncodingJobInfo SessionJob(params string[] options)
        {
            var streamOptions = new Dictionary<string, string>();
            for (int i = 0; i < options.Length; i += 2)
            {
                streamOptions[options[i]] = options[i + 1];
            }

            return new FakeJob
            {
                IsVideoRequest = true,
                VideoStream = new MediaBrowser.Model.Entities.MediaStream { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Width = 1280, Height = 720 },
                BaseRequest = new MediaBrowser.Controller.MediaEncoding.BaseEncodingJobOptions { StreamOptions = streamOptions },
            };
        }

        // PR 3: the levels that need NVIDIA are not offered on a detected Intel GPU, and are not run if asked for.
        private static void NvidiaOnlyLevels()
        {
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.DrmVulkanProbeSource = n => true;
            UpscaleEngine.Av1QsvProbeSource = _ => true;
            SetFilters("optix", "oidn", "ort", "fsr2", "dlss", "dlpp_rtcuda", "vsr_rtcuda", "libplacebo", "nlmeans_vulkan");

            // What the probe held before, for an NVIDIA box with every file in place.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            UpscaleEngine.Settings = RigSettings("auto");
            JsonElement nvidia = Probe();

            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.Settings = RigSettings("auto");
            JsonElement intel = Probe();

            Equal("Intel probe: Denoise", "off,light,strong,max,oidn", string.Join(",", Strings(intel, "Denoise")));
            Equal("Intel probe: Neural loses the ONNX level as well as DLPP and VSR", "off", string.Join(",", Strings(intel, "Neural")));
            Equal("Intel probe: Game is off alone", "off", string.Join(",", Strings(intel, "Game")));
            Equal("Intel probe: GameOptionLevels is empty", string.Empty, string.Join(",", Strings(intel, "GameOptionLevels")));
            Equal("Intel probe: the neural labels follow", "off", string.Join(",", Keys(intel.GetProperty("NeuralLabels")).OrderBy(n => n, StringComparer.Ordinal)));
            Equal("Intel probe: the game labels follow", "off", string.Join(",", Keys(intel.GetProperty("GameLabels"))));

            JsonElement withheld = intel.GetProperty("Withheld");
            Equal("Intel probe: Withheld, denoise", "optix,optix-temporal", string.Join(",", Keys(withheld.GetProperty("Denoise"))));
            Equal("Intel probe: Withheld, neural", "realesr-anime-x2,vsr-rtcuda,dlpp-1,dlpp-2,dlpp-3,dlpp-4", string.Join(",", Keys(withheld.GetProperty("Neural"))));
            Equal("Intel probe: Withheld, game", "fsr2,dlss,dlaa", string.Join(",", Keys(withheld.GetProperty("Game"))));
            Check("Intel probe: every withheld level but the ONNX one says needs NVIDIA",
                Keys(withheld).All(axis => withheld.GetProperty(axis).EnumerateObject().All(l => l.Name.StartsWith("realesr-") || l.Value.GetString() == "needs NVIDIA")));
            Equal("Intel probe: the ONNX level has its own reason", "not measured on Intel", withheld.GetProperty("Neural").GetProperty("realesr-anime-x2").GetString());
            Check("Intel probe: a level that is offered is not in Withheld", !withheld.GetProperty("Denoise").TryGetProperty("oidn", out _));

            // Nothing else in the probe moved: every other key is byte-identical to the NVIDIA one.
            string[] moved = { "Denoise", "Neural", "NeuralLabels", "Game", "GameLabels", "GameOptionLevels", "Gpu", "Withheld" };
            Check("Intel probe: no other key differs from the NVIDIA probe",
                Keys(nvidia).Where(k => !moved.Contains(k)).All(k => nvidia.GetProperty(k).GetRawText() == intel.GetProperty(k).GetRawText()));
            Equal("NVIDIA probe, same files: Denoise keeps OptiX", "off,light,strong,max,oidn,optix,optix-temporal", string.Join(",", Strings(nvidia, "Denoise")));
            Equal("NVIDIA probe, same files: Neural keeps DLPP and VSR", "off,realesr-anime-x2,vsr-rtcuda,dlpp-1,dlpp-2,dlpp-3,dlpp-4", string.Join(",", Strings(nvidia, "Neural")));
            Equal("NVIDIA probe, same files: Game keeps all three", "off,fsr2,dlss,dlaa", string.Join(",", Strings(nvidia, "Game")));

            // The status route carries the same account, on Intel only.
            using (JsonDocument status = JsonDocument.Parse(PatcherHost.StatusJson()))
            {
                Check("Intel status: Withheld names optix", status.RootElement.GetProperty("Withheld").GetProperty("Denoise").TryGetProperty("optix", out _));
            }

            // Switched off by the master switch, the lists are "off" and nothing is reported as withheld.
            UpscaleSettings gated = RigSettings("auto");
            gated.NeuralAllowed = false;
            gated.GameAllowed = false;
            UpscaleEngine.Settings = gated;
            Check("Intel, Neural and Game switched off: only the denoise levels are withheld", UpscaleEngine.WithheldLevels(gated).Keys.SequenceEqual(new[] { "Denoise" }));

            // A session asking for all three on Intel. The plan runs none of them and says so.
            UpscaleSettings rig = RigSettings("auto");
            UpscaleEngine.Settings = rig;
            EncodingJobInfo job = SessionJob("upscale", "1080", "denoise", "optix", "neural", "dlpp-2", "game", "dlss");
            UpscaleEngine.Plan plan = UpscaleEngine.Decide(job);
            Check("Intel session asking for optix, dlpp-2 and dlss: still enhanced (the upscale)", plan.Act, plan.Status + ": " + plan.Reason);
            Equal("Intel session: what was withheld", "denoise optix, neural dlpp-2, game dlss", plan.GpuWithheld);
            Check("Intel session: no denoise, neural or game node", !plan.DenoiseApplied && plan.DenoiseFilter == null && !plan.NeuralApplied && plan.NeuralFilter == null && !plan.GameApplied && plan.GameFilter == null);
            Check("Intel session: not the CUDA-native branch", !plan.UsesCudaNeural);
            Equal("Intel session: the neural level asked for is still recorded", "dlpp-2", plan.NeuralRequested);
            string chain = UpscaleEngine.BuildChain(plan);
            Check("Intel session: the chain has no optix, dlpp, vsr, fsr2 or dlss", !new[] { "optix", "dlpp", "vsr", "fsr2", "dlss" }.Any(chain.Contains), chain);
            Check("Intel session: no CUDA hwaccel", !UpscaleEngine.HwaccelArgs(plan).Contains("cuda"));
            SessionRecord record = UpscaleEngine.Describe(job, plan, plan.Status, plan.Reason);
            Equal("Intel record: GpuWithheld", "denoise optix, neural dlpp-2, game dlss", record.GpuWithheld);
            Check("Intel record: the summary says what did not run and why", record.Summary.Contains("denoise optix, neural dlpp-2, game dlss NOT run: needs an NVIDIA GPU and this server's is Intel"), record.Summary);
            Check("Intel record: the summary does not claim a denoise, neural or game pass", !record.Summary.Contains("Denoise ") && !record.Summary.Contains("Neural SR") && !record.Summary.Contains("Game upscaler"), record.Summary);
            Check("Intel record: NeuralRequested still says dlpp-2 and NeuralLevel says off", record.NeuralRequested == "dlpp-2" && record.NeuralLevel == "off");

            // The aliases and the unlisted level are withheld too, and a name that is not NVIDIA's is not.
            foreach (string level in new[] { "optix", "optix-temporal", "optix-hdr" })
            {
                Check(level + " is NVIDIA-only", ShaderLibrary.IsNvidiaOnlyDenoise(level));
            }

            foreach (string level in new[] { "off", "light", "strong", "max", "oidn", "atadenoise", "nlmeans", "nlmeans-strong", "banana", string.Empty })
            {
                Check("denoise '" + level + "' is not NVIDIA-only", !ShaderLibrary.IsNvidiaOnlyDenoise(level));
            }

            Check("denoise null is not NVIDIA-only", !ShaderLibrary.IsNvidiaOnlyDenoise(null));
            foreach (string level in new[] { "dlpp-1", "dlpp-2", "dlpp-3", "dlpp-4", "vsr-rtcuda", "vsr", "DLPP-3", " vsr-rtcuda " })
            {
                Check("neural " + level + " is NVIDIA-only", ShaderLibrary.IsNvidiaOnlyNeural(level));
            }

            foreach (string level in new[] { "off", "realesr-anime-x2", "realesr-anime-x4", "realesr-general-x4", "banana" })
            {
                Check("neural '" + level + "' is not NVIDIA-only", !ShaderLibrary.IsNvidiaOnlyNeural(level));
            }

            foreach (string level in new[] { "fsr2", "dlss", "dlaa" })
            {
                Check("game " + level + " is NVIDIA-only", ShaderLibrary.IsNvidiaOnlyGame(level));
            }

            Check("game off and banana are not NVIDIA-only", !ShaderLibrary.IsNvidiaOnlyGame("off") && !ShaderLibrary.IsNvidiaOnlyGame("banana") && !ShaderLibrary.IsNvidiaOnlyGame(null));

            // The dashboard default alone, no session option, is withheld the same way.
            UpscaleSettings defaults = RigSettings("auto");
            defaults.DenoiseLevel = "optix";
            defaults.GameLevel = "fsr2";
            UpscaleEngine.Settings = defaults;
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080"));
            Equal("Intel, dashboard defaults optix and fsr2: both withheld", "denoise optix, game fsr2", plan.GpuWithheld);

            // A session that asked for nothing but an NVIDIA level is left with nothing, and says why.
            UpscaleSettings optIn = RigSettings("auto");
            optIn.RequireClientOptIn = true;
            UpscaleEngine.Settings = optIn;
            plan = UpscaleEngine.Decide(SessionJob("denoise", "optix"));
            Check("Intel, only optix asked for: not enhanced", !plan.Act && plan.Status == "not-requested", plan.Status);
            Check("Intel, only optix asked for: the reason says why", plan.Reason.Contains("denoise optix not run: needs an NVIDIA GPU and this server's is Intel"), plan.Reason);

            // The control: the same sessions on an NVIDIA box are not withheld, and run.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            UpscaleEngine.Settings = rig;
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "denoise", "optix"));
            Check("NVIDIA: nothing withheld", plan.GpuWithheld == null, plan.GpuWithheld);
            Check("NVIDIA: optix runs", plan.Act && plan.DenoiseApplied && plan.DenoiseFilter.Contains("optix"), plan.Status + ": " + plan.Reason);
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "neural", "dlpp-2"));
            Check("NVIDIA: dlpp-2 runs CUDA-native", plan.Act && plan.UsesCudaNeural && plan.GpuWithheld == null, plan.Status + ": " + plan.Reason);
            Check("NVIDIA: the CUDA hwaccel is emitted for it", UpscaleEngine.HwaccelArgs(plan).EndsWith("-hwaccel cuda -hwaccel_output_format cuda"));
            Check("NVIDIA record: no withheld text", UpscaleEngine.Describe(null, plan, plan.Status, plan.Reason).GpuWithheld == null);

            // Intel chosen on a box that has both: the NVIDIA-only levels are withheld there too.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de", "renderD129", "0x8086"));
            UpscaleEngine.Settings = RigSettings("intel");
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "denoise", "optix"));
            Equal("mixed box, intel chosen: optix withheld", "denoise optix", plan.GpuWithheld);
            UpscaleEngine.Settings = RigSettings("auto");
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "denoise", "optix"));
            Check("mixed box, auto (NVIDIA): optix is not withheld", plan.GpuWithheld == null, plan.GpuWithheld);

            // The ONNX levels are withheld on Intel with their own reason, and run unchanged on NVIDIA.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.Settings = rig;
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "neural", "realesr-anime-x2"));
            record = UpscaleEngine.Describe(null, plan, plan.Status, plan.Reason);
            Check("Intel: an ONNX neural level is withheld, not run", plan.GpuWithheldUnmeasured == "neural realesr-anime-x2" && !plan.NeuralApplied && plan.NeuralFilter == null, plan.Status + ": " + plan.Reason);
            Check("Intel: it is not counted as needing NVIDIA", plan.GpuWithheld == null, plan.GpuWithheld);
            Equal("Intel: the neural level asked for is still recorded", "realesr-anime-x2", plan.NeuralRequested);
            Equal("Intel record: GpuWithheldUnmeasured", "neural realesr-anime-x2", record.GpuWithheldUnmeasured);
            Check("Intel record: GpuWithheld stays empty for it", record.GpuWithheld == null);
            Check("Intel record: the summary gives the unmeasured reason, not the NVIDIA one",
                record.Summary.Contains("neural realesr-anime-x2 NOT run: not measured on Intel") && !record.Summary.Contains("needs an NVIDIA GPU"), record.Summary);
            Check("Intel record: the summary claims no neural pass", !record.Summary.Contains("Neural SR") && !record.Summary.Contains("ONNX"), record.Summary);

            // Both reasons in one session: each is said under its own wording.
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "denoise", "optix", "neural", "realesr-anime-x2"));
            record = UpscaleEngine.Describe(null, plan, plan.Status, plan.Reason);
            Check("Intel: a session with both reasons keeps them apart", plan.GpuWithheld == "denoise optix" && plan.GpuWithheldUnmeasured == "neural realesr-anime-x2", plan.GpuWithheld + " | " + plan.GpuWithheldUnmeasured);
            Check("Intel record: both sentences are in the summary",
                record.Summary.Contains("denoise optix NOT run: needs an NVIDIA GPU and this server's is Intel") && record.Summary.Contains("neural realesr-anime-x2 NOT run: not measured on Intel"), record.Summary);

            // Asked for nothing but an ONNX level: left with nothing, and the reason says why.
            UpscaleEngine.Settings = optIn;
            plan = UpscaleEngine.Decide(SessionJob("neural", "realesr-anime-x2"));
            Check("Intel, only an ONNX level asked for: not enhanced", !plan.Act && plan.Status == "not-requested", plan.Status);
            Check("Intel, only an ONNX level asked for: the reason says why", plan.Reason.Contains("neural realesr-anime-x2 not run: not measured on Intel"), plan.Reason);

            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            UpscaleEngine.Settings = rig;
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "neural", "realesr-anime-x2"));
            record = UpscaleEngine.Describe(null, plan, plan.Status, plan.Reason);
            Check("NVIDIA: the same level runs, nothing withheld", plan.NeuralApplied && plan.GpuWithheld == null && plan.GpuWithheldUnmeasured == null, plan.Status + ": " + plan.Reason);
            Check("NVIDIA: it is still described as ONNX Runtime CUDA", record.Summary.Contains("ONNX Runtime CUDA"), record.Summary);
            Check("NVIDIA record: no unmeasured text", record.GpuWithheldUnmeasured == null);

            // ShaderLibrary names the ONNX levels, and leaves the NVIDIA-only and unknown ones out.
            foreach (string level in new[] { "realesr-anime-x2", "realesr-anime-x4", "realesr-general-x4", "REALESR-ANIME-X2" })
            {
                Check("neural " + level + " is an ONNX level", ShaderLibrary.IsOnnxNeural(level));
            }

            Check("off, dlpp-2, vsr-rtcuda, banana and null are not ONNX levels",
                !ShaderLibrary.IsOnnxNeural("off") && !ShaderLibrary.IsOnnxNeural("dlpp-2") && !ShaderLibrary.IsOnnxNeural("vsr-rtcuda") && !ShaderLibrary.IsOnnxNeural("banana") && !ShaderLibrary.IsOnnxNeural(null));
            Equal("reason: an ONNX level", "not measured on Intel", UpscaleEngine.IntelWithholdReason("Neural", "realesr-general-x4"));
            Equal("reason: a CUDA-native level", "needs NVIDIA", UpscaleEngine.IntelWithholdReason("Neural", "dlpp-1"));
            Check("reason: off and banana have none", UpscaleEngine.IntelWithholdReason("Neural", "off") == null && UpscaleEngine.IntelWithholdReason("Neural", "banana") == null);

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
            UpscaleEngine.Settings = new UpscaleSettings();
        }

        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

        // PR 2: DRM fdinfo, from fixture files in the two drivers' formats. The fixtures are written
        // from the kernel's drm-usage-stats documentation and the drivers' print code, not captured
        // from a machine, so they prove the parser against the documented format only.
        private static void Fdinfo()
        {
            DrmClient i915 = DrmFdinfo.Parse(Fixture("fdinfo-i915-encode-t0.txt"));
            Check("i915 fdinfo parses", i915 != null && i915.Driver == "i915" && i915.ClientId == "82" && i915.Pdev == "0000:03:00.0");
            Check("i915: busy time in ns per canonical class", i915.Busy["video"] == 2000000000L && i915.Busy["render"] == 1396112L && i915.Busy["copy"] == 0L && i915.Busy["video-enhance"] == 0L);
            Check("i915: engine capacity read, and not mistaken for busy time", i915.Capacity["video"] == 2 && i915.Busy.Count == 4);
            Check("i915: not in cycles", !i915.InCycles && i915.TotalCycles.Count == 0);
            Equal("i915: key is device plus client id", "0000:03:00.0/82", i915.Key);

            DrmClient xe = DrmFdinfo.Parse(Fixture("fdinfo-xe-t0.txt"));
            Check("xe fdinfo parses", xe != null && xe.Driver == "xe" && xe.ClientId == "14");
            Check("xe: busy cycles map rcs/bcs/vcs/vecs/ccs onto the canonical classes",
                xe.Busy["render"] == 100000L && xe.Busy["copy"] == 0L && xe.Busy["video"] == 40000000L && xe.Busy["video-enhance"] == 0L && xe.Busy["compute"] == 0L);
            Check("xe: total cycles read per class", xe.TotalCycles["video"] == 1000000000L && xe.TotalCycles.Count == 5);
            Check("xe: in cycles, memory lines ignored", xe.InCycles && xe.Busy.Count == 5);

            Check("a file descriptor that is not a DRM client is null", DrmFdinfo.Parse(Fixture("fdinfo-not-drm.txt")) == null);
            Check("empty and null text are null", DrmFdinfo.Parse(string.Empty) == null && DrmFdinfo.Parse(null) == null);
            Check("a driver with no client id is not a client", DrmFdinfo.Parse("drm-driver:\ti915\ndrm-engine-video:\t5 ns\n") == null);
            Check("an unknown engine class is skipped, not an error",
                DrmFdinfo.Parse("drm-driver:\ti915\ndrm-client-id:\t3\ndrm-engine-gsc:\t77 ns\ndrm-engine-video:\t5 ns\n").Busy.Count == 1);
            Check("a malformed number is skipped",
                DrmFdinfo.Parse("drm-driver:\ti915\ndrm-client-id:\t3\ndrm-engine-video:\tlots ns\n").Busy.Count == 0);

            // A /proc tree: pid 4242 holds the encode client twice (a dup of the same fd), an
            // ordinary fd and an unrelated node; pid 4243 is another client on another node; pid
            // 4244 is another process on the first node whose client is the same one.
            string root = Path.Combine(Path.GetTempPath(), "gpu-fdinfo-fake-" + Guid.NewGuid().ToString("N"));
            try
            {
                FakeFd(root, 4242, 0, "/dev/null", "fdinfo-not-drm.txt");
                FakeFd(root, 4242, 7, "/dev/dri/renderD128", "fdinfo-i915-encode-t0.txt");
                FakeFd(root, 4242, 9, "/dev/dri/renderD128", "fdinfo-i915-encode-t0.txt");
                FakeFd(root, 4243, 5, "/dev/dri/renderD129", "fdinfo-i915-render-t0.txt");
                FakeFd(root, 4244, 3, "/dev/dri/renderD128", "fdinfo-i915-encode-t0.txt");
                Directory.CreateDirectory(Path.Combine(root, "self"));

                var one = DrmFdinfo.Scan(root, "/dev/dri/renderD128");
                Check("scan: one client on the node, a dup fd and a second process deduplicated", one != null && one.Count == 1 && one.ContainsKey("0000:03:00.0/82"), one == null ? "null" : string.Join(",", one.Keys));
                var every = DrmFdinfo.Scan(root, null);
                Check("scan with no node: every render node's clients", every != null && every.Count == 2, every == null ? "null" : string.Join(",", every.Keys));
                var other = DrmFdinfo.Scan(root, "/dev/dri/renderD129");
                Check("scan: only the asked node", other != null && other.Count == 1 && other.ContainsKey("0000:03:00.0/91"));
                Check("scan: a missing /proc is null, not an error", DrmFdinfo.Scan(Path.Combine(root, "nope"), null) == null);
                Check("scan: nothing on a node nobody opened is an empty set", DrmFdinfo.Scan(root, "/dev/dri/renderD130").Count == 0);
            }
            finally
            {
                TryDelete(root);
            }

            // Two samples of the same client, one second apart.
            DrmLoadResult load = DrmFdinfo.Compute(
                Samples("fdinfo-i915-encode-t0.txt", "fdinfo-i915-render-t0.txt"),
                Samples("fdinfo-i915-encode-t1.txt", "fdinfo-i915-render-t1.txt"),
                1.0);
            Check("i915 load: video 1.5 s over 1 s across 2 engines is 75%", load.BusyByClass["video"] == 75.0, string.Join(",", load.BusyByClass));
            Check("i915 load: render 0.3 s over 1 s is 30%", load.BusyByClass["render"] == 30.0);
            Check("i915 load: the busiest class is the headline", load.BusyPercent == 75.0);
            Check("i915 load: one client moved the video engine", load.VideoClients == 1);
            Check("i915 load: both clients were measured", load.ClientsMeasured == 2);

            // A render-only client is not an encoder session.
            DrmLoadResult renderOnly = DrmFdinfo.Compute(Samples("fdinfo-i915-render-t0.txt"), Samples("fdinfo-i915-render-t1.txt"), 1.0);
            Check("i915 load: a client that never touches the video engine is not an encoder session", renderOnly.VideoClients == 0 && renderOnly.BusyPercent == 30.0);
            DrmLoadResult idle = DrmFdinfo.Compute(Samples("fdinfo-i915-encode-t1.txt"), Samples("fdinfo-i915-encode-t1.txt"), 1.0);
            Check("i915 load: no change is 0% and no sessions, not null", idle.BusyPercent == 0.0 && idle.VideoClients == 0);

            DrmLoadResult xeLoad = DrmFdinfo.Compute(Samples("fdinfo-xe-t0.txt"), Samples("fdinfo-xe-t1.txt"), 1.0);
            Check("xe load: 9.6M of 19.2M cycles on vcs is 50%", xeLoad.BusyByClass["video"] == 50.0, string.Join(",", xeLoad.BusyByClass));
            Check("xe load: the headline and the encoder session", xeLoad.BusyPercent == 50.0 && xeLoad.VideoClients == 1);

            // Honest negatives.
            Check("a client in only one sample is not measured",
                DrmFdinfo.Compute(Samples("fdinfo-i915-render-t0.txt"), Samples("fdinfo-i915-encode-t1.txt"), 1.0).ClientsMeasured == 0);
            Check("no clients measured: no busy figure, not 0",
                DrmFdinfo.Compute(Samples("fdinfo-i915-render-t0.txt"), Samples("fdinfo-i915-encode-t1.txt"), 1.0).BusyPercent == null);
            Check("a zero or negative window measures nothing", DrmFdinfo.Compute(Samples("fdinfo-xe-t0.txt"), Samples("fdinfo-xe-t1.txt"), 0).BusyPercent == null);
            Check("an xe class with no elapsed cycles is left out, not read as 0",
                DrmFdinfo.Compute(Samples("fdinfo-xe-t1.txt"), Samples("fdinfo-xe-t1.txt"), 1.0).BusyPercent == null);
            Check("a counter that went backwards is skipped", DrmFdinfo.Compute(Samples("fdinfo-i915-encode-t1.txt"), Samples("fdinfo-i915-encode-t0.txt"), 1.0).VideoClients == 0);
            Check("a null sample measures nothing", DrmFdinfo.Compute(null, Samples("fdinfo-xe-t1.txt"), 1.0).BusyPercent == null);
            DrmClient hot = DrmFdinfo.Parse("drm-driver:\ti915\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t1\ndrm-engine-video:\t9000000000 ns\n");
            DrmClient hotBefore = DrmFdinfo.Parse("drm-driver:\ti915\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t1\ndrm-engine-video:\t0 ns\n");
            Check("busy is clamped to 100",
                DrmFdinfo.Compute(new Dictionary<string, DrmClient> { [hotBefore.Key] = hotBefore }, new Dictionary<string, DrmClient> { [hot.Key] = hot }, 1.0).BusyPercent == 100.0);
        }

        private static Dictionary<string, DrmClient> Samples(params string[] fixtures)
        {
            var map = new Dictionary<string, DrmClient>();
            foreach (string f in fixtures)
            {
                DrmClient c = DrmFdinfo.Parse(Fixture(f));
                map[c.Key] = c;
            }

            return map;
        }

        private static void FakeFd(string root, int pid, int fd, string target, string fixture)
        {
            string fdDir = Path.Combine(root, pid.ToString(), "fd");
            string infoDir = Path.Combine(root, pid.ToString(), "fdinfo");
            Directory.CreateDirectory(fdDir);
            Directory.CreateDirectory(infoDir);
            File.CreateSymbolicLink(Path.Combine(fdDir, fd.ToString()), target);
            File.WriteAllText(Path.Combine(infoDir, fd.ToString()), Fixture(fixture));
        }

        private static void TryDelete(string root)
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception)
            {
                // the temp dir is the OS's to clean
            }
        }

        // PR 2: idle residency from sysfs, for the i915 and xe layouts.
        private static void IdleResidency()
        {
            string root = Path.Combine(Path.GetTempPath(), "gpu-idle-fake-" + Guid.NewGuid().ToString("N"));
            try
            {
                // i915: the render node's device has a drm/card1/gt/gt0 with rc6_residency_ms.
                Counter(root, "renderD128/device/drm/card1/gt/gt0/rc6_residency_ms", 5000);
                // xe with two GTs (the media GT is the busier one): tile0/gt0 and tile0/gt1.
                Counter(root, "renderD129/device/tile0/gt0/gtidle/idle_residency_ms", 9000);
                Counter(root, "renderD129/device/tile0/gt1/gtidle/idle_residency_ms", 3000);
                // an old kernel: one counter for the device.
                Counter(root, "renderD130/device/drm/card2/power/rc6_residency_ms", 100);
                Directory.CreateDirectory(Path.Combine(root, "renderD131", "device"));

                var i915a = GpuIdleResidency.Read(root, "renderD128");
                Check("i915: the GT counter is read", i915a.Count == 1 && i915a.Values.First() == 5000L);
                Counter(root, "renderD128/device/drm/card1/gt/gt0/rc6_residency_ms", 5800);
                var i915b = GpuIdleResidency.Read(root, "renderD128");
                Check("i915: 800 ms asleep in 1 s is 80% idle", GpuIdleResidency.IdlePercent(i915a, i915b, 1.0) == 80.0);

                var xeA = GpuIdleResidency.Read(root, "renderD129");
                Check("xe: both GTs are read", xeA.Count == 2);
                Counter(root, "renderD129/device/tile0/gt0/gtidle/idle_residency_ms", 9950);
                Counter(root, "renderD129/device/tile0/gt1/gtidle/idle_residency_ms", 3400);
                var xeB = GpuIdleResidency.Read(root, "renderD129");
                Check("xe: the least-idle GT is reported (400 ms of 1000)", GpuIdleResidency.IdlePercent(xeA, xeB, 1.0) == 40.0);

                Check("an older kernel's device-wide counter is read", GpuIdleResidency.Read(root, "renderD130").Count == 1);
                Check("a node with no counter is empty, not an error", GpuIdleResidency.Read(root, "renderD131").Count == 0);
                Check("a node that does not exist is empty", GpuIdleResidency.Read(root, "renderD999").Count == 0 && GpuIdleResidency.Read(root, null).Count == 0);
                Check("no counters give no figure", GpuIdleResidency.IdlePercent(new Dictionary<string, long>(), new Dictionary<string, long>(), 1.0) == null);
                Check("idle is clamped to 100", GpuIdleResidency.IdlePercent(i915a, new Dictionary<string, long> { [i915a.Keys.First()] = 99999 }, 1.0) == 100.0);
                Check("a counter that went backwards is skipped", GpuIdleResidency.IdlePercent(i915b, i915a, 1.0) == null);
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void Counter(string root, string relative, long ms)
        {
            string file = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, ms.ToString() + "\n");
        }

        // PR 2: GET /JellyfinPlus/Gpu/Load through the real controller. The patcher is "loaded" by
        // pointing the loader at the PatcherHost type this harness already holds, which is all the
        // loader does with it.
        private static void LoadRoute()
        {
            var hostField = typeof(Jellyfin.Plugin.Plus.PatcherLoader).GetField("_host", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var controller = new Jellyfin.Plugin.Plus.GpuLoadController(null);
            UpscaleEngine.Settings = new UpscaleSettings { GpuVendor = "auto" };

            // Patcher not loaded: the vendor is unknown, and the route answers as it always did.
            hostField.SetValue(null, null);
            JsonElement none = LoadJson(controller);
            foreach (string key in new[] { "activeTranscodes", "activeVideoTranscodes", "enhancedSessions", "nvencSessions", "vendor", "encoderSessions", "gpuBusyPercent", "gpuIdlePercent", "loadSource" })
            {
                Check("load route, patcher not loaded: carries " + key, none.TryGetProperty(key, out _));
            }

            Check("load route, patcher not loaded: the old fields keep their meaning", none.GetProperty("activeTranscodes").GetInt32() == 0 && none.GetProperty("enhancedSessions").ValueKind == JsonValueKind.Null);
            Check("load route, patcher not loaded: busy and idle are null, not 0", none.GetProperty("gpuBusyPercent").ValueKind == JsonValueKind.Null && none.GetProperty("gpuIdlePercent").ValueKind == JsonValueKind.Null);
            Check("load route, patcher not loaded: encoderSessions mirrors nvencSessions",
                none.GetProperty("encoderSessions").ToString() == none.GetProperty("nvencSessions").ToString());

            // An NVIDIA server: nvidia-smi is what is asked, and the new fields are only additive.
            hostField.SetValue(null, typeof(PatcherHost));
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            JsonElement nv = LoadJson(controller);
            Equal("load route, NVIDIA: vendor", "nvidia", nv.GetProperty("vendor").GetString());
            Check("load route, NVIDIA: encoderSessions mirrors nvencSessions", nv.GetProperty("encoderSessions").ToString() == nv.GetProperty("nvencSessions").ToString());
            Check("load route, NVIDIA: no Intel figures", nv.GetProperty("gpuBusyPercent").ValueKind == JsonValueKind.Null && nv.GetProperty("gpuIdlePercent").ValueKind == JsonValueKind.Null);

            // An Intel server: no nvidia-smi, nvencSessions null, the load from the kernel.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            JsonElement intel = LoadJson(controller);
            Equal("load route, Intel: vendor", "intel", intel.GetProperty("vendor").GetString());
            Check("load route, Intel: nvencSessions is null", intel.GetProperty("nvencSessions").ValueKind == JsonValueKind.Null);
            if (Directory.Exists("/proc/self/fd"))
            {
                Check("load route, Intel: with no process on the GPU, zero encoder sessions (not null)", intel.GetProperty("encoderSessions").GetInt32() == 0, intel.ToString());
                Check("load route, Intel: the source is named", intel.GetProperty("loadSource").GetString().StartsWith("drm-fdinfo"), intel.ToString());
            }

            // A mixed box on auto is NVIDIA, as everywhere else.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086", "renderD129", "0x10de"));
            Equal("load route, mixed box on auto: nvidia", "nvidia", LoadJson(controller).GetProperty("vendor").GetString());

            hostField.SetValue(null, null);
            GpuDetector.UseEnvironment(null);
        }

        private static JsonElement LoadJson(Jellyfin.Plugin.Plus.GpuLoadController controller)
        {
            // The controller caches for 5 s; the tests ask about different machines back to back.
            foreach (string name in new[] { "_nvencAtUtc", "_intelAtUtc" })
            {
                typeof(Jellyfin.Plugin.Plus.GpuLoadController).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, DateTime.MinValue);
            }

            var result = controller.GetLoad().GetAwaiter().GetResult();
            return JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement.Clone();
        }

        private static void CapabilityJson()
        {
            // The setting reaches the record and the probe through the real host entry points.
            GpuDetector.Reset();
            PatcherHost.Configure("{\"GpuVendor\":\"none\"}");
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(PatcherHost.SessionJson("harness-no-such-session")))
                {
                    JsonElement gpu = doc.RootElement.GetProperty("Levels").GetProperty("Gpu");
                    Equal("probe: Levels.Gpu.Setting follows the setting", "none", gpu.GetProperty("Setting").GetString());
                    Equal("probe: Levels.Gpu.Vendor", "none", gpu.GetProperty("Vendor").GetString());
                    Check("probe: Levels.Gpu has every capability field",
                        new[] { "Vendor", "Setting", "Detected", "RenderNode", "EncoderFamily", "DecodeType", "CudaOnlyLevelsAvailable", "OpenVinoAvailable", "Reason", "Devices" }
                            .All(f => gpu.TryGetProperty(f, out _)));
                }

                using (JsonDocument doc = JsonDocument.Parse(PatcherHost.StatusJson()))
                {
                    Check("status: carries Gpu", doc.RootElement.TryGetProperty("Gpu", out _));
                }
            }
            catch (Exception ex)
            {
                Check("probe and status carry the capability record", false, ex.GetType().Name + ": " + ex.Message);
            }

            using (JsonDocument doc = JsonDocument.Parse(PatcherHost.GpuJson()))
            {
                Check("GpuJson carries the vendor, whether it was seen, and the render node",
                    doc.RootElement.TryGetProperty("Vendor", out _) && doc.RootElement.TryGetProperty("Detected", out _) && doc.RootElement.TryGetProperty("RenderNode", out _));
            }

            string json = JsonSerializer.Serialize(new SessionRecord { Gpu = new GpuCapability { Vendor = "nvidia" } });
            Check("SessionRecord serialises its Gpu field", json.Contains("\"Gpu\":{") && json.Contains("\"Vendor\":\"nvidia\""), json);
        }
    }
}
