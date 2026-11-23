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
    /// <summary>An in-memory machine for the detection decision: nodes and whether the container holds an NVIDIA device.</summary>
    internal sealed class FakeGpuEnvironment : IGpuEnvironment
    {
        public List<KeyValuePair<string, string>> Nodes = new List<KeyValuePair<string, string>>();

        public bool NvidiaDevice;

        public HashSet<string> Files = new HashSet<string>();

        public IReadOnlyList<string> RenderNodes() => Nodes.Select(n => n.Key).ToList();

        public string ReadVendorId(string node) => Nodes.First(n => n.Key == node).Value;

        public string DevicePath(string node) => "/dev/dri/" + node;

        public bool NvidiaDevicePresent() => NvidiaDevice;

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
            NoCudaGame();
            NeuralDevice();
            NeuralRouting();
            AutoNeuralCache();
            IntelHardwareDecode();
            AccessHardwareDecode();
            Fdinfo();
            IdleResidency();
            LoadRoute();
            CapabilityJson();
            VaapiStrip();
            StripPrefix();
            NullEncoderBranches();
            IntelProbes();
            Access();
            CachedProbes();

            // Last: it runs the real PatcherHost.Apply, which installs the Harmony patches for the rest of the process.
            PatchWiring();

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

            // NVIDIA without a render node (no nvidia_drm): an NVIDIA device in the container decides.
            // /proc/driver/nvidia and nvidia-smi are the host's and no longer count (rev-ml3y9), so an
            // Intel-only pod on a mixed host is Intel: the fake carries no such signal at all.
            var intelOnly = GpuDetector.Resolve("auto", Models, Machine("renderD128", "0x8086"));
            Equal("Intel-only pod on a host with the NVIDIA driver: intel", "intel", intelOnly.Vendor);
            Equal("Intel-only pod on a host with the NVIDIA driver: renderD128", "/dev/dri/renderD128", intelOnly.RenderNode);
            Check("Intel-only pod on a host with the NVIDIA driver: detected, no CUDA-only levels", intelOnly.Detected && !intelOnly.CudaOnlyLevelsAvailable);
            Check("Intel-only pod: the reason does not claim NVIDIA", !intelOnly.Reason.Contains("NVIDIA GPU found"));

            var deviceOnly = Machine();
            deviceOnly.NvidiaDevice = true;
            var viaDevice = GpuDetector.Resolve("auto", Models, deviceOnly);
            Equal("NVIDIA device, no node: nvidia", "nvidia", viaDevice.Vendor);
            Check("NVIDIA device, no node: detected, no render node reported", viaDevice.Detected && viaDevice.RenderNode == null);
            Check("NVIDIA device, no node: CUDA-only levels", viaDevice.CudaOnlyLevelsAvailable);
            Check("NVIDIA device, no node: the reason names the device", viaDevice.Reason.Contains("/dev/nvidia"));

            var deviceAndIntel = Machine("renderD128", "0x8086");
            deviceAndIntel.NvidiaDevice = true;
            var deviceBoth = GpuDetector.Resolve("auto", Models, deviceAndIntel);
            Equal("NVIDIA device plus an Intel node: NVIDIA first", "nvidia", deviceBoth.Vendor);
            Check("NVIDIA device plus an Intel node: no render node reported", deviceBoth.RenderNode == null);
            Check("NVIDIA device plus an Intel node: the reason names the unused Intel GPU", deviceBoth.Reason.Contains("Intel"));
            Equal("NVIDIA device plus an Intel node: intel setting still wins", "intel", GpuDetector.Resolve("intel", Models, deviceAndIntel).Vendor);

            var deviceAndNode = Machine("renderD128", "0x8086", "renderD129", "0x10de");
            deviceAndNode.NvidiaDevice = true;
            var deviceNodeCap = GpuDetector.Resolve("auto", Models, deviceAndNode);
            Equal("NVIDIA device plus an NVIDIA node: the node is reported", "/dev/dri/renderD129", deviceNodeCap.RenderNode);

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

                string devRoot = Path.Combine(root, "dev");
                var env = new SystemGpuEnvironment(drm, dev, devRoot);
                var nodes = env.RenderNodes();
                Check("sysfs reader lists nodes that have a /dev entry, in numeric order, and not card0",
                    nodes.Count == 3 && nodes[0] == "renderD128" && nodes[1] == "renderD129" && nodes[2] == "renderD1000", string.Join(",", nodes));
                Equal("sysfs reader trims the vendor id", "0x8086", env.ReadVendorId("renderD128"));
                Check("sysfs reader: unreadable vendor is null", env.ReadVendorId("renderD999") == null);

                // The host's procfs is visible in every pod; only a device node in the container counts.
                Directory.CreateDirectory(Path.Combine(root, "proc", "driver", "nvidia"));
                File.WriteAllText(Path.Combine(devRoot, "nvidia-uvm"), string.Empty);
                File.WriteAllText(Path.Combine(devRoot, "nvidia-caps"), string.Empty);
                Check("device reader: /proc/driver/nvidia and /dev/nvidia-uvm alone are not an NVIDIA device", !env.NvidiaDevicePresent());
                File.WriteAllText(Path.Combine(devRoot, "nvidia0"), string.Empty);
                Check("device reader: /dev/nvidia0 counts", env.NvidiaDevicePresent());
                File.Delete(Path.Combine(devRoot, "nvidia0"));
                File.WriteAllText(Path.Combine(devRoot, "nvidiactl"), string.Empty);
                Check("device reader: /dev/nvidiactl counts", env.NvidiaDevicePresent());
                File.Delete(Path.Combine(devRoot, "nvidiactl"));
                Check("device reader: back to none", !env.NvidiaDevicePresent());

                var capNoDevice = GpuDetector.Resolve("auto", Models, env);
                Equal("fake sysfs tree, no NVIDIA device: its NVIDIA render node still counts", "nvidia", capNoDevice.Vendor);
                Equal("fake sysfs tree, auto: its node", Path.Combine(dev, "renderD129"), capNoDevice.RenderNode);

                var missing = new SystemGpuEnvironment(Path.Combine(root, "nope"), dev, Path.Combine(root, "nope2"));
                Check("no device root at all: no NVIDIA device, not an error", !missing.NvidiaDevicePresent());
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
            Check("no QSV and no software encoder: no encoder, Jellyfin's choice stands", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason) == null);
            Check("...and the reason says what was kept", reason.Contains("ffmpeg lists neither hevc_qsv nor libx265") && reason.Contains("kept Jellyfin's encoder"), reason);
            Check("no encoder, client named no codec: still null", UpscaleEngine.EncoderFor(null, out reason) == null);
            OnIntel("auto", "hevc_qsv", "hevc_vaapi");
            Check("a named QSV encoder ffmpeg lacks, nothing usable: null, not the unlisted name", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason) == null);
            OnIntel("auto", "hevc_vaapi", "hevc_vaapi");
            Check("a named VA-API encoder with nothing usable: null, not the VA-API name", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason) == null);

            // The encoder postfix keeps Jellyfin's own choice when the engine has none.
            OnIntel("auto", "auto", "hevc_vaapi");
            string kept = "hevc_vaapi";
            bool keptHandled = UpscalePatches.ApplyIntelEncoder(Job("hevc", "hevc"), UpscaleEngine.Settings, ref kept);
            Check("ApplyIntelEncoder with no usable encoder keeps Jellyfin's", keptHandled && kept == "hevc_vaapi", kept);
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
            Check("hevc_qsv named but not in ffmpeg, no software hevc either: null", UpscaleEngine.EncoderFor(Job("hevc", "hevc"), out reason) == null);
            Equal("hevc_qsv named but not in ffmpeg: the client's h264 encoder", "h264_qsv", UpscaleEngine.EncoderFor(Job("h264", "h264"), out reason));

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

            // The withhold is the maintainer's condition: it stays until the OpenVINO path is MEASURED on an Arc, even though
            // PR 5's path is present (provider file there, ffmpeg lists the device). Lifting it (a bench result) is one flag.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.Settings = rig;
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "neural", "realesr-anime-x2"));
            Check("Intel, not measured: the flag is off by default and the ONNX level is withheld", !UpscaleEngine.IntelOnnxMeasured && plan.GpuWithheldUnmeasured == "neural realesr-anime-x2" && !plan.NeuralApplied);
            UpscaleEngine.IntelOnnxMeasured = true;
            plan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "neural", "realesr-anime-x2"));
            record = UpscaleEngine.Describe(null, plan, plan.Status, plan.Reason);
            Check("Intel, measured (flag on): an ONNX neural level is not withheld", plan.GpuWithheld == null && plan.GpuWithheldUnmeasured == null && plan.NeuralApplied, plan.Status + ": " + plan.Reason);
            Check("Intel, measured (flag on): and its description does not claim CUDA", record.Summary.Contains("this server's GPU is Intel, so no CUDA and the CPU provider") && !record.Summary.Contains("ONNX Runtime CUDA"), record.Summary);
            UpscaleEngine.IntelOnnxMeasured = false;

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


        // ---- PR 5: the ort device, and NeuralMaxSourceHeight --------------------------------------

        // A session whose source is any size, optionally stored sideways.
        private static EncodingJobInfo SessionJobAt(int width, int height, int? rotation, params string[] options)
        {
            var streamOptions = new Dictionary<string, string>();
            for (int i = 0; i < options.Length; i += 2)
            {
                streamOptions[options[i]] = options[i + 1];
            }

            return new FakeJob
            {
                IsVideoRequest = true,
                VideoStream = new MediaBrowser.Model.Entities.MediaStream
                {
                    Type = MediaBrowser.Model.Entities.MediaStreamType.Video,
                    Width = width,
                    Height = height,
                    Rotation = rotation,
                },
                BaseRequest = new MediaBrowser.Controller.MediaEncoding.BaseEncodingJobOptions { StreamOptions = streamOptions },
            };
        }

        private static int _fsr2ProbeCalls;

        // Replaces the fsr2 start-up probe with one that counts its calls and answers with `answer`. Its scheduler
        // runs the work at once, so a Start() lands the answer before the next read; `schedule` overrides that.
        private static void UseFsr2Probe(Func<UpscaleEngine.Fsr2ProbeResult> answer, Func<DateTime> now = null, Action<Action> schedule = null)
        {
            _fsr2ProbeCalls = 0;
            UpscaleEngine.Fsr2Probe = UpscaleEngine.NewFsr2Probe(() => { _fsr2ProbeCalls++; return answer(); }, now, schedule ?? (work => work()));
        }

        private static UpscaleEngine.Fsr2ProbeResult Fsr2Passed() => new UpscaleEngine.Fsr2ProbeResult { Ok = true };

        private static UpscaleEngine.Fsr2ProbeResult Fsr2Failed() => new UpscaleEngine.Fsr2ProbeResult { Ok = false };

        // PR 6: fsr2 is offered and run on a detected Intel GPU only when the build left the fsr2-nocuda marker AND a
        // one-frame fsr2 run on this GPU passed; dlss and dlaa never.
        private static void NoCudaGame()
        {
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.DrmVulkanProbeSource = n => true;
            UpscaleEngine.Av1QsvProbeSource = _ => true;
            SetFilters("optix", "oidn", "ort", "fsr2", "dlss", "dlpp_rtcuda", "vsr_rtcuda", "libplacebo", "nlmeans_vulkan");
            string marker = Path.Combine(Rig(), "fsr2-nocuda");

            // The capability: the marker counts on an Intel GPU that was seen, and on nothing else.
            foreach (string vendor in new[] { "auto", "nvidia", "none", "intel" })
            {
                FakeGpuEnvironment nvMarked = Machine("renderD128", "0x10de");
                nvMarked.Files.Add(marker);
                Check("NVIDIA with the marker file: no NoCudaGameAvailable [GpuVendor=" + vendor + "]", !GpuDetector.Resolve(vendor, Path.Combine(Rig(), "models"), nvMarked).NoCudaGameAvailable);
                Check("no GPU with the marker file: no NoCudaGameAvailable [GpuVendor=" + vendor + "]", !GpuDetector.Resolve(vendor, Path.Combine(Rig(), "models"), WithFile(Machine(), marker)).NoCudaGameAvailable);
            }

            Check("Intel node, no marker: no NoCudaGameAvailable", !GpuDetector.Resolve("auto", Path.Combine(Rig(), "models"), Machine("renderD128", "0x8086")).NoCudaGameAvailable);
            Check("Intel node + marker: NoCudaGameAvailable", GpuDetector.Resolve("auto", Path.Combine(Rig(), "models"), WithFile(Machine("renderD128", "0x8086"), marker)).NoCudaGameAvailable);
            Check("Intel chosen, no Intel node, marker: no NoCudaGameAvailable", !GpuDetector.Resolve("intel", Path.Combine(Rig(), "models"), WithFile(Machine("renderD128", "0x10de"), marker)).NoCudaGameAvailable);

            // Without the marker the Intel probe is what PR 3 made it: no game level, and the fsr2 probe never runs
            // even though one that would pass is installed.
            UseFsr2Probe(Fsr2Passed);
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.Settings = RigSettings("auto");
            UpscaleEngine.StartFsr2Probe();
            JsonElement bare = Probe();
            Equal("Intel, no marker: Game is off alone", "off", string.Join(",", Strings(bare, "Game")));
            Equal("Intel, no marker: Withheld game", "fsr2,dlss,dlaa", string.Join(",", Keys(bare.GetProperty("Withheld").GetProperty("Game"))));
            UpscaleEngine.Plan refused = UpscaleEngine.Decide(SessionJob("upscale", "1080", "game", "fsr2"));
            Equal("Intel, no marker: fsr2 is withheld", "game fsr2", refused.GpuWithheld);
            Check("Intel, no marker: no fsr2 node and no motion note", !refused.GameApplied && refused.GameFilter == null && refused.GameMotionNote == null);
            Equal("Intel, no marker: fsr2 says needs NVIDIA", UpscaleEngine.NeedsNvidia, bare.GetProperty("Withheld").GetProperty("Game").GetProperty("fsr2").GetString());
            Check("Intel, no marker: the fsr2 probe was never run", _fsr2ProbeCalls == 0, _fsr2ProbeCalls + " calls");

            // With the marker and a passing probe, fsr2 is offered and runs, and dlss and dlaa stay withheld.
            UseFsr2Probe(Fsr2Passed);
            GpuDetector.UseEnvironment(WithFile(Machine("renderD128", "0x8086"), marker));
            UpscaleEngine.Settings = RigSettings("auto");
            UpscaleEngine.StartFsr2Probe();
            Check("Intel + marker: start-up runs the probe once", _fsr2ProbeCalls == 1, _fsr2ProbeCalls + " calls");
            JsonElement marked = Probe();
            Check("Intel + marker: Game offers fsr2 and not dlss or dlaa", Strings(marked, "Game").Contains("fsr2") && !Strings(marked, "Game").Contains("dlss") && !Strings(marked, "Game").Contains("dlaa"), string.Join(",", Strings(marked, "Game")));
            Check("Intel + marker: the game labels follow", Keys(marked.GetProperty("GameLabels")).Contains("fsr2") && !Keys(marked.GetProperty("GameLabels")).Contains("dlss"));
            Equal("Intel + marker: Withheld game", "dlss,dlaa", string.Join(",", Keys(marked.GetProperty("Withheld").GetProperty("Game"))));
            Check("Intel + marker: the other withheld axes are untouched", string.Join(",", Keys(marked.GetProperty("Withheld").GetProperty("Denoise"))) == "optix,optix-temporal"
                && string.Join(",", Keys(marked.GetProperty("Withheld").GetProperty("Neural"))) == "realesr-anime-x2,vsr-rtcuda,dlpp-1,dlpp-2,dlpp-3,dlpp-4");

            UpscaleEngine.Plan fsr = UpscaleEngine.Decide(SessionJob("upscale", "1080", "game", "fsr2"));
            Check("Intel + marker: fsr2 runs, nothing withheld", fsr.Act && fsr.GameApplied && fsr.GpuWithheld == null, fsr.Status + ": " + fsr.Reason + " / " + fsr.GpuWithheld);
            Check("Intel + marker: the chain carries the fsr2 node", UpscaleEngine.BuildChain(fsr).Contains("fsr2"), UpscaleEngine.BuildChain(fsr));
            Check("Intel + marker: no CUDA hwaccel", !UpscaleEngine.HwaccelArgs(fsr).Contains("cuda"));
            SessionRecord fsrRecord = UpscaleEngine.Describe(null, fsr, fsr.Status, fsr.Reason);
            Check("Intel + marker: the record says the motion vectors are zero", fsrRecord.GameMotionNote == UpscaleEngine.GameMotionZeroNote && fsrRecord.Summary.Contains("motion vectors are zero"), fsrRecord.Summary);

            UpscaleEngine.Plan dlss = UpscaleEngine.Decide(SessionJob("upscale", "1080", "game", "dlss"));
            Equal("Intel + marker: dlss is still withheld", "game dlss", dlss.GpuWithheld);
            UpscaleEngine.Plan dlaa = UpscaleEngine.Decide(SessionJob("upscale", "1080", "game", "dlaa"));
            Equal("Intel + marker: dlaa is still withheld", "game dlaa", dlaa.GpuWithheld);
            Check("Intel + marker: neither dlss nor dlaa has a node", !dlss.GameApplied && !dlaa.GameApplied && dlss.GameFilter == null && dlaa.GameFilter == null);
            Check("Intel + marker: a pass is kept, the probe ran once in all", _fsr2ProbeCalls == 1, _fsr2ProbeCalls + " calls");

            // A probe that ran and failed: fsr2 is withheld with its own reason, no node and no motion note.
            UseFsr2Probe(Fsr2Failed);
            UpscaleEngine.StartFsr2Probe();
            JsonElement failed = Probe();
            Check("Intel + marker, probe failed: Game is off alone", string.Join(",", Strings(failed, "Game")) == "off", string.Join(",", Strings(failed, "Game")));
            Equal("Intel + marker, probe failed: fsr2 says why", UpscaleEngine.Fsr2ProbeFailed, failed.GetProperty("Withheld").GetProperty("Game").GetProperty("fsr2").GetString());
            Equal("Intel + marker, probe failed: dlss still says needs NVIDIA", UpscaleEngine.NeedsNvidia, failed.GetProperty("Withheld").GetProperty("Game").GetProperty("dlss").GetString());
            UpscaleEngine.Plan failedPlan = UpscaleEngine.Decide(SessionJob("upscale", "1080", "game", "fsr2"));
            Equal("Intel + marker, probe failed: fsr2 is withheld", "game fsr2", failedPlan.GpuWithheld);
            Check("Intel + marker, probe failed: no fsr2 node and no motion note", !failedPlan.GameApplied && failedPlan.GameFilter == null && failedPlan.GameMotionNote == null);
            string failedSummary = UpscaleEngine.Describe(null, failedPlan, failedPlan.Status, failedPlan.Reason).Summary ?? string.Empty;
            Check("Intel + marker, probe failed: the summary does not say needs NVIDIA", !failedSummary.Contains("NVIDIA"), failedSummary);
            Check("Intel + marker, probe failed: the chain has no fsr2", !UpscaleEngine.BuildChain(failedPlan).Contains("fsr2"));

            // A probe with no answer (ffmpeg missing, killed on the time limit, threw), and one that has not landed yet.
            UseFsr2Probe(() => null);
            UpscaleEngine.StartFsr2Probe();
            JsonElement unanswered = Probe();
            Equal("Intel + marker, probe null: fsr2 is waiting", UpscaleEngine.Fsr2ProbePending, unanswered.GetProperty("Withheld").GetProperty("Game").GetProperty("fsr2").GetString());
            Equal("Intel + marker, probe null: fsr2 is withheld", "game fsr2", UpscaleEngine.Decide(SessionJob("upscale", "1080", "game", "fsr2")).GpuWithheld);
            UseFsr2Probe(() => throw new InvalidOperationException("no ffmpeg"));
            UpscaleEngine.StartFsr2Probe();
            Check("Intel + marker, probe threw: fsr2 is withheld", UpscaleEngine.WithheldLevels(UpscaleEngine.Settings)["Game"].ContainsKey("fsr2"));
            var queued = new List<Action>();
            UseFsr2Probe(Fsr2Passed, null, queued.Add);
            Check("Intel + marker, probe pending: fsr2 is withheld and the read did not run it", UpscaleEngine.WithheldLevels(UpscaleEngine.Settings)["Game"].ContainsKey("fsr2") && _fsr2ProbeCalls == 0 && queued.Count == 1, _fsr2ProbeCalls + " calls, " + queued.Count + " queued");
            Equal("Intel + marker, probe pending: the reason", UpscaleEngine.Fsr2ProbePending, UpscaleEngine.WithheldLevels(UpscaleEngine.Settings)["Game"]["fsr2"]);
            Equal("Intel + marker, probe pending: the session is withheld", "game fsr2", UpscaleEngine.Decide(SessionJob("upscale", "1080", "game", "fsr2")).GpuWithheld);
            queued[0]();
            Check("Intel + marker, probe lands: fsr2 is offered", !UpscaleEngine.WithheldLevels(UpscaleEngine.Settings)["Game"].ContainsKey("fsr2") && UpscaleEngine.NoCudaGameNow());

            // CachedProbe's rules on a fake clock: a pass is kept, a failure is asked again only after the TTL.
            DateTime clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            bool pass = false;
            UseFsr2Probe(() => pass ? Fsr2Passed() : Fsr2Failed(), () => clock);
            UpscaleEngine.StartFsr2Probe();
            UpscaleEngine.NoCudaGameNow();
            Check("probe: a failure is not asked again before the TTL", _fsr2ProbeCalls == 1, _fsr2ProbeCalls + " calls");
            clock = clock + UpscaleEngine.Fsr2ProbeRetryAfter + TimeSpan.FromSeconds(1);
            pass = true;
            Check("probe: the read after the TTL still answers from the failure", !UpscaleEngine.NoCudaGameNow());
            Check("probe: and it asked again, off the read", _fsr2ProbeCalls == 2, _fsr2ProbeCalls + " calls");
            Check("probe: the pass is offered", UpscaleEngine.NoCudaGameNow());
            clock = clock + TimeSpan.FromDays(1);
            UpscaleEngine.NoCudaGameNow();
            Check("probe: a pass is kept and never asked again", _fsr2ProbeCalls == 2 && UpscaleEngine.NoCudaGameNow(), _fsr2ProbeCalls + " calls");
            Check("probe: a throw is an answer of null", UpscaleEngine.NewFsr2Probe(() => throw new InvalidOperationException(), null, work => work()).Peek() == null);

            string probeArgs = UpscaleEngine.Fsr2ProbeArgs();
            Check("probe: the command runs fsr2 and starts no device of its own", probeArgs.Contains("fsr2=") && !probeArgs.Contains("-init_hw_device") && !probeArgs.Contains("-filter_hw_device"), probeArgs);

            // The marker never reaches an NVIDIA box or a no-GPU box, and the classification of the levels themselves does not move.
            UseFsr2Probe(Fsr2Passed);
            GpuDetector.UseEnvironment(WithFile(Machine("renderD128", "0x10de"), marker));
            UpscaleEngine.Settings = RigSettings("auto");
            UpscaleEngine.StartFsr2Probe();
            Equal("NVIDIA with the marker file: Game keeps all three", "off,fsr2,dlss,dlaa", string.Join(",", Strings(Probe(), "Game")));
            UpscaleEngine.Plan nvFsr = UpscaleEngine.Decide(SessionJob("upscale", "1080", "game", "fsr2"));
            Check("NVIDIA with the marker file: fsr2 runs with no motion note", nvFsr.GameApplied && nvFsr.GpuWithheld == null && nvFsr.GameMotionNote == null, nvFsr.Status + ": " + nvFsr.Reason);
            Check("NVIDIA with the marker file: the fsr2 probe was never run", _fsr2ProbeCalls == 0, _fsr2ProbeCalls + " calls");
            GpuDetector.UseEnvironment(WithFile(Machine(), marker));
            UpscaleEngine.Settings = RigSettings("auto");
            UpscaleEngine.StartFsr2Probe();
            UpscaleEngine.NoCudaGameNow();
            Probe();
            Check("no GPU with the marker file: the fsr2 probe was never run", _fsr2ProbeCalls == 0, _fsr2ProbeCalls + " calls");
            GpuDetector.UseEnvironment(WithFile(Machine("renderD128", "0x10de"), marker));
            UpscaleEngine.Settings = RigSettings("auto");
            Check("the level classification is unchanged", ShaderLibrary.IsNvidiaOnlyGame("fsr2") && ShaderLibrary.IsCudaFreeGame("fsr2") && !ShaderLibrary.IsCudaFreeGame("dlss") && !ShaderLibrary.IsCudaFreeGame("dlaa") && !ShaderLibrary.IsCudaFreeGame("off") && !ShaderLibrary.IsCudaFreeGame(null));

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.Fsr2Probe = UpscaleEngine.NewFsr2Probe();
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
        }

        // ---- PR 7: IntelHardwareDecode ------------------------------------------------------------

        // The QSV device is pinned to the detected render node in every Intel command (PR 3's review: the
        // encoder's own search is not trusted to pick the detector's node), software or hardware decode.
        private const string QsvPin = " -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD128";

        private const string SoftwareIntelArgs = " -init_hw_device drm=dr:/dev/dri/renderD128 -init_hw_device vulkan=vk@dr" + QsvPin + " -filter_hw_device vk";

        // The decode and map devices alone: what HardwareDecodeDeviceArgs and the start-up test command carry.
        private const string HardwareIntelDeviceArgs = " -init_hw_device vaapi=va:/dev/dri/renderD128 -init_hw_device vulkan=vk@va -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi -filter_hw_device vk";

        // What a session's command carries: the same, with the pinned QSV device after the Vulkan one.
        private const string HardwareIntelArgs = " -init_hw_device vaapi=va:/dev/dri/renderD128 -init_hw_device vulkan=vk@va" + QsvPin + " -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi -filter_hw_device vk";

        private const string Vk0Args = " -init_hw_device vulkan=vk:0 -filter_hw_device vk";

        private const string HardwareSourceReason = "software decode: the source is not upright 8-bit SDR progressive 4:2:0 h264 (Main or High) or hevc (Main)";

        private const string PatchedBinary = "/usr/lib/jellyfin-ffmpeg-oidn/ffmpeg";

        // A 1280x720 session asking for 1080, whose video stream says what codec, depth and scan it is.
        // The profile and pixel format are the ones the gate admits for that codec.
        private static EncodingJobInfo HwJob(string codec, int? bitDepth, bool interlaced, string transfer, int? rotation, params string[] options) =>
            HwJobFull(codec, bitDepth, interlaced, transfer, rotation, string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ? "Main" : "High", "yuv420p", options);

        private static EncodingJobInfo HwJobFull(string codec, int? bitDepth, bool interlaced, string transfer, int? rotation, string profile, string pixelFormat, params string[] options)
        {
            var streamOptions = new Dictionary<string, string>();
            for (int i = 0; i < options.Length; i += 2)
            {
                streamOptions[options[i]] = options[i + 1];
            }

            return new FakeJob
            {
                IsVideoRequest = true,
                VideoStream = new MediaBrowser.Model.Entities.MediaStream
                {
                    Type = MediaBrowser.Model.Entities.MediaStreamType.Video,
                    Width = 1280,
                    Height = 720,
                    Rotation = rotation,
                    Codec = codec,
                    Profile = profile,
                    PixelFormat = pixelFormat,
                    BitDepth = bitDepth,
                    IsInterlaced = interlaced,
                    ColorTransfer = transfer,
                    ColorRange = "tv",
                },
                BaseRequest = new MediaBrowser.Controller.MediaEncoding.BaseEncodingJobOptions { StreamOptions = streamOptions },
            };
        }

        private static EncodingJobInfo PlainHwJob(params string[] options) => HwJob("h264", 8, false, "bt709", null, options);

        private static UpscaleSettings HwSettings(bool on, string vendor = "auto")
        {
            UpscaleSettings cfg = RigSettings(vendor);
            cfg.IntelHardwareDecode = on;
            return cfg;
        }

        private static UpscaleEngine.Plan HwDecide(FakeGpuEnvironment env, UpscaleSettings cfg, EncodingJobInfo job)
        {
            GpuDetector.UseEnvironment(env);
            UpscaleEngine.Settings = cfg;
            return UpscaleEngine.Decide(job);
        }

        // AGENTS.md invariant 13 again, with IntelHardwareDecode on: its reasons name the render node ("the startup
        // test-decode of h264 on /dev/dri/renderD128 ..."), so the non-elevated session route must not carry them
        // (Record, the flat fields or the Summary); the elevated Status route keeps them whole.
        private static void AccessHardwareDecode()
        {
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.DrmVulkanProbeSource = n => true;
            UpscaleEngine.Av1QsvProbeSource = _ => true;
            UpscaleEngine.PatchedFfmpegSource = () => PatchedBinary;
            UpscaleEngine.IntelHwDecodeProbeSource = node => null;
            UpscaleEngine.Plan hwPlan = HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), PlainHwJob("upscale", "1080"));
            SessionRecord hwRecord = UpscaleEngine.Describe(null, hwPlan, hwPlan.Status, hwPlan.Reason);
            hwRecord.PlaySessionId = "harness-access-hw-" + Guid.NewGuid().ToString("N");
            Check("access (hardware decode) setup: the record's reason does name the render node", hwRecord.HardwareDecodeReason != null && hwRecord.HardwareDecodeReason.Contains("/dev/dri/renderD128"), hwRecord.HardwareDecodeReason);
            UpscaleEngine.Record(hwRecord);
            string body = PatcherHost.SessionJson(hwRecord.PlaySessionId);
            Check("non-elevated recorded session, hardware decode on: no render node path anywhere in the body", !body.Contains("/dev/dri") && !body.Contains("renderD"), body);
            Check("the elevated Status route keeps the hardware decode reason whole", PatcherHost.StatusJson().Contains("/dev/dri/renderD128"));
            UpscaleEngine.IntelHwDecodeProbeSource = null;
            UpscaleEngine.PatchedFfmpegSource = null;
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
            GpuDetector.UseEnvironment(null);
            UpscaleEngine.Settings = new UpscaleSettings();
        }

        private static void IntelHardwareDecode()
        {
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.DrmVulkanProbeSource = n => true;
            UpscaleEngine.Av1QsvProbeSource = _ => true;
            SetFilters("optix", "oidn", "ort", "fsr2", "dlss", "dlpp_rtcuda", "vsr_rtcuda", "libplacebo", "nlmeans_vulkan", "atadenoise", "deblock");
            string marker = Path.Combine(Rig(), "fsr2-nocuda");
            UpscaleEngine.PatchedFfmpegSource = () => PatchedBinary;
            int calls = 0;
            bool? answer = true;
            UpscaleEngine.IntelHwDecodeProbeSource = node =>
            {
                calls++;
                return answer;
            };

            // The default is off, in both copies of the setting, and survives the settings JSON.
            Check("IntelHardwareDecode defaults to off", !new UpscaleSettings().IntelHardwareDecode);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            Check("settings JSON without IntelHardwareDecode keeps it off", !JsonSerializer.Deserialize<UpscaleSettings>("{\"Enabled\":true}", opts).IntelHardwareDecode);
            Check("settings JSON carries IntelHardwareDecode across the boundary", JsonSerializer.Deserialize<UpscaleSettings>("{\"IntelHardwareDecode\":true}", opts).IntelHardwareDecode);

            // The golden: setting on, an Intel GPU, a passing test, a plan with nothing CPU-side.
            calls = 0;
            UpscaleEngine.Plan hw = HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), PlainHwJob("upscale", "1080"));
            Check("on, Intel, test passes: the session is enhanced", hw.Act, hw.Status + ": " + hw.Reason);
            Check("on, Intel, test passes: hardware decode", hw.HardwareDecode, hw.HardwareDecodeReason);
            Equal("on, Intel, test passes: the reason", "decoded on the Intel GPU (VA-API) and mapped to Vulkan", hw.HardwareDecodeReason);
            Check("on, Intel, test passes: the test ran once", calls == 1, "calls " + calls);
            Equal("hardware decode: the command's device arguments", HardwareIntelArgs, UpscaleEngine.HwaccelArgs(hw));
            string chain = UpscaleEngine.BuildChain(hw);
            Check("hardware decode: the chain maps the VA-API surfaces to Vulkan first",
                chain.StartsWith("hwmap=derive_device=vulkan,libplacebo=w=1920:h=1080:upscaler="), chain);
            Check("hardware decode: the chain downloads as nv12", chain.EndsWith(",hwdownload,format=nv12"), chain);
            Check("hardware decode: no hwupload and no format=yuv420p", !chain.Contains("hwupload") && !chain.Contains("yuv420p"), chain);
            Equal("hardware decode, literal plan: the chain",
                "hwmap=derive_device=vulkan,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos,hwdownload,format=nv12",
                UpscaleEngine.BuildChain(new UpscaleEngine.Plan { Width = 1920, Height = 1080, UpscaleApplied = true, HardwareDecode = true, HardwareDecodeFormat = "nv12" }));
            Equal("hardware decode, literal plan with deband: the chain",
                "hwmap=derive_device=vulkan,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos:deband=1:deband_threshold=3:deband_grain=0,hwdownload,format=nv12",
                UpscaleEngine.BuildChain(new UpscaleEngine.Plan { Width = 1920, Height = 1080, UpscaleApplied = true, DebandApplied = true, HardwareDecode = true, HardwareDecodeFormat = "nv12" }));
            Equal("hardware decode, untagged range: tagged before the map",
                "setparams=range=tv,hwmap=derive_device=vulkan,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos,hwdownload,format=nv12",
                UpscaleEngine.BuildChain(new UpscaleEngine.Plan { Width = 1920, Height = 1080, UpscaleApplied = true, RangeUntagged = true, HardwareDecode = true, HardwareDecodeFormat = "nv12" }));
            Equal("the surface format of 8-bit 4:2:0 is nv12", "nv12", UpscaleEngine.HardwareSurfaceFormat("yuv420p"));
            foreach (string other in new[] { "yuv420p10le", "yuv422p", "yuv444p", "p010le", "nv12", string.Empty, null })
            {
                Check("no surface format, so no hardware decode, for " + (other ?? "null"), UpscaleEngine.HardwareSurfaceFormat(other) == null);
            }

            Equal("the decided plan carries the derived format", "nv12", hw.HardwareDecodeFormat);
            Check("a software plan carries no format", HwDecide(Machine("renderD128", "0x8086"), HwSettings(false), PlainHwJob("upscale", "1080")).HardwareDecodeFormat == null);
            Equal("the software chain with the same literal plan is the one it always was",
                "format=yuv420p,hwupload,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos,hwdownload,format=yuv420p",
                UpscaleEngine.BuildChain(new UpscaleEngine.Plan { Width = 1920, Height = 1080, UpscaleApplied = true }));

            // The start-up test is the production command: same device arguments (decoder bound by name),
            // a decode of a real clip, and the production chain with libplacebo on the mapped surfaces.
            Equal("the device arguments, shared by the command and the test", HardwareIntelDeviceArgs, UpscaleEngine.HardwareDecodeDeviceArgs("/dev/dri/renderD128", string.Empty));
            Equal("the device arguments carry the QSV device after the Vulkan one",
                " -init_hw_device vaapi=va:/dev/dri/renderD128 -init_hw_device vulkan=vk@va -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD128 -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi -filter_hw_device vk",
                UpscaleEngine.HardwareDecodeDeviceArgs("/dev/dri/renderD128", " -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD128"));
            string testChain = UpscaleEngine.HardwareDecodeTestChain();
            Equal("the test chain is the production hardware-decode chain",
                "hwmap=derive_device=vulkan,libplacebo=w=512:h=288:upscaler=ewa_lanczos,hwdownload,format=nv12", testChain);
            Equal("the test command", "-hide_banner -loglevel error" + HardwareIntelDeviceArgs + " -i \"/tmp/h264.mp4\" -vf \"" + testChain + "\" -frames:v 3 -f null -",
                UpscaleEngine.HardwareDecodeTestArgs("/dev/dri/renderD128", "/tmp/h264.mp4", testChain));
            Check("the test command decodes before it filters: no testsrc, no hwupload, no -filter_hw_device va",
                !UpscaleEngine.HardwareDecodeTestArgs("/dev/dri/renderD128", "/tmp/h264.mp4", testChain).Contains("testsrc")
                && !UpscaleEngine.HardwareDecodeTestArgs("/dev/dri/renderD128", "/tmp/h264.mp4", testChain).Contains("hwupload")
                && !UpscaleEngine.HardwareDecodeTestArgs("/dev/dri/renderD128", "/tmp/h264.mp4", testChain).Contains("-filter_hw_device va"));
            // The test command is the production device arguments, QSV pin included: both come from the one DeviceInit.
            string productionDevices = UpscaleEngine.HwaccelArgs(hw);
            string testCommand = UpscaleEngine.HardwareDecodeTestCommand("/dev/dri/renderD128", "/tmp/h264.mp4", testChain);
            Check("the start-up test command carries the QSV pin", testCommand.Contains(QsvPin), testCommand);
            Equal("the start-up test command's device arguments are the session command's own",
                "-hide_banner -loglevel error" + productionDevices + " -i \"/tmp/h264.mp4\" -vf \"" + testChain + "\" -frames:v 3 -f null -", testCommand);
            string h264Clip = UpscaleEngine.HardwareDecodeClipArgs("h264", "/tmp/h264.mp4");
            string hevcClip = UpscaleEngine.HardwareDecodeClipArgs("hevc", "/tmp/hevc.mp4");
            Check("the h264 clip is 8-bit 4:2:0 Main", h264Clip.Contains("-pix_fmt yuv420p") && h264Clip.Contains("-c:v libx264 -profile:v main") && h264Clip.EndsWith("-y \"/tmp/h264.mp4\""), h264Clip);
            Check("the hevc clip is 8-bit 4:2:0 Main", hevcClip.Contains("-pix_fmt yuv420p") && hevcClip.Contains("-c:v libx265 -profile:v main") && hevcClip.EndsWith("-y \"/tmp/hevc.mp4\""), hevcClip);

            SessionRecord on = UpscaleEngine.Describe(null, hw, hw.Status, hw.Reason);
            Check("the record says hardware decode ran", on.HardwareDecode == true && on.HardwareDecodeReason == hw.HardwareDecodeReason);
            Check("the record's JSON carries both", JsonSerializer.Serialize(on).Contains("\"HardwareDecode\":true") && JsonSerializer.Serialize(on).Contains("\"HardwareDecodeReason\":"));
            Check("the summary says it", on.Summary.Contains("decoded on the Intel GPU"), on.Summary);

            // hevc, any case of the codec name, and an untagged transfer all qualify.
            Check("hevc qualifies", HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), HwJob("hevc", 8, false, null, null, "upscale", "1080")).HardwareDecode);
            Check("H264 in capitals qualifies", HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), HwJob("H264", 8, false, string.Empty, null, "upscale", "1080")).HardwareDecode);

            // A failed or inconclusive test keeps software decode, and says which.
            foreach (bool? result in new bool?[] { false, null })
            {
                answer = result;
                UpscaleEngine.Plan soft = HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), PlainHwJob("upscale", "1080"));
                string tag = " [test " + (result == null ? "inconclusive" : "failed") + "]";
                Check("software decode" + tag, soft.Act && !soft.HardwareDecode, soft.HardwareDecodeReason);
                Check("the reason names the test" + tag, soft.HardwareDecodeReason.StartsWith("software decode: the startup test-decode of h264 on /dev/dri/renderD128 ") && soft.HardwareDecodeReason.EndsWith(result == null ? "could not be run" : "failed"), soft.HardwareDecodeReason);
                Equal("the device arguments are the software ones" + tag, SoftwareIntelArgs, UpscaleEngine.HwaccelArgs(soft));
                Check("the chain is the software chain" + tag, UpscaleEngine.BuildChain(soft).StartsWith("format=yuv420p") && UpscaleEngine.BuildChain(soft).Contains("hwupload"));
                SessionRecord softRecord = UpscaleEngine.Describe(null, soft, soft.Status, soft.Reason);
                Check("the record says why" + tag, softRecord.HardwareDecode == false && softRecord.HardwareDecodeReason == soft.HardwareDecodeReason);
            }

            answer = true;
            UpscaleEngine.IntelHwDecodeProbeSource = node => throw new InvalidOperationException("probe broke");
            UpscaleEngine.Plan thrown = HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), PlainHwJob("upscale", "1080"));
            Check("a test that throws keeps software decode and the session is still enhanced", thrown.Act && !thrown.HardwareDecode && thrown.HardwareDecodeReason.StartsWith("software decode: error"), thrown.HardwareDecodeReason);
            UpscaleEngine.IntelHwDecodeProbeSource = node =>
            {
                calls++;
                return answer;
            };

            // A plan with a step that cannot follow VA-API decode keeps software decode, without running the test.
            FakeGpuEnvironment marked = WithFile(Machine("renderD128", "0x8086"), marker);
            var blocked = new Dictionary<string, string[]>
            {
                ["deblock"] = new[] { "upscale", "1080", "deblock", "light" },
                ["atadenoise"] = new[] { "upscale", "1080", "denoise", "light" },
                ["neural"] = new[] { "upscale", "1080", "neural", "realesr-anime-x2" },
                ["game"] = new[] { "upscale", "1080", "game", "fsr2" },
            };
            foreach (var pair in blocked)
            {
                calls = 0;
                if (pair.Key == "game")
                {
                    // PR 6's review: fsr2 runs on Intel only once its start-up probe has passed, so it has to have.
                    GpuDetector.UseEnvironment(marked);
                    UpscaleEngine.Settings = HwSettings(true);
                    UseFsr2Probe(Fsr2Passed);
                    UpscaleEngine.StartFsr2Probe();
                }

                UpscaleEngine.Plan cpu = HwDecide(marked, HwSettings(true), PlainHwJob(pair.Value));
                string tag = " [" + pair.Key + "]";
                Check("a CPU-side step keeps software decode" + tag, cpu.Act && !cpu.HardwareDecode, cpu.Status + ": " + cpu.Reason + " / " + cpu.HardwareDecodeReason);
                Check("the reason names the step" + tag, cpu.HardwareDecodeReason.StartsWith("software decode: the plan has a step that cannot follow VA-API decode (") && cpu.HardwareDecodeReason.Contains(pair.Key == "atadenoise" ? "denoise" : pair.Key), cpu.HardwareDecodeReason);
                Check("the test did not run" + tag, calls == 0, "calls " + calls);
                Equal("the device arguments are the software ones" + tag, SoftwareIntelArgs, UpscaleEngine.HwaccelArgs(cpu));
                Check("the chain is the software chain" + tag, UpscaleEngine.BuildChain(cpu).StartsWith("format=yuv420p"), UpscaleEngine.BuildChain(cpu));
            }

            UpscaleEngine.Fsr2Probe = UpscaleEngine.NewFsr2Probe();

            // A GPU-side Vulkan denoise rides along only when the patched binary can compile it.
            UpscaleEngine.Plan vulkanDenoise = HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), PlainHwJob("upscale", "1080", "denoise", "strong"));
            Check("a Vulkan denoise follows the patched-binary shader test", vulkanDenoise.HardwareDecode == UpscaleEngine.VulkanDenoiseRunsOnPatchedBinary(), vulkanDenoise.HardwareDecodeReason);

            // Sources Jellyfin would tone-map, deinterlace or rotate, or the decoder may not take.
            var sources = new Dictionary<string, EncodingJobInfo>
            {
                ["vp9"] = HwJob("vp9", 8, false, "bt709", null, "upscale", "1080"),
                ["av1"] = HwJob("av1", 8, false, "bt709", null, "upscale", "1080"),
                ["no codec"] = HwJob(null, 8, false, "bt709", null, "upscale", "1080"),
                ["10-bit"] = HwJob("hevc", 10, false, "bt709", null, "upscale", "1080"),
                ["unknown depth"] = HwJob("h264", null, false, "bt709", null, "upscale", "1080"),
                ["interlaced"] = HwJob("h264", 8, true, "bt709", null, "upscale", "1080"),
                ["HDR10"] = HwJob("hevc", 8, false, "smpte2084", null, "upscale", "1080"),
                ["HLG"] = HwJob("hevc", 8, false, "arib-std-b67", null, "upscale", "1080"),
                ["sideways"] = HwJob("h264", 8, false, "bt709", 90, "upscale", "1920"),
            };
            foreach (var pair in sources)
            {
                calls = 0;
                UpscaleEngine.Plan source = HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), pair.Value);
                string tag = " [" + pair.Key + "]";
                Check("source keeps software decode" + tag, source.Act && !source.HardwareDecode, source.Status + ": " + source.Reason + " / " + source.HardwareDecodeReason);
                Equal("the reason names the source" + tag, HardwareSourceReason, source.HardwareDecodeReason);
                Check("the test did not run" + tag, calls == 0, "calls " + calls);
                Equal("the device arguments are the software ones" + tag, SoftwareIntelArgs, UpscaleEngine.HwaccelArgs(source));
            }

            // No usable render node path: software decode, reason says so, no test.
            calls = 0;
            UpscaleEngine.Plan noNode = HwDecide(Machine("render X;rm", "0x8086"), HwSettings(true), PlainHwJob("upscale", "1080"));
            Check("an odd render node name keeps software decode", noNode.Act && !noNode.HardwareDecode && noNode.HardwareDecodeReason == "software decode: the Intel render node has no usable path" && calls == 0, noNode.HardwareDecodeReason);
            Equal("...and the command is the vk:0 one", Vk0Args, UpscaleEngine.HwaccelArgs(noNode));

            // Setting off: an Intel box is exactly what PR 6 left it, the test never runs, the record has no new key.
            calls = 0;
            UpscaleEngine.Plan off = HwDecide(Machine("renderD128", "0x8086"), HwSettings(false), PlainHwJob("upscale", "1080"));
            Check("off, Intel: no hardware decode, no reason", off.Act && !off.HardwareDecode && off.HardwareDecodeReason == null);
            Equal("off, Intel: the device arguments", SoftwareIntelArgs, UpscaleEngine.HwaccelArgs(off));
            Check("off, Intel: the chain is the software chain", UpscaleEngine.BuildChain(off).StartsWith("format=yuv420p,hwupload,libplacebo=w=1920:h=1080:upscaler=") && UpscaleEngine.BuildChain(off).EndsWith(",hwdownload,format=yuv420p"), UpscaleEngine.BuildChain(off));
            Check("off, Intel: the test never ran", calls == 0);
            SessionRecord offRecord = UpscaleEngine.Describe(null, off, off.Status, off.Reason);
            string offJson = JsonSerializer.Serialize(offRecord);
            Check("off, Intel: the record has neither key", !offJson.Contains("HardwareDecode") && offRecord.HardwareDecode == null && offRecord.HardwareDecodeReason == null, offJson);
            Check("off, Intel: the summary does not mention it", !offRecord.Summary.Contains("VA-API") && !offRecord.Summary.Contains("software decode"), offRecord.Summary);

            // Setting on but no Intel GPU that was seen: nothing changes anywhere (null reason, no record keys,
            // no probe key), so the command, the record and the probe are the setting-off ones.
            var others = new Dictionary<string, FakeGpuEnvironment[]>
            {
                ["NVIDIA"] = new[] { Machine("renderD128", "0x10de") },
                ["no GPU"] = new[] { Machine() },
                ["mixed, auto"] = new[] { Machine("renderD128", "0x8086", "renderD129", "0x10de") },
            };
            foreach (var pair in others)
            {
                calls = 0;
                string tag = " [" + pair.Key + "]";
                UpscaleEngine.Plan noIntel = HwDecide(pair.Value[0], HwSettings(true), PlainHwJob("upscale", "1080"));
                UpscaleEngine.Plan baseline = HwDecide(pair.Value[0], HwSettings(false), PlainHwJob("upscale", "1080"));
                Check("on, no Intel path: still enhanced as with the setting off" + tag, noIntel.Act == baseline.Act, noIntel.Status + ": " + noIntel.Reason);
                Check("on, no Intel path: software decode, and no reason" + tag, !noIntel.HardwareDecode && noIntel.HardwareDecodeReason == null, noIntel.HardwareDecodeReason);
                Check("on, no Intel path: the test never ran" + tag, calls == 0);
                SessionRecord baselineRecord = UpscaleEngine.Describe(null, baseline, baseline.Status, baseline.Reason);
                SessionRecord noIntelRecord = UpscaleEngine.Describe(null, noIntel, noIntel.Status, noIntel.Reason);
                baselineRecord.Timestamp = noIntelRecord.Timestamp = string.Empty;
                Equal("on, no Intel path: the session record is the setting-off one" + tag, JsonSerializer.Serialize(baselineRecord), JsonSerializer.Serialize(noIntelRecord));
                Check("on, no Intel path: the record has no HardwareDecode key" + tag, !JsonSerializer.Serialize(noIntelRecord).Contains("HardwareDecode"));
                GpuDetector.UseEnvironment(pair.Value[0]);
                UpscaleEngine.Settings = HwSettings(false);
                string probeBaseline = Probe().GetRawText();
                UpscaleEngine.Settings = HwSettings(true);
                Equal("on, no Intel path: the probe is the setting-off one" + tag, probeBaseline, Probe().GetRawText());
                Check("on, no Intel path: the probe has no IntelHardwareDecode key" + tag, !Keys(Probe()).Contains("IntelHardwareDecode"));
                Equal("on, no Intel path: the device arguments are the setting-off ones" + tag, UpscaleEngine.HwaccelArgs(baseline), UpscaleEngine.HwaccelArgs(noIntel));
                Equal("on, no Intel path: the chain is the setting-off one" + tag, UpscaleEngine.BuildChain(baseline), UpscaleEngine.BuildChain(noIntel));
                Check("off, no Intel path: no reason" + tag, baseline.HardwareDecodeReason == null);
                Check("off, no Intel path: the record has no new key" + tag, !JsonSerializer.Serialize(UpscaleEngine.Describe(null, baseline, baseline.Status, baseline.Reason)).Contains("HardwareDecode"));
            }

            // The source gate by profile and chroma format: only 8-bit 4:2:0 h264 Main/High and hevc Main reach the
            // GPU decoder; the rest would come back as software frames and kill the stream at the hwmap.
            answer = true;
            var gate = new (string Codec, string Profile, string Pixel, bool Hardware)[]
            {
                ("h264", "Main", "yuv420p", true),
                ("h264", "High", "yuv420p", true),
                ("hevc", "Main", "yuv420p", true),
                ("H264", " high ", "YUV420P", true),
                ("hevc", "MAIN", "yuv420p", true),
                ("h264", "Baseline", "yuv420p", false),
                ("h264", "Constrained Baseline", "yuv420p", false),
                ("h264", "Extended", "yuv420p", false),
                ("h264", "High 10", "yuv420p", false),
                ("h264", "High 10", "yuv420p10le", false),
                ("h264", "High 4:2:2", "yuv422p", false),
                ("h264", "High 4:2:2", "yuv420p", false),
                ("h264", "High 4:4:4 Predictive", "yuv444p", false),
                ("h264", "High 4:4:4 Predictive", "yuv420p", false),
                ("h264", "High", "yuv422p", false),
                ("h264", "High", "yuv444p", false),
                ("h264", "High", "yuvj420p", false),
                ("h264", "Main", "yuvj420p", false),
                ("hevc", "Main 10", "yuv420p10le", false),
                ("hevc", "Main 10", "yuv420p", false),
                ("hevc", "Rext", "yuv420p", false),
                ("hevc", "Rext", "yuv422p", false),
                ("hevc", "Rext", "yuv444p", false),
                ("hevc", "Main Still Picture", "yuv420p", false),
                ("hevc", "Main", "yuv422p", false),
                ("hevc", "Main", "yuv444p", false),
                ("hevc", "Main", "yuvj420p", false),
                ("hevc", "High", "yuv420p", false),
                ("h264", null, "yuv420p", false),
                ("h264", string.Empty, "yuv420p", false),
                ("hevc", null, "yuv420p", false),
                ("h264", "High", null, false),
                ("h264", "High", string.Empty, false),
                ("hevc", "Main", null, false),
            };
            foreach (var row in gate)
            {
                calls = 0;
                string tag = " [" + row.Codec + " / " + (row.Profile ?? "no profile") + " / " + (row.Pixel ?? "no pixel format") + "]";
                UpscaleEngine.Plan p = HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), HwJobFull(row.Codec, 8, false, "bt709", null, row.Profile, row.Pixel, "upscale", "1080"));
                Check("the session is enhanced" + tag, p.Act, p.Status + ": " + p.Reason);
                if (row.Hardware)
                {
                    Check("hardware decode" + tag, p.HardwareDecode, p.HardwareDecodeReason);
                    Equal("the device arguments are the hardware ones" + tag, HardwareIntelArgs, UpscaleEngine.HwaccelArgs(p));
                }
                else
                {
                    Check("software decode" + tag, !p.HardwareDecode, p.HardwareDecodeReason);
                    Equal("the reason is the source one" + tag, HardwareSourceReason, p.HardwareDecodeReason);
                    Check("the test did not run" + tag, calls == 0, "calls " + calls);
                    Equal("the device arguments are the software ones" + tag, SoftwareIntelArgs, UpscaleEngine.HwaccelArgs(p));
                }
            }

            // The start-up test is read, never waited on, and a bad answer is not kept for the life of the process.
            UpscaleEngine.IntelHwDecodeProbeSource = null;
            var queued = new List<Action>();
            DateTime clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            int runs = 0;
            Func<UpscaleEngine.HwDecodeAnswer> next = () => new UpscaleEngine.HwDecodeAnswer(true, true);
            UpscaleEngine.HardwareDecodeProbeFactory = n => new CachedProbe<UpscaleEngine.HwDecodeAnswer>(
                () =>
                {
                    runs++;
                    return next();
                },
                a => a.Conclusive,
                UpscaleEngine.HardwareDecodeRetryAfter,
                () => clock,
                queued.Add);
            Func<string, UpscaleEngine.Plan> decide = codec => HwDecide(Machine("renderD128", "0x8086"), HwSettings(true), HwJob(codec, 8, false, "bt709", null, "upscale", "1080"));
            Action drain = () =>
            {
                Action[] work = queued.ToArray();
                queued.Clear();
                foreach (Action w in work)
                {
                    w();
                }
            };
            const string Test264 = "software decode: the startup test-decode of h264 on /dev/dri/renderD128";

            UpscaleEngine.ResetHardwareDecodeProbes();
            UpscaleEngine.Plan cold = decide("h264");
            Check("no answer yet: the session is enhanced in software", cold.Act && !cold.HardwareDecode, cold.HardwareDecodeReason);
            Equal("no answer yet: the reason says so", Test264 + " has not finished", cold.HardwareDecodeReason);
            Check("no answer yet: the probe was queued, not run, so the plan did not wait", queued.Count == 1 && runs == 0, "queued " + queued.Count + ", runs " + runs);
            Equal("no answer yet: the command is the software one", SoftwareIntelArgs, UpscaleEngine.HwaccelArgs(cold));
            decide("h264");
            Check("a second session does not queue a second probe", queued.Count == 1 && runs == 0);
            drain();
            Check("the probe ran once", runs == 1, "runs " + runs);
            Check("a passed test: both codecs decode on the GPU", decide("h264").HardwareDecode && decide("hevc").HardwareDecode);
            clock = clock.AddDays(30);
            Check("true is kept for life: no probe however late", decide("h264").HardwareDecode && queued.Count == 0 && runs == 1, "queued " + queued.Count + ", runs " + runs);

            UpscaleEngine.ResetHardwareDecodeProbes();
            runs = 0;
            next = () => new UpscaleEngine.HwDecodeAnswer(false, false);
            decide("h264");
            drain();
            Equal("a failed test: the reason says failed", Test264 + " failed", decide("h264").HardwareDecodeReason);
            clock = clock.AddSeconds(59);
            decide("h264");
            Check("a failed test is not asked again inside the delay", queued.Count == 0 && runs == 1, "queued " + queued.Count + ", runs " + runs);
            clock = clock.AddSeconds(2);
            UpscaleEngine.Plan standing = decide("h264");
            Check("after the delay it is asked again in the background while the last answer stands", queued.Count == 1 && !standing.HardwareDecode && standing.HardwareDecodeReason == Test264 + " failed", standing.HardwareDecodeReason);
            next = () => new UpscaleEngine.HwDecodeAnswer(true, true);
            drain();
            Check("a later pass turns hardware decode on without a restart", decide("h264").HardwareDecode && runs == 2, "runs " + runs);

            UpscaleEngine.ResetHardwareDecodeProbes();
            next = () => throw new InvalidOperationException("probe broke");
            decide("h264");
            drain();
            UpscaleEngine.Plan broke = decide("h264");
            Check("a probe that throws is no answer: software decode, the session still enhanced", broke.Act && !broke.HardwareDecode && broke.HardwareDecodeReason == Test264 + " has not finished", broke.HardwareDecodeReason);
            clock = clock.AddSeconds(61);
            decide("h264");
            next = () => new UpscaleEngine.HwDecodeAnswer(true, true);
            drain();
            Check("a throwing probe is retried and then passes", decide("h264").HardwareDecode);

            UpscaleEngine.ResetHardwareDecodeProbes();
            next = () => new UpscaleEngine.HwDecodeAnswer(null, null);
            decide("h264");
            drain();
            Equal("a test that could not be run: the reason says so", Test264 + " could not be run", decide("h264").HardwareDecodeReason);

            UpscaleEngine.ResetHardwareDecodeProbes();
            next = () => new UpscaleEngine.HwDecodeAnswer(true, false);
            decide("h264");
            drain();
            Check("the answer is per codec: h264 passed, so h264 decodes on the GPU", decide("h264").HardwareDecode);
            UpscaleEngine.Plan hevcOnly = decide("hevc");
            Check("the answer is per codec: hevc failed, so hevc stays in software", !hevcOnly.HardwareDecode && hevcOnly.HardwareDecodeReason == "software decode: the startup test-decode of hevc on /dev/dri/renderD128 failed", hevcOnly.HardwareDecodeReason);
            Equal("...and its command is the software one", SoftwareIntelArgs, UpscaleEngine.HwaccelArgs(hevcOnly));

            // The patched ffmpeg is looked up for every plan: a binary removed after start-up gives software decode.
            UpscaleEngine.ResetHardwareDecodeProbes();
            queued.Clear();
            UpscaleEngine.PatchedFfmpegSource = () => null;
            UpscaleEngine.Plan nobin = decide("h264");
            Check("no patched ffmpeg: software decode with the reason", nobin.Act && !nobin.HardwareDecode && nobin.HardwareDecodeReason.StartsWith("software decode: the patched ffmpeg is not installed"), nobin.HardwareDecodeReason);
            Check("no patched ffmpeg: no test was started", queued.Count == 0);
            Equal("no patched ffmpeg: the command is the software one", SoftwareIntelArgs, UpscaleEngine.HwaccelArgs(nobin));
            UpscaleEngine.PatchedFfmpegSource = () => PatchedBinary;

            // Start-up: the probe is started in the background for an Intel node with the setting on, and nowhere else.
            UpscaleEngine.ResetHardwareDecodeProbes();
            queued.Clear();
            runs = 0;
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.Settings = HwSettings(true);
            UpscaleEngine.PrimeHardwareDecode();
            Check("start-up: an Intel node with the setting on queues the probe without running it", queued.Count == 1 && runs == 0, "queued " + queued.Count + ", runs " + runs);
            UpscaleEngine.ResetHardwareDecodeProbes();
            queued.Clear();
            UpscaleEngine.Settings = HwSettings(false);
            UpscaleEngine.PrimeHardwareDecode();
            Check("start-up: the setting off starts nothing", queued.Count == 0);
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            UpscaleEngine.Settings = HwSettings(true);
            UpscaleEngine.PrimeHardwareDecode();
            Check("start-up: an NVIDIA server starts nothing", queued.Count == 0);
            UpscaleEngine.HardwareDecodeProbeFactory = null;
            UpscaleEngine.ResetHardwareDecodeProbes();
            answer = true;
            UpscaleEngine.IntelHwDecodeProbeSource = node =>
            {
                calls++;
                return answer;
            };

            // Jellyfin's own kept nodes: before the chain in software decode, after its download under hardware decode.
            var keptNodes = new List<string> { "setparams=color_primaries=bt709", "setparams=color_trc=bt709" };
            Equal("software decode: kept nodes lead", "setparams=color_primaries=bt709,setparams=color_trc=bt709,CHAIN", UpscaleEngine.JoinChain(new UpscaleEngine.Plan(), "CHAIN", keptNodes));
            Equal("hardware decode: kept nodes follow the download", "CHAIN,setparams=color_primaries=bt709,setparams=color_trc=bt709", UpscaleEngine.JoinChain(new UpscaleEngine.Plan { HardwareDecode = true }, "CHAIN", keptNodes));
            Equal("software decode, nothing kept: the chain alone", "CHAIN", UpscaleEngine.JoinChain(new UpscaleEngine.Plan(), "CHAIN", new List<string>()));
            Equal("hardware decode, nothing kept: the chain alone", "CHAIN", UpscaleEngine.JoinChain(new UpscaleEngine.Plan { HardwareDecode = true }, "CHAIN", new List<string>()));

            // Two GPUs with Intel chosen: the Intel node, not the first or the NVIDIA one.
            UpscaleEngine.Plan chosen = HwDecide(Machine("renderD128", "0x10de", "renderD129", "0x8086"), HwSettings(true, "intel"), PlainHwJob("upscale", "1080"));
            Check("two GPUs, intel chosen: hardware decode", chosen.HardwareDecode, chosen.HardwareDecodeReason);
            Equal("two GPUs, intel chosen: the Intel node", " -init_hw_device vaapi=va:/dev/dri/renderD129 -init_hw_device vulkan=vk@va -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD129 -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi -filter_hw_device vk", UpscaleEngine.HwaccelArgs(chosen));

            // The probe: no new key until the setting is on.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.Settings = HwSettings(false);
            JsonElement probeOff = Probe();
            UpscaleEngine.Settings = HwSettings(true);
            JsonElement probeOn = Probe();
            Check("setting off: the probe has no IntelHardwareDecode key", !Keys(probeOff).Contains("IntelHardwareDecode"));
            Check("setting on: the probe carries IntelHardwareDecode=true", probeOn.TryGetProperty("IntelHardwareDecode", out JsonElement flag) && flag.ValueKind == JsonValueKind.True);
            Check("setting on: nothing else in the probe moved", Keys(probeOff).All(k => probeOff.GetProperty(k).GetRawText() == probeOn.GetProperty(k).GetRawText()) && Keys(probeOn).Count == Keys(probeOff).Count + 1);
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            UpscaleEngine.Settings = HwSettings(false);
            Check("NVIDIA, setting off: the probe keys are the ones it always had", string.Join(",", Keys(Probe()).OrderBy(k => k, StringComparer.Ordinal)) == ProbeKeys, string.Join(",", Keys(Probe())));

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.Settings = new UpscaleSettings();
            UpscaleEngine.IntelHwDecodeProbeSource = null;
            UpscaleEngine.PatchedFfmpegSource = null;
            UpscaleEngine.HardwareDecodeProbeFactory = null;
            UpscaleEngine.ResetHardwareDecodeProbes();
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
        }

        private static FakeGpuEnvironment WithFile(FakeGpuEnvironment env, string path)
        {
            env.Files.Add(path);
            return env;
        }

        private static string _neuralRig;

        // A directory holding all three Real-ESRGAN weights (a stand-in file each) and, beside it, where the
        // OpenVINO provider would sit. The fake machine decides whether that provider file "exists".
        private static string NeuralRig()
        {
            if (_neuralRig != null)
            {
                return _neuralRig;
            }

            string root = Path.Combine(Path.GetTempPath(), "gpu-vendor-neural-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "models"));
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            foreach (string file in new[] { "realesr-animevideo-x2-fp16.onnx", "realesr-animevideov3-x4-fp16.onnx", "realesr-general-x4v3-fp16.onnx" })
            {
                File.WriteAllText(Path.Combine(root, "models", file), "x");
            }

            _neuralRig = root;
            return root;
        }

        private static string NeuralProviderFile() => Path.Combine(NeuralRig(), "ort/lib/libonnxruntime_providers_openvino.so");

        private static string X2() => Path.Combine(NeuralRig(), "models", "realesr-animevideo-x2-fp16.onnx");

        private static UpscaleSettings NeuralSettings(string vendor, int maxHeight, bool weights = true) => new UpscaleSettings
        {
            Encoder = "auto",
            GpuVendor = vendor,
            NeuralMaxSourceHeight = maxHeight,
            NeuralModelDirectory = Path.Combine(NeuralRig(), weights ? "models" : "empty"),
        };

        // A machine, with the OpenVINO provider file present when asked.
        private static FakeGpuEnvironment NeuralMachine(bool provider, params string[] nodeAndId)
        {
            FakeGpuEnvironment env = Machine(nodeAndId);
            if (provider)
            {
                // The detector looks beside the model directory of the settings in force, which is models/ or empty/.
                env.Files.Add(NeuralProviderFile());
            }

            return env;
        }

        private static UpscaleEngine.Plan DecideOn(FakeGpuEnvironment env, UpscaleSettings cfg, int width, int height, params string[] options)
        {
            GpuDetector.UseEnvironment(env);
            UpscaleEngine.Settings = cfg;
            return UpscaleEngine.Decide(SessionJobAt(width, height, null, options));
        }

        // Which ort device the node asks for. Intel only changes anything; every other server's node is the one it always was.
        private static void NeuralDevice()
        {
            UpscaleEngine.IntelOnnxMeasured = true; // PR 5's path is what is under test; the withhold itself is tested in NvidiaOnlyLevels
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.DrmVulkanProbeSource = n => true;
            UpscaleEngine.Av1QsvProbeSource = _ => true;
            UpscaleEngine.OrtOpenVinoProbeSource = () => true;
            SetFilters("optix", "oidn", "ort", "fsr2", "dlss", "libplacebo", "nlmeans_vulkan");

            string golden = "format=gbrpf32le,ort=model=" + X2() + ",format=yuv420p";
            string[] ask = { "upscale", "1080", "neural", "realesr-anime-x2" };

            // NVIDIA, no GPU, a mixed box on auto, an Intel box that is told it is not Intel: the node is byte-identical,
            // for every setting value, with and without the OpenVINO provider file on disk.
            foreach (bool provider in new[] { false, true })
            {
                foreach (string vendor in new[] { "auto", "nvidia", "none", "banana", "intel" })
                {
                    string tag = " [GpuVendor=" + vendor + ", provider file " + (provider ? "present" : "absent") + "]";
                    if (vendor != "intel")
                    {
                        UpscaleEngine.Plan nv = DecideOn(NeuralMachine(provider, "renderD128", "0x10de"), NeuralSettings(vendor, 0), 1280, 720, ask);
                        Equal("NVIDIA box: the ort node is the one it always was" + tag, golden, nv.NeuralFilter);
                        Check("NVIDIA box: the device is cuda, with no note" + tag, nv.NeuralDevice == "cuda" && nv.NeuralDeviceNote == null && nv.NeuralApplied);
                        Check("NVIDIA box: the chain carries the node and no device option" + tag, UpscaleEngine.BuildChain(nv).Contains("ort=model=" + X2() + ",format=yuv420p") && !UpscaleEngine.BuildChain(nv).Contains("ort=model=" + X2() + ":device"));
                    }

                    UpscaleEngine.Plan none = DecideOn(NeuralMachine(provider), NeuralSettings(vendor, 0), 1280, 720, ask);
                    Equal("no GPU: the ort node is the one it always was" + tag, golden, none.NeuralFilter);
                    Check("no GPU: the device is cuda" + tag, none.NeuralDevice == "cuda");
                }

                UpscaleEngine.Plan mixed = DecideOn(NeuralMachine(provider, "renderD128", "0x8086", "renderD129", "0x10de"), NeuralSettings("auto", 0), 1280, 720, ask);
                Equal("mixed box on auto is NVIDIA's: the ort node is the one it always was" + (provider ? " [provider file present]" : string.Empty), golden, mixed.NeuralFilter);

                foreach (string vendor in new[] { "nvidia", "none" })
                {
                    UpscaleEngine.Plan intelOnly = DecideOn(NeuralMachine(provider, "renderD128", "0x8086"), NeuralSettings(vendor, 0), 1280, 720, ask);
                    Equal("an Intel GPU the setting did not choose: the ort node is the one it always was [GpuVendor=" + vendor + "]", golden, intelOnly.NeuralFilter);
                }
            }

            // Intel, the provider installed: OpenVINO, asked for on the node and said in the record.
            UpscaleEngine.Plan ov = DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, ask);
            Equal("Intel with the provider: the node asks for openvino", "format=gbrpf32le,ort=model=" + X2() + ":device=openvino,format=yuv420p", ov.NeuralFilter);
            Check("Intel with the provider: the plan says openvino, with no note", ov.NeuralDevice == "openvino" && ov.NeuralDeviceNote == null);
            Check("Intel with the provider: the chain carries it", UpscaleEngine.BuildChain(ov).Contains("ort=model=" + X2() + ":device=openvino,format=yuv420p"));
            SessionRecord ovRecord = UpscaleEngine.Describe(null, ov, ov.Status, ov.Reason);
            Check("Intel with the provider: the record says openvino", ovRecord.NeuralDevice == "openvino" && ovRecord.NeuralDeviceNote == null);
            Check("Intel with the provider: the summary says OpenVINO on the Intel GPU, asked for, and not CUDA",
                ovRecord.Summary.Contains("OpenVINO provider on the Intel GPU (asked for, not measured for this chain") && !ovRecord.Summary.Contains("ONNX Runtime CUDA") && !ovRecord.Summary.Contains("CPU provider"), ovRecord.Summary);

            // Intel, no provider: the CPU, asked for explicitly, with the reason.
            UpscaleEngine.Plan cpu = DecideOn(NeuralMachine(false, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, ask);
            Equal("Intel without the provider: the node asks for cpu", "format=gbrpf32le,ort=model=" + X2() + ":device=cpu,format=yuv420p", cpu.NeuralFilter);
            Check("Intel without the provider: the plan says cpu, and why", cpu.NeuralDevice == "cpu" && cpu.NeuralDeviceNote != null && cpu.NeuralDeviceNote.Contains("OpenVINO provider (ort/lib/libonnxruntime_providers_openvino.so) is not installed"), cpu.NeuralDeviceNote);
            SessionRecord cpuRecord = UpscaleEngine.Describe(null, cpu, cpu.Status, cpu.Reason);
            Check("Intel without the provider: the summary says no CUDA, the CPU provider, and why",
                cpuRecord.Summary.Contains("this server's GPU is Intel, so no CUDA and the CPU provider (the OpenVINO provider (ort/lib/libonnxruntime_providers_openvino.so) is not installed"), cpuRecord.Summary);
            Check("Intel without the provider: the record carries the note", cpuRecord.NeuralDevice == "cpu" && cpuRecord.NeuralDeviceNote == cpu.NeuralDeviceNote);

            // Mixed box, Intel chosen: the Intel path, whichever node the NVIDIA card has.
            UpscaleEngine.Plan mixedIntel = DecideOn(NeuralMachine(true, "renderD128", "0x10de", "renderD129", "0x8086"), NeuralSettings("intel", 0), 1280, 720, ask);
            Check("mixed box, intel chosen, provider present: openvino", mixedIntel.NeuralDevice == "openvino" && mixedIntel.NeuralFilter.Contains(":device=openvino,"));

            // Nothing neural ran: no device, no note, nothing in the record.
            int probeCalls = 0;
            UpscaleEngine.OrtOpenVinoProbeSource = () => { probeCalls++; return true; };
            UpscaleEngine.Plan plain = DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, "upscale", "1080");
            Check("Intel, no neural level: no device and no note", !plain.NeuralApplied && plain.NeuralDevice == null && plain.NeuralDeviceNote == null);
            Equal("Intel, no neural level, no routing: the OpenVINO probe is not asked", "0", probeCalls.ToString());
            DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, ask);
            Equal("Intel, a neural level named: the probe is asked", "1", probeCalls.ToString());
            DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 480), 640, 360, "upscale", "1080");
            Equal("Intel, NeuralMaxSourceHeight set: the probe is asked", "2", probeCalls.ToString());
            UpscaleEngine.OrtOpenVinoProbeSource = () => true;
            SessionRecord plainRecord = UpscaleEngine.Describe(null, plain, plain.Status, plain.Reason);
            Check("Intel, no neural level: the record has no device", plainRecord.NeuralDevice == null && plainRecord.NeuralDeviceNote == null && plainRecord.NeuralRouting == null);

            // The filter text takes only the two values it knows: anything else is not put on a command line.
            string path = X2();
            foreach (string device in new[] { "cuda", "CPU", "openvino ", "cpu,format=yuv420p", "x:y", string.Empty, null })
            {
                string node = ShaderLibrary.NeuralFilter("realesr-anime-x2", 720, 1080, out _, NeuralSettings("auto", 0), device);
                Equal("NeuralFilter ignores the device '" + device + "'", golden, node);
            }

            Equal("NeuralFilter takes cpu", "format=gbrpf32le,ort=model=" + path + ":device=cpu,format=yuv420p", ShaderLibrary.NeuralFilter("realesr-anime-x2", 720, 1080, out _, NeuralSettings("auto", 0), "cpu"));
            Equal("NeuralFilter takes openvino", "format=gbrpf32le,ort=model=" + path + ":device=openvino,format=yuv420p", ShaderLibrary.NeuralFilter("realesr-anime-x2", 720, 1080, out _, NeuralSettings("auto", 0), "openvino"));

            // The CUDA-native levels never go through the ort filter, so they have no device.
            UpscaleSettings rig = RigSettings("auto");
            UpscaleEngine.Plan dlpp = DecideOn(NeuralMachine(false, "renderD128", "0x10de"), rig, 1280, 720, "upscale", "1080", "neural", "dlpp-2");
            Check("NVIDIA, dlpp-2: CUDA-native, no ort device", dlpp.UsesCudaNeural && dlpp.NeuralDevice == null);

            // The provider file is there, but the patched ffmpeg has to list the device too. The real probe runs
            // here against a stub binary beside the model directory, with no seam: one that lists openvino, one that does not.
            UpscaleEngine.OrtOpenVinoProbeSource = null;
            UpscaleEngine.ResetOrtProbe();
            string openvinoNode = "format=gbrpf32le,ort=model=" + X2() + ":device=openvino,format=yuv420p";
            string cpuNode = "format=gbrpf32le,ort=model=" + X2() + ":device=cpu,format=yuv420p";

            StubFfmpeg(listsOpenVino: false);
            UpscaleEngine.Plan cold = DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, ask);
            Equal("Intel, provider present, probe not answered yet: the node asks for cpu, not openvino", cpuNode, cold.NeuralFilter);
            Check("Intel, provider present, probe not answered yet: the note says it is not confirmed",
                cold.NeuralDevice == "cpu" && cold.NeuralDeviceNote != null && cold.NeuralDeviceNote.Contains("has not been confirmed"), cold.NeuralDeviceNote);
            UpscaleEngine.WarmOrtProbe().Wait();

            UpscaleEngine.Plan stale = DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, ask);
            Equal("Intel, provider present, binary without openvino: the node asks for cpu", cpuNode, stale.NeuralFilter);
            Check("Intel, provider present, binary without openvino: the note says rebuild with ORT_EP=openvino",
                stale.NeuralDevice == "cpu" && stale.NeuralDeviceNote != null && stale.NeuralDeviceNote.Contains("no openvino device") && stale.NeuralDeviceNote.Contains("ORT_EP=openvino"), stale.NeuralDeviceNote);
            SessionRecord staleRecord = UpscaleEngine.Describe(null, stale, stale.Status, stale.Reason);
            Check("Intel, binary without openvino: the summary says CPU provider and why", staleRecord.Summary.Contains("the CPU provider (the patched ffmpeg's ort filter has no openvino device"), staleRecord.Summary);
            UpscaleEngine.Plan staleRouted = DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 480), 640, 360, "upscale", "1080");
            Check("Intel, binary without openvino, 480, 360p: no network is chosen, and it says why",
                staleRouted.Act && !staleRouted.NeuralApplied && staleRouted.NeuralRouting != null
                && staleRouted.NeuralRouting.Contains("no neural level was chosen") && staleRouted.NeuralRouting.Contains("no openvino device") && staleRouted.NeuralRouting.Contains("far from real time"), staleRouted.NeuralRouting);
            UpscaleEngine.Plan nvidiaStale = DecideOn(NeuralMachine(true, "renderD128", "0x10de"), NeuralSettings("auto", 0), 1280, 720, ask);
            Equal("NVIDIA, binary without openvino: the node is the one it always was", golden, nvidiaStale.NeuralFilter);

            StubFfmpeg(listsOpenVino: true);
            UpscaleEngine.ResetOrtProbe();
            UpscaleEngine.WarmOrtProbe().Wait();
            UpscaleEngine.Plan fresh = DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, ask);
            Equal("Intel, provider present, binary lists openvino: the node asks for openvino", openvinoNode, fresh.NeuralFilter);
            Check("Intel, provider present, binary lists openvino: no note", fresh.NeuralDevice == "openvino" && fresh.NeuralDeviceNote == null);

            // Without the provider file the binary is not asked at all: the CPU, for the provider reason.
            UpscaleEngine.Plan noProvider = DecideOn(NeuralMachine(false, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, ask);
            Check("Intel, no provider file, binary lists openvino: still cpu, for the provider reason", noProvider.NeuralDevice == "cpu" && noProvider.NeuralDeviceNote.Contains("OpenVINO provider (ort/lib/libonnxruntime_providers_openvino.so) is not installed"));

            // A binary that takes seconds to answer: the session is planned at once on the CPU, with
            // the probe left running in the background, and the answer is used once it lands.
            StubFfmpeg(listsOpenVino: true, delaySeconds: 5);
            UpscaleEngine.ResetOrtProbe();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            UpscaleEngine.Plan slow = DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, ask);
            clock.Stop();
            Check("Intel, slow probe, cold cache: the plan does not wait for the binary (" + clock.ElapsedMilliseconds + " ms)", clock.ElapsedMilliseconds < 2000);
            Equal("Intel, slow probe, cold cache: the node asks for cpu", cpuNode, slow.NeuralFilter);
            Check("Intel, slow probe, cold cache: the note says it is not confirmed", slow.NeuralDevice == "cpu" && slow.NeuralDeviceNote != null && slow.NeuralDeviceNote.Contains("has not been confirmed"), slow.NeuralDeviceNote);
            UpscaleEngine.WarmOrtProbe().Wait();
            UpscaleEngine.Plan slowDone = DecideOn(NeuralMachine(true, "renderD128", "0x8086"), NeuralSettings("auto", 0), 1280, 720, ask);
            Equal("Intel, slow probe answered: the node asks for openvino", openvinoNode, slowDone.NeuralFilter);

            ResetNeuralHarness();
        }

        // A stand-in patched ffmpeg beside the model directory (where PatchedFfmpegPath looks) whose
        // `-h filter=ort` prints the device list with or without openvino, after an optional delay.
        // Moved into place, not written in place, so a process is never started from a file that is
        // still open for writing.
        private static void StubFfmpeg(bool listsOpenVino, int delaySeconds = 0)
        {
            string path = Path.Combine(NeuralRig(), "ffmpeg");
            string temp = path + ".tmp";
            string script = "#!/bin/sh\n"
                + (delaySeconds > 0 ? "sleep " + delaySeconds + "\n" : string.Empty)
                + "echo 'Filter ort'\n"
                + "echo '  device <int> ..FV...... which device to run on (from 0 to 2) (default cuda)'\n"
                + "echo '     cuda            0'\n"
                + "echo '     cpu             1'\n"
                + (listsOpenVino ? "echo '     openvino        2'\n" : string.Empty)
                + "exit 0\n";
            File.WriteAllText(temp, script);
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(temp, path, overwrite: true);
        }

        private static void ResetNeuralHarness()
        {
            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
            UpscaleEngine.IntelOnnxMeasured = false;
            UpscaleEngine.OrtOpenVinoProbeSource = null;
            UpscaleEngine.ResetOrtProbe();
            File.Delete(Path.Combine(NeuralRig(), "ffmpeg"));
            UpscaleEngine.Settings = new UpscaleSettings();
        }

        // The auto-picked level is looked up once per directory per TTL, not stat'ed on every plan.
        private static void AutoNeuralCache()
        {
            UpscaleEngine.IntelOnnxMeasured = true; // PR 5's path is what is under test; the withhold itself is tested in NvidiaOnlyLevels
            int stats = 0;
            var present = new HashSet<string>(StringComparer.Ordinal);
            long now = 1000;
            ShaderLibrary.WeightFileExistsSource = p => { stats++; return present.Contains(p); };
            ShaderLibrary.AutoNeuralClockSource = () => now;
            ShaderLibrary.ResetAutoNeuralCache();
            try
            {
                var cfg = new UpscaleSettings { NeuralModelDirectory = "/nonexistent/auto-neural-cache-rig" };
                present.Add(ShaderLibrary.NeuralModelPath("realesr-general-x4", cfg));

                string first = ShaderLibrary.AutoNeuralLevel(cfg);
                int afterFirst = stats;
                Check("auto neural cache: the first lookup stats the weights and picks the cheapest installed", first == "realesr-general-x4" && afterFirst > 0, first + " stats=" + afterFirst);

                for (int i = 0; i < 50; i++)
                {
                    ShaderLibrary.AutoNeuralLevel(cfg);
                }

                Check("auto neural cache: 50 further lookups inside the TTL stat nothing", stats == afterFirst, "stats=" + stats + " first=" + afterFirst);

                present.Add(ShaderLibrary.NeuralModelPath("realesr-anime-x2", cfg));
                Equal("auto neural cache: a weight installed inside the TTL is not seen yet", "realesr-general-x4", ShaderLibrary.AutoNeuralLevel(cfg));

                now += 31000;
                Equal("auto neural cache: past the TTL it looks again and sees it", "realesr-anime-x2", ShaderLibrary.AutoNeuralLevel(cfg));
                Check("auto neural cache: past the TTL it did stat again", stats > afterFirst);

                int before = stats;
                var other = new UpscaleSettings { NeuralModelDirectory = "/nonexistent/auto-neural-cache-other" };
                Check("auto neural cache: another directory is its own entry (none installed there)", ShaderLibrary.AutoNeuralLevel(other) == null && stats > before);

                // Through the engine: planning a small source repeatedly stats the weights once.
                before = stats;
                UpscaleEngine.EncoderListSource = () => Encoders(FullList);
                UpscaleEngine.DrmVulkanProbeSource = n => true;
                UpscaleEngine.Av1QsvProbeSource = _ => true;
                UpscaleEngine.OrtOpenVinoProbeSource = () => true;
                SetFilters("optix", "oidn", "ort", "fsr2", "dlss", "dlpp_rtcuda", "vsr_rtcuda", "libplacebo", "nlmeans_vulkan");
                var routed = new UpscaleSettings { Encoder = "auto", GpuVendor = "auto", NeuralMaxSourceHeight = 480, NeuralModelDirectory = "/nonexistent/auto-neural-cache-rig" };
                // A session that names the level never goes through the auto pick, so its stat count is
                // the cost of planning alone; an auto-picked session must cost exactly the same.
                DecideOn(NeuralMachine(false, "renderD128", "0x10de"), routed, 640, 360, "upscale", "1080", "neural", "realesr-anime-x2");
                int named = stats - before;
                bool flat = true;
                UpscaleEngine.Plan p = null;
                for (int i = 0; i < 20; i++)
                {
                    before = stats;
                    p = DecideOn(NeuralMachine(false, "renderD128", "0x10de"), routed, 640, 360, "upscale", "1080");
                    flat &= stats - before == named;
                }

                Check("auto neural cache: an auto-picked Decide stats no more files than one that names the level", flat && p.NeuralLevel == "realesr-anime-x2" && p.NeuralRouting != null && p.NeuralRouting.Contains("was chosen"), "named=" + named + " last=" + (stats - before) + " " + p.NeuralRouting);
            }
            finally
            {
                ShaderLibrary.WeightFileExistsSource = null;
                ShaderLibrary.AutoNeuralClockSource = null;
                ShaderLibrary.ResetAutoNeuralCache();
                UpscaleEngine.EncoderListSource = null;
                UpscaleEngine.DrmVulkanProbeSource = null;
                UpscaleEngine.Av1QsvProbeSource = null;
                UpscaleEngine.OrtOpenVinoProbeSource = null;
                UpscaleEngine.Settings = new UpscaleSettings();
            }
        }

        // NeuralMaxSourceHeight: what it does for each source height, and that 0 does nothing.
        private static void NeuralRouting()
        {
            UpscaleEngine.IntelOnnxMeasured = true; // PR 5's path is what is under test; the withhold itself is tested in NvidiaOnlyLevels
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.DrmVulkanProbeSource = n => true;
            UpscaleEngine.Av1QsvProbeSource = _ => true;
            UpscaleEngine.OrtOpenVinoProbeSource = () => true;
            SetFilters("optix", "oidn", "ort", "fsr2", "dlss", "dlpp_rtcuda", "vsr_rtcuda", "libplacebo", "nlmeans_vulkan");

            Equal("the setting defaults to 0 in the patcher mirror", "0", new UpscaleSettings().NeuralMaxSourceHeight.ToString());
            Equal("the setting defaults to 0 in the plugin configuration", "0", new Jellyfin.Plugin.Plus.Configuration.PluginConfiguration().NeuralMaxSourceHeight.ToString());
            var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            Equal("settings JSON without it keeps 0", "0", JsonSerializer.Deserialize<UpscaleSettings>("{\"Enabled\":true}", json).NeuralMaxSourceHeight.ToString());
            Equal("settings JSON carries it across the boundary", "480", JsonSerializer.Deserialize<UpscaleSettings>("{\"NeuralMaxSourceHeight\":480}", json).NeuralMaxSourceHeight.ToString());

            FakeGpuEnvironment nvidia() => NeuralMachine(false, "renderD128", "0x10de");
            FakeGpuEnvironment intelOv() => NeuralMachine(true, "renderD128", "0x8086");
            FakeGpuEnvironment intelNoOv() => NeuralMachine(false, "renderD128", "0x8086");

            foreach (var machine in new (string Name, System.Func<FakeGpuEnvironment> Env, string Device)[] { ("NVIDIA", nvidia, "cuda"), ("Intel with OpenVINO", intelOv, "openvino") })
            {
                string m = machine.Name + ": ";
                string dev = machine.Device;

                // 0 is no routing at all: a small source is not given a network, and a network asked for runs at any height.
                UpscaleEngine.Plan p = DecideOn(machine.Env(), NeuralSettings("auto", 0), 640, 360, "upscale", "1080");
                Check(m + "0: a 360p source is given no neural level", !p.NeuralApplied && p.NeuralRouting == null && p.NeuralRequested == "off");
                p = DecideOn(machine.Env(), NeuralSettings("auto", 0), 1920, 1080, "upscale", "2160", "neural", "realesr-anime-x2");
                Check(m + "0: a 1080p source asked for a network runs it, with nothing said about routing", p.NeuralApplied && p.NeuralLevel == "realesr-anime-x2" && p.NeuralRouting == null);

                // Below, at, and above the line, nothing asked for.
                foreach (int height in new[] { 240, 360, 480 })
                {
                    p = DecideOn(machine.Env(), NeuralSettings("auto", 480), height * 16 / 9, height, "upscale", "1080");
                    Check(m + "480: a " + height + "p source is given the neural level", p.Act && p.NeuralApplied && p.NeuralLevel == "realesr-anime-x2" && p.NeuralDevice == dev, p.Status + " " + p.Reason + " " + p.NeuralRouting);
                    Check(m + "480: a " + height + "p source: it says it was chosen, and nobody is recorded as asking", p.NeuralRequested == "off" && p.NeuralRouting != null && p.NeuralRouting.Contains("neural realesr-anime-x2 was chosen (the cheapest installed weight)") && p.NeuralRouting.Contains("NeuralMaxSourceHeight 480"), p.NeuralRouting);
                }

                foreach (int height in new[] { 481, 540, 720, 1080 })
                {
                    p = DecideOn(machine.Env(), NeuralSettings("auto", 480), height * 16 / 9, height, "upscale", "2160");
                    Check(m + "480: a " + height + "p source takes the shader path", p.Act && !p.NeuralApplied && p.NeuralFilter == null && p.NeuralRouting == null, p.Status);
                }

                // A network asked for by the session: runs at or below the line, is dropped above it, and says so.
                p = DecideOn(machine.Env(), NeuralSettings("auto", 480), 640, 360, "upscale", "1080", "neural", "realesr-general-x4");
                Check(m + "480: a 360p source that asked for general-x4 runs it, with nothing said (nothing changed)", p.NeuralApplied && p.NeuralLevel == "realesr-general-x4" && p.NeuralRouting == null);
                p = DecideOn(machine.Env(), NeuralSettings("auto", 480), 1280, 720, "upscale", "1080", "neural", "realesr-anime-x2");
                Check(m + "480: a 720p source that asked for anime-x2 does not run it", p.Act && !p.NeuralApplied && p.NeuralFilter == null);
                Equal(m + "480: a 720p source that asked for anime-x2: the reason", "neural realesr-anime-x2 NOT run: the source is 720p, taller than NeuralMaxSourceHeight 480, so the shader path ran", p.NeuralRouting);
                Equal(m + "480: a 720p source that asked for anime-x2: still recorded as asked for", "realesr-anime-x2", p.NeuralRequested);
                SessionRecord r = UpscaleEngine.Describe(null, p, p.Status, p.Reason);
                Check(m + "480: the record and its summary say it", r.NeuralLevel == "off" && r.NeuralRequested == "realesr-anime-x2" && r.NeuralDevice == null && r.Summary.Contains("neural realesr-anime-x2 NOT run: the source is 720p") && !r.Summary.Contains("Neural SR"), r.Summary);

                // The dashboard default is a network too.
                UpscaleSettings dash = NeuralSettings("auto", 480);
                dash.NeuralLevel = "realesr-general-x4";
                p = DecideOn(machine.Env(), dash, 640, 360, "upscale", "1080");
                Check(m + "480, dashboard general-x4: a 360p source runs it", p.NeuralApplied && p.NeuralLevel == "realesr-general-x4" && p.NeuralRouting == null);
                p = DecideOn(machine.Env(), dash, 1280, 720, "upscale", "1080");
                Check(m + "480, dashboard general-x4: a 720p source does not", !p.NeuralApplied && p.NeuralRouting != null && p.NeuralRouting.Contains("neural realesr-general-x4 NOT run"));

                // Not chosen for a viewer who said no, who turned the upscale off, or when there is no upscale to enhance.
                p = DecideOn(machine.Env(), NeuralSettings("auto", 480), 640, 360, "upscale", "1080", "neural", "off");
                Check(m + "480: a viewer who chose neural Off gets none", !p.NeuralApplied && p.NeuralRouting == null);
                p = DecideOn(machine.Env(), NeuralSettings("auto", 480), 640, 360, "upscale", "off");
                Check(m + "480: a viewer who chose upscale Off is not enhanced", !p.Act && !p.NeuralApplied && p.NeuralRouting == null);
                p = DecideOn(machine.Env(), NeuralSettings("auto", 480), 640, 360, "upscale", "360");
                Check(m + "480: no upscale to apply, no network chosen", !p.Act && !p.NeuralApplied && p.NeuralRouting == null, p.Status);

                // A source stored sideways is judged upright: 854x480 turned a quarter is a 854-tall picture.
                GpuDetector.UseEnvironment(machine.Env());
                UpscaleEngine.Settings = NeuralSettings("auto", 480);
                p = UpscaleEngine.Decide(SessionJobAt(854, 480, 90, "upscale", "1080"));
                Check(m + "480: 854x480 stored sideways is an 854p picture and takes the shader path", p.SourceHeight == 854 && !p.NeuralApplied && p.NeuralRouting == null);

                // The master switch wins.
                UpscaleSettings off = NeuralSettings("auto", 480);
                off.NeuralAllowed = false;
                p = DecideOn(machine.Env(), off, 640, 360, "upscale", "1080");
                Check(m + "480, neural not allowed: nothing is chosen", !p.NeuralApplied && p.NeuralRouting == null);

                // No weights installed: said, not silent, and nothing fails.
                p = DecideOn(machine.Env(), NeuralSettings("auto", 480, weights: false), 640, 360, "upscale", "1080");
                Check(m + "480, no weights: nothing is chosen, and it says why", p.Act && !p.NeuralApplied && p.NeuralRouting != null && p.NeuralRouting.Contains("none has its weights installed"), p.NeuralRouting);

                // The probe key exists only when the setting is on.
                UpscaleEngine.Settings = NeuralSettings("auto", 480);
                GpuDetector.UseEnvironment(machine.Env());
                JsonElement on = Probe();
                Check(m + "480: the probe carries NeuralMaxSourceHeight 480", on.TryGetProperty("NeuralMaxSourceHeight", out JsonElement v) && v.GetInt32() == 480);
                UpscaleEngine.Settings = NeuralSettings("auto", 0);
                JsonElement zero = Probe();
                Check(m + "0: the probe has no NeuralMaxSourceHeight key", !zero.TryGetProperty("NeuralMaxSourceHeight", out _));
            }

            // Only the ONNX levels are routed: a CUDA-native level on NVIDIA runs at any height, and says nothing.
            UpscaleSettings cudaRig = RigSettings("auto");
            cudaRig.NeuralMaxSourceHeight = 480;
            foreach (int height in new[] { 360, 1080 })
            {
                UpscaleEngine.Plan p = DecideOn(nvidia(), cudaRig, height * 16 / 9, height, "upscale", "2160", "neural", "dlpp-2");
                Check("NVIDIA, 480: dlpp-2 on a " + height + "p source is not routed", p.UsesCudaNeural && p.NeuralApplied && p.NeuralRouting == null, p.Status + " " + p.Reason);
            }

            // A game upscaler in force on NVIDIA: no network is chosen on top of it.
            UpscaleSettings game = NeuralSettings("auto", 480);
            game.GameLevel = "dlss";
            UpscaleEngine.Plan g = DecideOn(nvidia(), game, 640, 360, "upscale", "1080");
            Check("NVIDIA, 480, a game upscaler as the default: no network is chosen beside it", !g.NeuralApplied && g.NeuralRouting == null);
            g = DecideOn(nvidia(), NeuralSettings("auto", 480), 640, 360, "upscale", "1080", "game", "dlss");
            Check("NVIDIA, 480, the session asks for dlss: no network is chosen beside it", !g.NeuralApplied && g.NeuralRouting == null);

            // Intel without the provider: nothing is chosen for the viewer, said plainly; a named level still runs, on the CPU.
            UpscaleEngine.Plan q = DecideOn(intelNoOv(), NeuralSettings("auto", 480), 640, 360, "upscale", "1080");
            Check("Intel without OpenVINO, 480, 360p: no network is chosen", q.Act && !q.NeuralApplied && q.NeuralFilter == null);
            Check("Intel without OpenVINO: it says the provider is missing and that the CPU is not real time",
                q.NeuralRouting != null && q.NeuralRouting.Contains("no neural level was chosen") && q.NeuralRouting.Contains("OpenVINO provider (ort/lib/libonnxruntime_providers_openvino.so) is not installed") && q.NeuralRouting.Contains("far from real time"), q.NeuralRouting);
            SessionRecord qr = UpscaleEngine.Describe(null, q, q.Status, q.Reason);
            Check("Intel without OpenVINO: the summary carries it", qr.Summary.Contains("no neural level was chosen") && qr.NeuralRouting == q.NeuralRouting, qr.Summary);
            q = DecideOn(intelNoOv(), NeuralSettings("auto", 480), 640, 360, "upscale", "1080", "neural", "realesr-anime-x2");
            Check("Intel without OpenVINO, 480, 360p, anime-x2 asked for by name: it runs, on the CPU, with the reason",
                q.NeuralApplied && q.NeuralDevice == "cpu" && q.NeuralFilter.Contains(":device=cpu,") && q.NeuralDeviceNote != null && q.NeuralRouting == null);
            q = DecideOn(intelNoOv(), NeuralSettings("auto", 480), 1280, 720, "upscale", "1080", "neural", "realesr-anime-x2");
            Check("Intel without OpenVINO, 480, 720p, anime-x2 asked for: dropped like any other server's", !q.NeuralApplied && q.NeuralRouting != null && q.NeuralRouting.Contains("NOT run: the source is 720p"));

            // The Intel session, end to end: a 360p source, OpenVINO, the whole command line.
            UpscaleEngine.Plan e2e = DecideOn(intelOv(), NeuralSettings("auto", 480), 640, 360, "upscale", "1080");
            string chain = UpscaleEngine.BuildChain(e2e);
            Check("Intel, 480, 360p: the chain runs the network on OpenVINO before the upload", chain.Contains("format=gbrpf32le,ort=model=" + X2() + ":device=openvino,format=yuv420p") && chain.IndexOf("ort=", StringComparison.Ordinal) < chain.IndexOf("hwupload", StringComparison.Ordinal), chain);
            Check("Intel, 480, 360p: and never CUDA, nvenc or vaapi", !chain.Contains("cuda") && !chain.Contains("nvenc") && !chain.Contains("vaapi"), chain);

            ResetNeuralHarness();
        }

        private static bool WaitUntil(Func<bool> condition)
        {
            for (int i = 0; i < 100 && !condition(); i++)
            {
                System.Threading.Thread.Sleep(20);
            }

            return condition();
        }

        // Review items 1 and 2: the AV1 test encode and the encoder list are read off the request
        // path, with a time-to-live on every answer but a usable one's, pinned to the render node.
        private static void IntelProbes()
        {
            string reason;
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            UpscaleEngine.Clock = () => now;
            int runs = 0;
            string seenNode = null;
            bool? answer = false;
            var gate = new System.Threading.ManualResetEventSlim(false);
            UpscaleEngine.Av1QsvRunner = node =>
            {
                System.Threading.Interlocked.Increment(ref runs);
                seenNode = node;
                gate.Wait(5000);
                return answer;
            };

            OnIntel("auto", "auto", FullList);
            UpscaleEngine.Av1QsvProbeSource = null;
            UpscaleEngine.ResetAv1QsvProbe();

            var timer = System.Diagnostics.Stopwatch.StartNew();
            string first = UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason);
            timer.Stop();
            Check("AV1 guard returns while the probe is still running", timer.ElapsedMilliseconds < 1000 && !UpscaleEngine.Av1QsvProbeTask.IsCompleted, timer.ElapsedMilliseconds + " ms");
            Equal("no answer yet: av1_qsv is guarded as before (hevc_qsv), not left to fail at open", "hevc_qsv", first);
            Check("no answer yet: the reason says the test encode has not finished", reason != null && reason.Contains("has not finished yet"), reason);
            Equal("no answer yet, the session cannot take HEVC: h264_qsv", "h264_qsv", UpscaleEngine.GuardAv1(Job("av1", "av1", "h264"), "av1_qsv", out reason));
            Equal("no answer yet: another encoder is untouched", "hevc_qsv", UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "hevc_qsv", out reason));
            Check("no answer yet: ...and has no reason", reason == null);
            Check("the probe started in the background", WaitUntil(() => runs == 1));
            UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason);
            UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason);
            System.Threading.Thread.Sleep(50);
            Equal("a probe in flight is not started twice", "1", runs.ToString());
            Equal("the probe is pinned to the detected render node", "/dev/dri/renderD128", seenNode);

            gate.Set();
            Check("the probe finishes", UpscaleEngine.Av1QsvProbeTask.Wait(5000));
            Equal("a failed probe swaps to hevc_qsv", "hevc_qsv", UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason));
            Check("a failed probe: the reason is the test-encode failure, not 'not finished'", reason != null && reason.StartsWith("a test encode with av1_qsv failed"), reason);
            Equal("a failed probe is not re-run inside its time-to-live", "1", runs.ToString());

            now += UpscaleEngine.Av1QsvFailedTtl - TimeSpan.FromSeconds(1);
            UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason);
            UpscaleEngine.Av1QsvProbeTask.Wait(5000);
            Equal("...still not one second before it lapses", "1", runs.ToString());

            now += TimeSpan.FromSeconds(2);
            answer = true;
            Equal("past the failure time-to-live the stale answer is served while it re-asks", "hevc_qsv", UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason));
            Check("a failure is asked again", WaitUntil(() => runs == 2));
            UpscaleEngine.Av1QsvProbeTask.Wait(5000);
            Equal("the GPU recovered: av1_qsv is kept", "av1_qsv", UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason));

            now += UpscaleEngine.Av1QsvFailedTtl + TimeSpan.FromMinutes(1);
            UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason);
            UpscaleEngine.Av1QsvProbeTask.Wait(5000);
            Equal("a usable answer outlives the failure time-to-live", "2", runs.ToString());
            now += UpscaleEngine.Av1QsvUsableTtl;
            UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason);
            Check("a usable answer is re-checked eventually", WaitUntil(() => runs == 3));
            UpscaleEngine.Av1QsvProbeTask.Wait(5000);

            // An inconclusive probe (null, e.g. a timeout) is cached briefly, not forever and not never.
            UpscaleEngine.ResetAv1QsvProbe();
            runs = 0;
            answer = null;
            UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason);
            UpscaleEngine.Av1QsvProbeTask.Wait(5000);
            Equal("an inconclusive probe leaves av1_qsv alone", "av1_qsv", UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason));
            Equal("an inconclusive probe is not re-run at once", "1", runs.ToString());
            now += UpscaleEngine.Av1QsvUnknownTtl + TimeSpan.FromSeconds(1);
            answer = false;
            UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason);
            Check("an inconclusive probe is asked again after its short time-to-live", WaitUntil(() => runs == 2));
            UpscaleEngine.Av1QsvProbeTask.Wait(5000);
            Equal("...and the later answer is used", "hevc_qsv", UpscaleEngine.GuardAv1(Job("av1", "av1", "hevc"), "av1_qsv", out reason));

            // The test encode's arguments: the same QSV device a session opens (DeviceInit.QsvArgs),
            // so the probe tries the GPU the chain will use and not whichever one ffmpeg finds first.
            Equal("probe arguments without a QSV device (as before)",
                "-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:r=1 -frames:v 1 -vf format=nv12 -c:v av1_qsv -f null -",
                UpscaleEngine.Av1QsvProbeArgs(new UpscaleEngine.DeviceInit()));
            Equal("probe arguments with the render node",
                "-hide_banner -loglevel error -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD129 -f lavfi -i color=c=black:s=640x360:r=1 -frames:v 1 -vf format=nv12 -c:v av1_qsv -f null -",
                UpscaleEngine.Av1QsvProbeArgs(new UpscaleEngine.DeviceInit { Intel = true, QsvArgs = " -init_hw_device qsv=qs:hw_any,child_device=/dev/dri/renderD129" }));

            // The record and the load route read the encoder list without asking ffmpeg for it.
            // On a COLD cache (nothing probed, nothing remembered), so the test does not lean on an
            // earlier test having left the list behind: any path that asked ffmpeg would count.
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.ResetEncoderProbe();
            string family = UpscaleEngine.Capability().EncoderFamily;
            UpscaleEngine.Capability();
            Equal("Capability() on a cold cache does not read the encoder list from ffmpeg", "0", UpscaleEngine.EncoderProbeRuns.ToString());
            Equal("...and reports the unresolved family until it is warmed", "qsv", family);
            PatcherHost.GpuJson();
            Equal("PatcherHost.GpuJson (the load route's source) on a cold cache does not read it either", "0", UpscaleEngine.EncoderProbeRuns.ToString());

            // The warm-up is what reads it: on the same cold cache, WarmProbes is the one call that asks ffmpeg.
            Check("warm-up on a cold cache completes", UpscaleEngine.WarmProbes().Wait(20000));
            Equal("...and it read the encoder list from ffmpeg once", "1", UpscaleEngine.EncoderProbeRuns.ToString());
            UpscaleEngine.Av1QsvProbeTask?.Wait(5000);

            // The warm-up reads the list (off the caller) and starts the probe only when av1_qsv is listed.
            UpscaleEngine.ResetAv1QsvProbe();
            runs = 0;
            answer = true;
            UpscaleEngine.EncoderListSource = () => Encoders("hevc_qsv", "libx265");
            Check("warm-up completes", UpscaleEngine.WarmProbes().Wait(20000));
            UpscaleEngine.Av1QsvProbeTask.Wait(5000);
            Equal("warm-up: no av1_qsv listed, no AV1 test encode", "0", runs.ToString());
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            Check("warm-up completes (av1_qsv listed)", UpscaleEngine.WarmProbes().Wait(20000));
            Check("warm-up: av1_qsv listed, the AV1 test encode runs", WaitUntil(() => runs == 1));
            UpscaleEngine.Av1QsvProbeTask.Wait(5000);
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            UpscaleEngine.ResetAv1QsvProbe();
            runs = 0;
            Check("warm-up completes (NVIDIA)", UpscaleEngine.WarmProbes().Wait(20000));
            System.Threading.Thread.Sleep(50);
            Equal("warm-up on NVIDIA starts no Intel probe", "0", runs.ToString());

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
            UpscaleEngine.Av1QsvRunner = null;
            UpscaleEngine.Clock = null;
            UpscaleEngine.ResetAv1QsvProbe();
        }

        // Review item 3: a Jellyfin VA-API command line whose encoder was swapped to QSV or software
        // keeps none of the VA-API-only options. The line is reconstructed from Jellyfin's
        // EncodingHelper source (see the fixture), not captured from a running server.
        private static void VaapiStrip()
        {
            string line = Fixture("jellyfin-vaapi-commandline.txt").Trim();
            string swapped = line.Replace("-codec:v:0 hevc_vaapi", "-codec:v:0 hevc_qsv");
            string stripped = UpscaleEngine.StripVaapiOnlyArguments(swapped);

            Check("strip: the VA-API device is gone", !stripped.Contains("-init_hw_device") && !stripped.Contains("-filter_hw_device") && !stripped.Contains("-hwaccel_device"), stripped);
            Check("strip: the VA-API decode arguments are gone", !stripped.Contains("-hwaccel vaapi") && !stripped.Contains("-hwaccel_output_format"), stripped);
            Check("strip: the VA-API encoder options are gone", !stripped.Contains("-rc_mode") && !stripped.Contains("-sei") && !stripped.Contains("-a53_cc"), stripped);
            Check("strip: the encoder, bitrate, profile and GOP are kept", stripped.Contains("-codec:v:0 hevc_qsv -b:v 8000000 -maxrate 8000000 -bufsize 16000000 -profile:v:0 main -level 153 -g:v:0 72 -keyint_min:v:0 72"), stripped);
            Check("strip: the input with its spaces and quotes is kept whole", stripped.Contains("-i file:\"/media/Movie (2020)/Movie.mkv\" "), stripped);
            Check("strip: the filter chain is left to the filter hook", stripped.Contains("scale_vaapi=w=1920:h=1080"), stripped);
            Check("strip: the audio and the HLS output are kept", stripped.Contains("-codec:a:0 aac -ac 2 -b:a 384000 -f hls") && stripped.EndsWith("abc.m3u8\""), stripped);
            Check("strip: no double spaces are left behind", !stripped.Contains("  "), stripped);
            Equal("strip: the same result for a software encoder", stripped.Replace("hevc_qsv", "libx265"),
                UpscaleEngine.StripVaapiOnlyArguments(line.Replace("-codec:v:0 hevc_vaapi", "-codec:v:0 libx265")));

            Equal("strip: a line still naming a VA-API encoder is returned as it was", line, UpscaleEngine.StripVaapiOnlyArguments(line));

            string qsvNative = "-init_hw_device vaapi=va:/dev/dri/renderD128 -init_hw_device qsv=qs@va -filter_hw_device qs -hwaccel qsv -hwaccel_output_format qsv -i file:\"/m.mkv\" -codec:v:0 h264_qsv -b:v 4000000 out.m3u8";
            Equal("strip: a line that opens a QSV device is returned as it was", qsvNative, UpscaleEngine.StripVaapiOnlyArguments(qsvNative));

            string software = "-i file:\"/m.mkv\" -codec:v:0 libx264 -preset veryfast -crf 23 out.m3u8";
            Check("strip: a line with nothing to strip is the same string", ReferenceEquals(software, UpscaleEngine.StripVaapiOnlyArguments(software)));
            Equal("strip: null stays null", null, UpscaleEngine.StripVaapiOnlyArguments(null));
            Equal("strip: empty stays empty", string.Empty, UpscaleEngine.StripVaapiOnlyArguments(string.Empty));
            Equal("strip: an option left without a value is not dropped past the end", "-codec:v:0 hevc_qsv -rc_mode", UpscaleEngine.StripVaapiOnlyArguments("-codec:v:0 hevc_qsv -rc_mode"));

            // Nits: the software test is the names the chain can pick, not any "lib" encoder.
            Check("a libvpx encoder does not fit the chain", !UpscaleEngine.EncoderFitsIntelChain("libvpx-vp9") && !UpscaleEngine.EncoderFitsIntelChain("libaom-av1"));
            Check("libx264, libx265 and libsvtav1 fit the chain, in any case", UpscaleEngine.EncoderFitsIntelChain("libx264") && UpscaleEngine.EncoderFitsIntelChain("LIBX265") && UpscaleEngine.EncoderFitsIntelChain("libsvtav1"));
        }

        // A job that carries a play session id, so the encoder the patch notes can be read back.
        private static EncodingJobInfo CodecJob(string session, string negotiated, params string[] supported)
        {
            return new FakeJob
            {
                BaseRequest = new StreamingRequestDto { PlaySessionId = session, VideoCodec = negotiated },
                SupportedVideoCodecs = supported,
            };
        }

        // Review item 3: what the StartFfMpeg prefix does with a command line, with the decision
        // handed in as a ready plan. The fixture line is reconstructed from source, not captured.
        private static void StripPrefix()
        {
            string line = Fixture("jellyfin-vaapi-commandline.txt").Trim();
            string swapped = line.Replace("-codec:v:0 hevc_vaapi", "-codec:v:0 hevc_qsv");
            string expected = UpscaleEngine.StripVaapiOnlyArguments(swapped);
            UpscalePatches.PlanSource = _ => new UpscaleEngine.Plan { Act = true };

            OnIntel("auto", "auto", FullList);
            string args = swapped;
            UpscalePatches.StartFfMpegStripPrefix(Job("hevc", "hevc"), ref args);
            Check("StartFfMpeg prefix, Intel and acting: the VA-API-only options are stripped", args != swapped && args == expected && !args.Contains("-init_hw_device") && !args.Contains("-rc_mode"), args);

            UpscalePatches.PlanSource = _ => new UpscaleEngine.Plan { Act = false };
            args = swapped;
            UpscalePatches.StartFfMpegStripPrefix(Job("hevc", "hevc"), ref args);
            Equal("StartFfMpeg prefix, a session the plugin does not act on: the line is untouched", swapped, args);

            UpscalePatches.PlanSource = _ => new UpscaleEngine.Plan { Act = true };
            GpuDetector.UseEnvironment(Machine("renderD128", "0x10de"));
            args = swapped;
            UpscalePatches.StartFfMpegStripPrefix(Job("hevc", "hevc"), ref args);
            Equal("StartFfMpeg prefix on NVIDIA: the line is untouched", swapped, args);

            UpscalePatches.PlanSource = null;
            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
        }

        // Review item 4: the two places the patch leaves Jellyfin's own encoder alone because the
        // engine has none to offer. Before this, nothing reached either branch.
        private static void NullEncoderBranches()
        {
            // A stream copy that the plugin would replace, with no QSV or software encoder in ffmpeg.
            OnIntel("auto", "auto", "hevc_vaapi");
            UpscalePatches.PlanSource = _ => new UpscaleEngine.Plan { Act = true, ClientOptIn = true };
            string result = "copy";
            UpscalePatches.VideoEncoderPostfix(CodecJob("harness-copy-null", "hevc", "hevc"), ref result);
            Equal("stream copy with no usable encoder: the copy stands", "copy", result);
            Tuple<string, string> noted = UpscaleEngine.NotedEncoder("harness-copy-null");
            Check("stream copy with no usable encoder: the session notes why", noted != null && noted.Item1 == "copy" && noted.Item2.Contains("kept the stream copy"), noted == null ? "nothing noted" : noted.Item2);

            // The same copy with a QSV encoder listed IS replaced, so the null case is not a no-op for every input.
            OnIntel("auto", "auto", FullList);
            result = "copy";
            UpscalePatches.VideoEncoderPostfix(CodecJob("harness-copy-qsv", "hevc", "hevc"), ref result);
            Equal("stream copy with hevc_qsv listed: replaced by it", "hevc_qsv", result);

            // Jellyfin picked a VA-API encoder, which cannot take the chain, and the engine has nothing to put there.
            OnIntel("auto", "auto", "hevc_vaapi");
            UpscalePatches.PlanSource = _ => new UpscaleEngine.Plan { Act = true };
            result = "hevc_vaapi";
            bool handled = UpscalePatches.ApplyIntelEncoder(CodecJob("harness-vaapi-null", "hevc", "hevc"), UpscaleEngine.Settings, ref result);
            Check("Jellyfin's VA-API encoder and none to replace it: handled, and kept", handled && result == "hevc_vaapi", result);
            noted = UpscaleEngine.NotedEncoder("harness-vaapi-null");
            Check("...and the session notes that it kept Jellyfin's encoder", noted != null && noted.Item2.Contains("kept Jellyfin's hevc_vaapi"), noted == null ? "nothing noted" : noted.Item2);

            // Controls: a session the plugin does not act on is left alone without a note, and one it does act on is replaced.
            UpscalePatches.PlanSource = _ => new UpscaleEngine.Plan { Act = false };
            result = "hevc_vaapi";
            handled = UpscalePatches.ApplyIntelEncoder(CodecJob("harness-vaapi-idle", "hevc", "hevc"), UpscaleEngine.Settings, ref result);
            Check("a session the plugin does not act on: kept, no note", handled && result == "hevc_vaapi" && UpscaleEngine.NotedEncoder("harness-vaapi-idle") == null, result);
            OnIntel("auto", "auto", FullList);
            UpscalePatches.PlanSource = _ => new UpscaleEngine.Plan { Act = true };
            result = "hevc_vaapi";
            handled = UpscalePatches.ApplyIntelEncoder(CodecJob("harness-vaapi-swap", "hevc", "hevc"), UpscaleEngine.Settings, ref result);
            Check("Jellyfin's VA-API encoder with hevc_qsv listed: replaced by it", handled && result == "hevc_qsv", result);

            UpscalePatches.PlanSource = null;
            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.Av1QsvProbeSource = null;
        }

        // Review items 2 and 3: the real PatcherHost.Apply, against the real Jellyfin assemblies. It
        // installs the Harmony patches for good, so it runs last.
        private static void PatchWiring()
        {
            // Item 3: the strip prefix is bound to the real StartFfMpeg signature.
            Type manager = typeof(MediaBrowser.MediaEncoding.Transcoding.TranscodeManager);
            System.Reflection.MethodInfo start = HarmonyLib.AccessTools.Method(manager, "StartFfMpeg");
            Check("TranscodeManager.StartFfMpeg exists in the server's assembly", start != null);
            if (start == null)
            {
                return;
            }

            System.Reflection.ParameterInfo[] parameters = start.GetParameters();
            string signature = string.Join(", ", parameters.Select(p => p.ParameterType.Name + " " + p.Name));
            System.Reflection.ParameterInfo stateParam = parameters.FirstOrDefault(p => p.Name == "state");
            System.Reflection.ParameterInfo argsParam = parameters.FirstOrDefault(p => p.Name == "commandLineArguments");
            Check("StartFfMpeg takes a job state and the command-line string the prefix names",
                stateParam != null && typeof(EncodingJobInfo).IsAssignableFrom(stateParam.ParameterType) && argsParam != null && argsParam.ParameterType == typeof(string), signature);
            System.Reflection.MethodInfo prefix = typeof(UpscalePatches).GetMethod("StartFfMpegStripPrefix", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Check("the strip prefix's own parameters are all in StartFfMpeg's signature",
                prefix != null && prefix.GetParameters().All(p => parameters.Any(q => q.Name == p.Name)), signature);

            // Item 2: a cold encoder cache, an Intel server, and the entry point the plugin calls.
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.ResetEncoderProbe();
            UpscaleEngine.ResetAv1QsvProbe();
            UpscaleEngine.Av1QsvProbeSource = null;
            UpscaleEngine.Av1QsvRunner = _ => null;

            string status = null;
            try
            {
                status = PatcherHost.Apply(null, "{\"GpuVendor\":\"auto\"}");
            }
            catch (Exception ex)
            {
                Check("PatcherHost.Apply runs against the server's assemblies", false, ex.GetType().Name + ": " + ex.Message);
            }

            Check("PatcherHost.Apply starts the warm-up: the encoder list is read off the caller", WaitUntil(() => UpscaleEngine.EncoderProbeRuns > 0), "EncoderProbeRuns " + UpscaleEngine.EncoderProbeRuns);

            Check("Apply reports the patches active", status != null && status.StartsWith("active"), status);
            Check("Apply reports the strip installed, not unavailable", status != null && status.Contains("Intel VA-API argument strip") && !status.Contains("Intel VA-API argument strip (not needed"), status);
            HarmonyLib.Patches installed = HarmonyLib.Harmony.GetPatchInfo(start);
            Check("StartFfMpeg carries the strip prefix",
                installed != null && installed.Prefixes.Any(p => p.PatchMethod.Name == "StartFfMpegStripPrefix"),
                installed == null ? "no patches" : string.Join(", ", installed.Prefixes.Select(p => p.PatchMethod.Name)));

            UpscaleEngine.Av1QsvRunner = null;
            UpscaleEngine.ResetAv1QsvProbe();
            GpuDetector.UseEnvironment(null);
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
            // One xe class measured, another busy with no elapsed cycles: the unmeasured one may be the busiest.
            DrmClient xeMixedBefore = DrmFdinfo.Parse("drm-driver:\txe\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t9\ndrm-total-cycles-vcs:\t0\ndrm-cycles-vcs:\t0\ndrm-total-cycles-rcs:\t100\ndrm-cycles-rcs:\t0\n");
            DrmClient xeMixedNow = DrmFdinfo.Parse("drm-driver:\txe\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t9\ndrm-total-cycles-vcs:\t1000\ndrm-cycles-vcs:\t500\ndrm-total-cycles-rcs:\t100\ndrm-cycles-rcs:\t50\n");
            DrmLoadResult xeMixed = DrmFdinfo.Compute(new Dictionary<string, DrmClient> { [xeMixedBefore.Key] = xeMixedBefore }, new Dictionary<string, DrmClient> { [xeMixedNow.Key] = xeMixedNow }, 1.0);
            Check("an xe class with no elapsed cycles beside a measured one: the measured class is kept, the headline is null",
                xeMixed.BusyByClass.ContainsKey("video") && xeMixed.BusyByClass["video"] == 50.0 && !xeMixed.BusyByClass.ContainsKey("render") && xeMixed.BusyPercent == null,
                string.Join(",", xeMixed.BusyByClass) + " headline " + xeMixed.BusyPercent);
            Check("a counter that went backwards is skipped", DrmFdinfo.Compute(Samples("fdinfo-i915-encode-t1.txt"), Samples("fdinfo-i915-encode-t0.txt"), 1.0).VideoClients == 0);
            Check("a null sample measures nothing", DrmFdinfo.Compute(null, Samples("fdinfo-xe-t1.txt"), 1.0).BusyPercent == null);
            DrmClient hot = DrmFdinfo.Parse("drm-driver:\ti915\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t1\ndrm-engine-video:\t9000000000 ns\n");
            DrmClient hotBefore = DrmFdinfo.Parse("drm-driver:\ti915\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t1\ndrm-engine-video:\t0 ns\n");
            Check("an i915 class with no capacity line and a figure far over 100% is left out, not clamped to a false 100",
                DrmFdinfo.Compute(new Dictionary<string, DrmClient> { [hotBefore.Key] = hotBefore }, new Dictionary<string, DrmClient> { [hot.Key] = hot }, 1.0).BusyPercent == null);

            // A multi-engine class whose capacity line is missing: 1.8 s of busy time in a 1 s window.
            DrmLoadResult noCap = DrmFdinfo.Compute(Samples("fdinfo-i915-multi-nocap-t0.txt"), Samples("fdinfo-i915-multi-nocap-t1.txt"), 1.0);
            Check("i915 load: no capacity line and 180% raw is null, not 100", noCap.BusyPercent == null && !noCap.BusyByClass.ContainsKey("video"), string.Join(",", noCap.BusyByClass));
            Check("i915 load: the client still counts as an encoder session when its figure is left out", noCap.VideoClients == 1);

            // Jitter on a one-engine class (no capacity line is normal there) is clamped, not dropped.
            DrmClient jitterBefore = DrmFdinfo.Parse("drm-driver:\ti915\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t1\ndrm-engine-video:\t0 ns\n");
            DrmClient jitterNow = DrmFdinfo.Parse("drm-driver:\ti915\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t1\ndrm-engine-video:\t1050000000 ns\n");
            Check("i915 load: 105% on a class with no capacity line is jitter, clamped to 100",
                DrmFdinfo.Compute(new Dictionary<string, DrmClient> { [jitterBefore.Key] = jitterBefore }, new Dictionary<string, DrmClient> { [jitterNow.Key] = jitterNow }, 1.0).BusyPercent == 100.0);
            Check("i915 load: 50% on a class with no capacity line is reported as is",
                DrmFdinfo.Compute(
                    new Dictionary<string, DrmClient> { [jitterBefore.Key] = jitterBefore },
                    new Dictionary<string, DrmClient> { [jitterBefore.Key] = DrmFdinfo.Parse("drm-driver:\ti915\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t1\ndrm-engine-video:\t500000000 ns\n") },
                    1.0).BusyPercent == 50.0);

            // With a capacity line the figure is normalised and the old clamp stands.
            DrmClient capBefore = DrmFdinfo.Parse("drm-driver:\ti915\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t2\ndrm-engine-video:\t0 ns\ndrm-engine-capacity-video:\t2\n");
            DrmClient capNow = DrmFdinfo.Parse("drm-driver:\ti915\ndrm-pdev:0000:03:00.0\ndrm-client-id:\t2\ndrm-engine-video:\t9000000000 ns\ndrm-engine-capacity-video:\t2\n");
            Check("i915 load: with a capacity line the clamp to 100 still applies",
                DrmFdinfo.Compute(new Dictionary<string, DrmClient> { [capBefore.Key] = capBefore }, new Dictionary<string, DrmClient> { [capNow.Key] = capNow }, 1.0).BusyPercent == 100.0);
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
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.ResetEncoderProbe();
            var cold = System.Diagnostics.Stopwatch.StartNew();
            JsonElement intelCold = LoadJson(controller);
            cold.Stop();
            Equal("load route, Intel: vendor", "intel", intelCold.GetProperty("vendor").GetString());
            Check("load route, Intel: nvencSessions is null", intelCold.GetProperty("nvencSessions").ValueKind == JsonValueKind.Null);
            Check("load route, Intel: a cold poll answers at once, with no figure yet (the walk and the 300 ms gap are off the request)",
                cold.ElapsedMilliseconds < 250 && intelCold.GetProperty("encoderSessions").ValueKind == JsonValueKind.Null && intelCold.GetProperty("gpuBusyPercent").ValueKind == JsonValueKind.Null,
                cold.ElapsedMilliseconds + " ms; " + intelCold);
            Check("load route, Intel: the cold poll started a background refresh", !Jellyfin.Plugin.Plus.GpuLoadController.IntelRefreshTask.IsCompleted || Jellyfin.Plugin.Plus.GpuLoadController.IntelRefreshTask.IsCompletedSuccessfully);
            Jellyfin.Plugin.Plus.GpuLoadController.IntelRefreshTask.GetAwaiter().GetResult();
            Equal("load route, Intel, cold encoder cache: the route never ran ffmpeg for the encoder list", "0", UpscaleEngine.EncoderProbeRuns.ToString());

            // The refresh stored its figure: the next poll inside the window reads it without a new walk.
            var warm = controller.GetLoad().GetAwaiter().GetResult();
            JsonElement intel = JsonDocument.Parse(JsonSerializer.Serialize(warm.Value)).RootElement.Clone();
            if (Directory.Exists("/proc/self/fd"))
            {
                Check("load route, Intel: with no process on the GPU, zero encoder sessions (not null)", intel.GetProperty("encoderSessions").GetInt32() == 0, intel.ToString());
                Check("load route, Intel: the source is named", intel.GetProperty("loadSource").GetString().StartsWith("drm-fdinfo"), intel.ToString());

                // A figure older than 60 s is reported as unavailable, not as current.
                typeof(Jellyfin.Plugin.Plus.GpuLoadController).GetField("_intelAtUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, DateTime.UtcNow.AddSeconds(-120));
                var stale = controller.GetLoad().GetAwaiter().GetResult();
                JsonElement staleJson = JsonDocument.Parse(JsonSerializer.Serialize(stale.Value)).RootElement.Clone();
                Check("load route, Intel: a figure older than 60 s is null", staleJson.GetProperty("encoderSessions").ValueKind == JsonValueKind.Null, staleJson.ToString());
                Jellyfin.Plugin.Plus.GpuLoadController.IntelRefreshTask.GetAwaiter().GetResult();
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
                    Equal("probe: Levels.Gpu.Vendor", "none", gpu.GetProperty("Vendor").GetString());
                    Check("probe: Levels.Gpu has the summary fields",
                        SummaryFields.All(f => gpu.TryGetProperty(f, out _)));
                }

                using (JsonDocument doc = JsonDocument.Parse(PatcherHost.StatusJson()))
                {
                    JsonElement gpu = doc.RootElement.GetProperty("Gpu");
                    Equal("status: Gpu.Setting follows the setting", "none", gpu.GetProperty("Setting").GetString());
                    Check("status: Gpu keeps the full record (elevated route)",
                        SummaryFields.Concat(PrivateFields).Concat(new[] { "DecodeType" }).All(f => gpu.TryGetProperty(f, out _)));
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

            string json = JsonSerializer.Serialize(new SessionRecord { Gpu = new GpuSummary("nvidia", true, "nvenc", true, false) });
            Check("SessionRecord serialises its Gpu field", json.Contains("\"Gpu\":{") && json.Contains("\"Vendor\":\"nvidia\""), json);
        }

        private static readonly string[] SummaryFields = { "Vendor", "Detected", "EncoderFamily", "CudaOnlyLevelsAvailable", "OpenVinoAvailable" };

        private static readonly string[] PrivateFields = { "Devices", "RenderNode", "Reason", "Setting" };

        // AGENTS.md invariant 13. GET JellyfinPlus/Session/{id} is any authenticated user, so
        // neither of its two answers (the probe, a recorded session) may carry what the GPU record
        // knows about the host. The full record stays on the elevated status route only.
        private static void Access()
        {
            var full = GpuDetector.Resolve("auto", Models, Machine("renderD128", "0x10de", "renderD129", "0x8086"));
            Check("access setup: the full record does hold the private fields",
                full.Devices.Count == 2 && full.RenderNode != null && full.Reason.Length > 0 && full.Setting == "auto");

            string json = JsonSerializer.Serialize(full.ToSummary());
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                Check("summary has exactly the five non-sensitive fields",
                    doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).SequenceEqual(SummaryFields.OrderBy(n => n)), json);
            }

            // The routes are driven on a FAKE Intel machine, so a render node is really in play: on a machine
            // with no GPU (the real one) a leak of the node path has nothing to leak.
            UpscaleEngine.EncoderListSource = () => Encoders(FullList);
            UpscaleEngine.DrmVulkanProbeSource = n => true;
            GpuDetector.UseEnvironment(Machine("renderD128", "0x8086"));
            PatcherHost.Configure("{\"GpuVendor\":\"intel\",\"Encoder\":\"auto\"}");
            string sid = "harness-access-" + Guid.NewGuid().ToString("N");
            var accessPlan = new UpscaleEngine.Plan { Act = true, Status = "applied", Width = 1920, Height = 1080, UpscaleApplied = true };
            SessionRecord accessRecord = UpscaleEngine.Describe(null, accessPlan, "applied", "x");
            accessRecord.PlaySessionId = sid;
            Check("access setup: the session record does hold the render node (VulkanDevice and QsvDevice)",
                accessRecord.VulkanDevice != null && accessRecord.VulkanDevice.Contains("/dev/dri/renderD128")
                && accessRecord.QsvDevice != null && accessRecord.QsvDevice.Contains("/dev/dri/renderD128"), accessRecord.VulkanDevice + " | " + accessRecord.QsvDevice);
            UpscaleEngine.Record(accessRecord);
            foreach (string route in new[] { sid, "harness-no-such-session" })
            {
                string tag = route == sid ? "recorded session" : "probe";
                string body = PatcherHost.SessionJson(route);
                using (JsonDocument doc = JsonDocument.Parse(body))
                {
                    JsonElement root = doc.RootElement;
                    JsonElement gpu = route == sid ? root.GetProperty("Gpu") : root.GetProperty("Levels").GetProperty("Gpu");
                    Check("non-elevated " + tag + ": Gpu has the summary fields", SummaryFields.All(f => gpu.TryGetProperty(f, out _)));
                    Check("non-elevated " + tag + ": Gpu omits Devices, RenderNode, Reason and Setting",
                        PrivateFields.All(f => !gpu.TryGetProperty(f, out _)));
                    if (route == sid)
                    {
                        JsonElement rec = root.GetProperty("Record");
                        Check("non-elevated recorded session: Record.Gpu omits the private fields", PrivateFields.All(f => !rec.GetProperty("Gpu").TryGetProperty(f, out _)));
                        Check("non-elevated recorded session: Record omits VulkanDevice and QsvDevice",
                            !rec.TryGetProperty("VulkanDevice", out _) && !rec.TryGetProperty("QsvDevice", out _));
                        Check("non-elevated recorded session: the flat fields omit them too", !root.TryGetProperty("VulkanDevice", out _) && !root.TryGetProperty("QsvDevice", out _));
                    }
                }

                Check("non-elevated " + tag + ": no render node path or PCI id anywhere in the body",
                    !body.Contains("/dev/dri") && !body.Contains("renderD") && !body.Contains("0x10de") && !body.Contains("0x8086"), body);
            }

            // The elevated Status route is the one place that may carry them (Sessions), and it does.
            string status = PatcherHost.StatusJson();
            Check("the elevated Status route still carries the devices of the session", status.Contains("render node /dev/dri/renderD128"));
            using (JsonDocument doc = JsonDocument.Parse(status))
            {
                JsonElement recorded = doc.RootElement.GetProperty("Sessions").EnumerateArray().First(e => e.GetProperty("PlaySessionId").GetString() == sid);
                Check("Status Sessions: VulkanDevice and QsvDevice are there", recorded.TryGetProperty("VulkanDevice", out _) && recorded.TryGetProperty("QsvDevice", out _));
            }

            string history = JsonSerializer.Serialize(UpscaleEngine.ForSession(sid));
            Check("the history entry (served by Status) carries no device list", !history.Contains("\"Devices\"") && !history.Contains("\"RenderNode\""), history);

            GpuDetector.UseEnvironment(null);
            UpscaleEngine.EncoderListSource = null;
            UpscaleEngine.DrmVulkanProbeSource = null;
            UpscaleEngine.Settings = new UpscaleSettings();
        }

        // Review items 2 and 3: a negative or failed probe is not kept for the life of the process,
        // and the hot path never runs the probe. Driven with a fake clock and a queue standing in for
        // the thread pool, so it is deterministic.
        private static void CachedProbes()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var queue = new Queue<Action>();
            int calls = 0;
            string next = null;
            bool throwNext = false;
            var probe = new CachedProbe<string>(
                () =>
                {
                    calls++;
                    if (throwNext)
                    {
                        throw new InvalidOperationException("driver still loading");
                    }

                    return next;
                },
                v => v == "gpu",
                TimeSpan.FromSeconds(60),
                () => now,
                queue.Enqueue);

            Check("peek before any probe returns null", probe.Peek() == null);
            Check("peek schedules the probe and does not run it on the caller", calls == 0 && queue.Count == 1);
            Check("a second peek while one is scheduled does not queue another", probe.Peek() == null && queue.Count == 1);

            next = "none";
            queue.Dequeue()();
            Check("the first probe ran once, off the caller", calls == 1);
            Equal("an inconclusive answer is returned", "none", probe.Peek());
            Check("an inconclusive answer inside the retry window does not reprobe", queue.Count == 0 && calls == 1);

            now += TimeSpan.FromSeconds(61);
            Equal("after the window the stale answer is still returned", "none", probe.Peek());
            Check("after the window a reprobe is scheduled", queue.Count == 1);
            next = "gpu";
            queue.Dequeue()();
            Equal("the reprobe's conclusive answer replaces the negative one", "gpu", probe.Peek());

            now += TimeSpan.FromDays(1);
            probe.Peek();
            Check("a conclusive answer is never reprobed", queue.Count == 0 && calls == 2);

            probe.Reset();
            Check("after Reset the answer is gone and a probe is scheduled", probe.Peek() == null && queue.Count == 1);
            throwNext = true;
            queue.Dequeue()();
            Check("a throwing probe is no answer and does not escape", probe.Peek() == null && queue.Count == 0);
            now += TimeSpan.FromSeconds(61);
            probe.Peek();
            Check("a failed probe is retried after the window", queue.Count == 1);

            // A probe that lands after Reset must not stand.
            throwNext = false;
            next = "gpu";
            Action inFlight = queue.Dequeue();
            probe.Reset();
            inFlight();
            Check("a probe in flight across Reset is discarded", probe.Peek() == null);

            var overridden = GpuDetector.Resolve("none", Models, Machine("renderD128", "0x10de"));
            Check("GpuVendor=none with an NVIDIA card: the reason says the setting overrode detection",
                overridden.Vendor == "none" && overridden.Reason.Contains("overrides detection") && overridden.Reason.Contains("NVIDIA"), overridden.Reason);
            Check("GpuVendor=none with no card: the reason does not claim an override",
                !GpuDetector.Resolve("none", Models, Machine()).Reason.Contains("overrides"));
        }
    }
}
