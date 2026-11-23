using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
            PciIds();
            Settings();
            Decision();
            SysfsReader();
            ReportOnly();
            Access();
            CachedProbes();

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
            Equal("auto, Intel node: encoder", "vaapi", il.EncoderFamily);
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

        // The report-only guarantee. GpuVendor is set to every value, and each of the three engine
        // outputs the NVIDIA path produces must be the exact string it produced before the setting
        // existed. The expected strings are written out by hand from the code, not computed.
        private static void ReportOnly()
        {
            foreach (string vendor in new[] { "nvidia", "auto", "intel", "none", "banana" })
            {
                UpscaleEngine.Settings = new UpscaleSettings { Encoder = "auto", GpuVendor = vendor, GpuResidentEncode = true };
                string tag = " [GpuVendor=" + vendor + "]";

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

                // EncoderFor with Encoder=auto and a request naming no codec reads no ffmpeg and
                // no session, so it is deterministic on any machine. A named encoder would probe
                // the installed ffmpeg, which is the machine's business and not this test's.
                string encoder = UpscaleEngine.EncoderFor(null, out string reason);
                Equal("EncoderFor, auto, no client codec" + tag, "hevc_nvenc", encoder);
                Equal("EncoderFor reason, auto, no client codec" + tag,
                    "Encoder set to auto and the client named no codec; using hevc_nvenc", reason);
            }

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

            string sid = "harness-access-" + Guid.NewGuid().ToString("N");
            UpscaleEngine.Record(new SessionRecord { PlaySessionId = sid, Gpu = full.ToSummary() });
            PatcherHost.Configure("{\"GpuVendor\":\"auto\"}");
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
                        JsonElement rec = root.GetProperty("Record").GetProperty("Gpu");
                        Check("non-elevated recorded session: Record.Gpu omits the private fields", PrivateFields.All(f => !rec.TryGetProperty(f, out _)));
                    }
                }

                Check("non-elevated " + tag + ": no render node path or PCI id anywhere in the body",
                    !body.Contains("/dev/dri") && !body.Contains("0x10de") && !body.Contains("0x8086"));
            }

            string history = JsonSerializer.Serialize(UpscaleEngine.ForSession(sid));
            Check("the history entry (served by Status) carries no device list or render node",
                !history.Contains("\"Devices\"") && !history.Contains("\"RenderNode\"") && !history.Contains("/dev/dri"), history);
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
