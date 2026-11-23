using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Plus.Patcher
{
    /// <summary>
    /// The GPU vendors this plugin tells apart, and the setting values that name them. Strings,
    /// not an enum, because the value crosses the load-context boundary as JSON and is shown on
    /// the dashboard as it is stored.
    /// </summary>
    public static class GpuVendors
    {
        public const string Auto = "auto";

        public const string Nvidia = "nvidia";

        public const string Intel = "intel";

        public const string None = "none";

        public const string PciNvidia = "0x10de";

        public const string PciIntel = "0x8086";

        /// <summary>
        /// Maps a PCI vendor id as the kernel prints it (<c>0x10de</c>, with or without the prefix,
        /// any case, trailing newline included) to nvidia or intel. Null for any other vendor and
        /// for text that is not an id: an AMD or virtual GPU is not ours to claim.
        /// </summary>
        public static string FromPciId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            string v = id.Trim().ToLowerInvariant();
            if (!v.StartsWith("0x", StringComparison.Ordinal))
            {
                v = "0x" + v;
            }

            return v == PciNvidia ? Nvidia : v == PciIntel ? Intel : null;
        }

        /// <summary>
        /// The <c>GpuVendor</c> setting as one of auto, nvidia, intel, none. Anything blank or
        /// unrecognised is auto, so a hand-edited config can never leave the engine without a vendor
        /// decision; <paramref name="recognised"/> says whether the text was a real value, so the
        /// capability record can report the correction instead of hiding it.
        /// </summary>
        public static string NormaliseSetting(string setting, out bool recognised)
        {
            recognised = true;
            if (string.IsNullOrWhiteSpace(setting))
            {
                return Auto;
            }

            string s = setting.Trim().ToLowerInvariant();
            if (s == Auto || s == Nvidia || s == Intel || s == None)
            {
                return s;
            }

            recognised = false;
            return Auto;
        }
    }

    /// <summary>One render node the kernel exposes, and whose it is.</summary>
    public sealed class GpuDevice
    {
        /// <summary>The device file, for example /dev/dri/renderD128.</summary>
        public string RenderNode { get; set; }

        /// <summary>The PCI vendor id read from sysfs, as printed (0x10de).</summary>
        public string PciVendorId { get; set; }

        /// <summary>nvidia, intel, or null for a vendor this plugin does not handle.</summary>
        public string Vendor { get; set; }
    }

    /// <summary>
    /// What this server's GPU is and what the vendor-specific parts of the chain can use, as the
    /// plugin sees it. The encoder choice and the end of the chain read <see cref="Vendor"/> and
    /// <see cref="Detected"/> (docs/architecture/GPU_VENDORS.md, PR 2); the rest is still reported
    /// only. Every flag is about what was seen, not what the setting wishes: a vendor that was
    /// chosen and not found reports <see cref="Detected"/> false and no capability, and then the
    /// engine takes the NVIDIA path it always took.
    /// </summary>
    public sealed class GpuCapability
    {
        /// <summary>nvidia, intel or none: the vendor the plugin treats this server as having.</summary>
        public string Vendor { get; set; } = GpuVendors.None;

        /// <summary>The GpuVendor setting, normalised: auto, nvidia, intel or none.</summary>
        public string Setting { get; set; } = GpuVendors.Auto;

        /// <summary>True when hardware of <see cref="Vendor"/> was actually seen (a render node, or for NVIDIA an NVIDIA device in the container).</summary>
        public bool Detected { get; set; }

        /// <summary>The render node of the chosen vendor, or null: an NVIDIA GPU without nvidia_drm has none.</summary>
        public string RenderNode { get; set; }

        /// <summary>
        /// The encoder family the vendor's chain ends in: nvenc, qsv (Intel; the chain hands the
        /// encoder system-memory frames, which QSV accepts and VA-API does not), or software. The
        /// detector reports qsv for Intel; the engine downgrades it to software on a server whose
        /// ffmpeg lists no qsv encoder, because only the engine can ask ffmpeg.
        /// </summary>
        public string EncoderFamily { get; set; } = "software";

        /// <summary>
        /// The hardware decode API the vendor's chain would use once hardware decode is wired
        /// (cuda, vaapi, none). Report only: the chain still decodes in system memory, except
        /// for the CUDA-native levels on NVIDIA.
        /// </summary>
        public string DecodeType { get; set; } = "none";

        /// <summary>True when the CUDA-only levels (OptiX, DLSS, DLPP, VSR) can run: an NVIDIA GPU was seen.</summary>
        public bool CudaOnlyLevelsAvailable { get; set; }

        /// <summary>True when an Intel GPU was seen and the patched ffmpeg's ONNX Runtime ships the OpenVINO provider.</summary>
        public bool OpenVinoAvailable { get; set; }

        /// <summary>Why the plugin decided this, in a sentence the dashboard can show as it is.</summary>
        public string Reason { get; set; }

        /// <summary>Every render node seen, in node order, including vendors the plugin does not handle.</summary>
        public List<GpuDevice> Devices { get; set; } = new List<GpuDevice>();

        /// <summary>The non-sensitive subset, for every route a non-administrator can reach.</summary>
        public GpuSummary ToSummary()
            => new GpuSummary(Vendor, Detected, EncoderFamily, CudaOnlyLevelsAvailable, OpenVinoAvailable);
    }

    /// <summary>
    /// The part of <see cref="GpuCapability"/> that is safe for any authenticated user: which
    /// vendor and what its chain could use. No render-node path, PCI id, setting text or reason
    /// (AGENTS.md invariant 13). Immutable, so one instance is shared by every session record that
    /// was built under the same detection.
    /// </summary>
    public sealed class GpuSummary
    {
        public GpuSummary(string vendor, bool detected, string encoderFamily, bool cudaOnlyLevelsAvailable, bool openVinoAvailable)
        {
            Vendor = vendor;
            Detected = detected;
            EncoderFamily = encoderFamily;
            CudaOnlyLevelsAvailable = cudaOnlyLevelsAvailable;
            OpenVinoAvailable = openVinoAvailable;
        }

        public string Vendor { get; }

        public bool Detected { get; }

        public string EncoderFamily { get; }

        public bool CudaOnlyLevelsAvailable { get; }

        public bool OpenVinoAvailable { get; }
    }

    /// <summary>
    /// The three things detection asks the machine, behind an interface so the decision can be
    /// tested without a GPU. The real one reads sysfs and /dev.
    /// </summary>
    public interface IGpuEnvironment
    {
        /// <summary>Render node names that have a device behind them and can be opened, for example renderD128.</summary>
        IReadOnlyList<string> RenderNodes();

        /// <summary>The PCI vendor id text of a render node (device/vendor), or null when unreadable.</summary>
        string ReadVendorId(string node);

        /// <summary>The path to report for a node.</summary>
        string DevicePath(string node);

        /// <summary>
        /// True when this container holds an NVIDIA device (/dev/nvidiactl or /dev/nvidia[0-9]*). /proc/driver
        /// and nvidia-smi describe the host, so a pod with only an Intel GPU would wrongly count as NVIDIA.
        /// </summary>
        bool NvidiaDevicePresent();

        /// <summary>True when the file exists.</summary>
        bool FileExists(string path);
    }

    /// <summary>The real machine. Roots are parameters so a test can point them at a temp directory.</summary>
    public sealed class SystemGpuEnvironment : IGpuEnvironment
    {
        private readonly string _drmRoot;
        private readonly string _devRoot;
        private readonly string _nvidiaDevRoot;

        public SystemGpuEnvironment()
            : this("/sys/class/drm", "/dev/dri", "/dev")
        {
        }

        /// <param name="nvidiaDevRoot">Where nvidiactl and nvidia[0-9]* live (/dev).</param>
        public SystemGpuEnvironment(string drmRoot, string devRoot, string nvidiaDevRoot)
        {
            _drmRoot = drmRoot;
            _devRoot = devRoot;
            _nvidiaDevRoot = nvidiaDevRoot;
        }

        public IReadOnlyList<string> RenderNodes()
        {
            var names = new List<string>();
            try
            {
                if (!Directory.Exists(_drmRoot))
                {
                    return names;
                }

                foreach (string dir in Directory.GetDirectories(_drmRoot, "renderD*"))
                {
                    string name = Path.GetFileName(dir);

                    // sysfs lists every GPU on the host, including ones a container was never
                    // given. A node with no device file here cannot be opened by ffmpeg, so it is
                    // not a GPU this server has.
                    if (File.Exists(Path.Combine(_devRoot, name)))
                    {
                        names.Add(name);
                    }
                }
            }
            catch (Exception)
            {
                // an unreadable tree is an empty one; the NVIDIA device check still runs
            }

            // renderD128 before renderD129, numerically: the first node is the one the kernel
            // numbered first, which is the one a vendor-agnostic tool would also pick.
            return names.OrderBy(NodeNumber).ThenBy(n => n, StringComparer.Ordinal).ToList();
        }

        public string ReadVendorId(string node)
        {
            try
            {
                return File.ReadAllText(Path.Combine(_drmRoot, node, "device", "vendor")).Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string DevicePath(string node) => _devRoot.TrimEnd('/') + "/" + node;

        public bool NvidiaDevicePresent()
        {
            try
            {
                if (File.Exists(Path.Combine(_nvidiaDevRoot, "nvidiactl")))
                {
                    return true;
                }

                // nvidia[0-9]* only: nvidia-uvm and nvidia-caps are not a GPU.
                return Directory.Exists(_nvidiaDevRoot)
                    && Directory.EnumerateFileSystemEntries(_nvidiaDevRoot, "nvidia*")
                        .Select(Path.GetFileName)
                        .Any(n => n.Length > "nvidia".Length && char.IsDigit(n["nvidia".Length]));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool FileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int NodeNumber(string name)
        {
            string digits = new string(name.SkipWhile(c => !char.IsDigit(c)).ToArray());
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;
        }
    }

    /// <summary>
    /// Finds the GPU and builds the capability record. The decision is a pure function of the
    /// environment (<see cref="Resolve"/>). The machine is asked through a <see cref="CachedProbe{T}"/>:
    /// <see cref="Start"/> probes in the background at plug-in start-up, the transcode path only
    /// reads the cached answer, and an answer that found no supported GPU is retried after
    /// <see cref="RetryAfter"/> rather than kept for the life of the process (nvidia-smi can fail
    /// while the driver is still loading at boot). Never throws: a failure is a record that says
    /// none and why.
    /// </summary>
    public static class GpuDetector
    {
        /// <summary>
        /// The ONNX Runtime OpenVINO provider, beside the CUDA one the patched ffmpeg already
        /// ships (docs/filters/NEURAL.md). Relative to the directory above the model directory.
        /// </summary>
        public const string OpenVinoProviderFile = "ort/lib/libonnxruntime_providers_openvino.so";

        /// <summary>How long a "no supported GPU" answer stands before the machine is asked again.</summary>
        public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

        private static readonly object _lock = new object();
        private static IGpuEnvironment _environment;

        private static volatile CachedProbe<Inventory> _probe = NewProbe(null);

        private static Built _built;

        private static CachedProbe<Inventory> NewProbe(IGpuEnvironment environment)
            => new CachedProbe<Inventory>(
                () => Inventory.Gather(environment ?? new SystemGpuEnvironment()),
                inv => inv.AnySupportedGpu,
                RetryAfter,
                schedule: environment == null ? null : (Action<Action>)(work => work()));

        /// <summary>Begins the background probe. Called when the patches are applied, so the first session finds an answer.</summary>
        public static void Start() => _probe.Start();

        /// <summary>
        /// Points detection at another machine (a fake in the tests) and forgets what was probed.
        /// A fake is probed on the calling thread so the next read already has its answer; null
        /// goes back to the real machine and its background probe. Nothing in production calls this.
        /// </summary>
        public static void UseEnvironment(IGpuEnvironment environment)
        {
            lock (_lock)
            {
                _environment = environment;
                _probe = NewProbe(environment);
                _built = null;
            }

            if (environment != null)
            {
                _probe.Start();
            }
        }

        /// <summary>
        /// The full record for this server under the given setting: render-node paths, PCI ids, the
        /// reason. For the administrator-only status route and nothing a non-administrator can
        /// reach; use <see cref="CurrentSummary"/> there. Safe on the transcode path: it never
        /// probes or spawns, and any failure comes back as a none record.
        /// </summary>
        public static GpuCapability Current(string setting, string neuralModelDirectory)
            => Entry(setting, neuralModelDirectory)?.Capability
                ?? Failed(setting, "GPU detection failed; reported as none");

        /// <summary>The non-sensitive subset of <see cref="Current"/>, one shared instance per detection, for non-elevated routes and session records.</summary>
        public static GpuSummary CurrentSummary(string setting, string neuralModelDirectory)
            => Entry(setting, neuralModelDirectory)?.Summary
                ?? new GpuCapability().ToSummary();

        /// <summary>Forgets what was probed and built, so the next read probes again. For tests.</summary>
        public static void Reset()
        {
            _probe.Reset();
            lock (_lock)
            {
                _built = null;
            }
        }

        private static Built Entry(string setting, string neuralModelDirectory)
        {
            try
            {
                Inventory inv = _probe.Peek();
                DateTime now = DateTime.UtcNow;
                lock (_lock)
                {
                    Built b = _built;
                    if (b != null
                        && ReferenceEquals(b.Inventory, inv)
                        && b.Setting == setting
                        && b.ModelDirectory == neuralModelDirectory
                        && now < b.Expires)
                    {
                        return b;
                    }
                }

                // Expires so an OpenVINO provider installed while running is seen without a restart.
                GpuCapability cap = inv == null
                    ? Failed(setting, "GPU detection has not finished; reported as none")
                    : Build(setting, neuralModelDirectory, inv, _environment ?? new SystemGpuEnvironment());
                var built = new Built
                {
                    Inventory = inv,
                    Setting = setting,
                    ModelDirectory = neuralModelDirectory,
                    Expires = now + (inv == null ? TimeSpan.FromSeconds(1) : RetryAfter),
                    Capability = cap,
                    Summary = cap.ToSummary(),
                };
                lock (_lock)
                {
                    _built = built;
                }

                return built;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static GpuCapability Failed(string setting, string reason)
            => new GpuCapability
            {
                Setting = GpuVendors.NormaliseSetting(setting, out _),
                Reason = reason,
            };

        /// <summary>One built record and what it was built from; replaced when any of that changes.</summary>
        private sealed class Built
        {
            public Inventory Inventory { get; set; }

            public string Setting { get; set; }

            public string ModelDirectory { get; set; }

            public DateTime Expires { get; set; }

            public GpuCapability Capability { get; set; }

            public GpuSummary Summary { get; set; }
        }

        /// <summary>The decision, from an environment, with nothing cached. The tests drive this.</summary>
        public static GpuCapability Resolve(string setting, string neuralModelDirectory, IGpuEnvironment env)
            => Build(setting, neuralModelDirectory, Inventory.Gather(env), env);

        private static GpuCapability Build(string setting, string neuralModelDirectory, Inventory inv, IGpuEnvironment env)
        {
            string chosen = GpuVendors.NormaliseSetting(setting, out bool recognised);
            string note = recognised ? string.Empty : " (GpuVendor '" + setting + "' is not auto, nvidia, intel or none; treated as auto)";

            GpuDevice nvidiaNode = inv.Devices.FirstOrDefault(d => d.Vendor == GpuVendors.Nvidia);
            GpuDevice intelNode = inv.Devices.FirstOrDefault(d => d.Vendor == GpuVendors.Intel);
            bool nvidiaSeen = nvidiaNode != null || inv.NvidiaDevice;
            bool intelSeen = intelNode != null;

            string vendor;
            string reason;
            if (chosen == GpuVendors.None)
            {
                vendor = GpuVendors.None;
                reason = nvidiaSeen || intelSeen
                    ? "GpuVendor is none: the setting overrides detection (" + (nvidiaSeen ? "an NVIDIA GPU" : "an Intel GPU")
                        + (nvidiaSeen && intelSeen ? " and an Intel GPU" : string.Empty)
                        + " was found); no vendor-specific path is offered"
                    : "GpuVendor is none: no vendor-specific path is offered";
            }
            else if (chosen == GpuVendors.Nvidia)
            {
                vendor = GpuVendors.Nvidia;
                reason = nvidiaSeen
                    ? "GpuVendor is nvidia and an NVIDIA GPU was found" + WhyNvidia(nvidiaNode)
                    : "GpuVendor is nvidia but no NVIDIA GPU was found (no NVIDIA render node, no /dev/nvidiactl or /dev/nvidia[0-9]* in this container)";
            }
            else if (chosen == GpuVendors.Intel)
            {
                vendor = GpuVendors.Intel;
                reason = intelSeen
                    ? "GpuVendor is intel and an Intel render node was found"
                    : "GpuVendor is intel but no Intel render node was found";
            }
            else if (nvidiaSeen)
            {
                // NVIDIA first, so a server that already works keeps working when an Intel card
                // is added beside it: the setting that existed before this one means nvidia.
                vendor = GpuVendors.Nvidia;
                reason = "auto: NVIDIA GPU found" + WhyNvidia(nvidiaNode)
                    + (intelSeen ? "; an Intel GPU is also present and is not used (set GpuVendor to intel to choose it)" : string.Empty);
            }
            else if (intelSeen)
            {
                vendor = GpuVendors.Intel;
                reason = "auto: Intel GPU found, no NVIDIA GPU";
            }
            else
            {
                vendor = GpuVendors.None;
                reason = "auto: no NVIDIA or Intel GPU found (no usable render node, no /dev/nvidiactl or /dev/nvidia[0-9]* in this container)";
            }

            var cap = new GpuCapability
            {
                Vendor = vendor,
                Setting = chosen,
                Devices = inv.Devices.ToList(),
                Reason = reason + note,
            };

            if (vendor == GpuVendors.Nvidia)
            {
                cap.Detected = nvidiaSeen;
                cap.RenderNode = nvidiaNode?.RenderNode;
                cap.EncoderFamily = "nvenc";
                cap.DecodeType = "cuda";
                cap.CudaOnlyLevelsAvailable = nvidiaSeen;
            }
            else if (vendor == GpuVendors.Intel)
            {
                cap.Detected = intelSeen;
                cap.RenderNode = intelNode?.RenderNode;
                cap.EncoderFamily = "qsv";
                cap.DecodeType = "vaapi";
                cap.OpenVinoAvailable = intelSeen && OpenVinoProvider(neuralModelDirectory, env);
            }

            return cap;
        }

        private static string WhyNvidia(GpuDevice node)
            => node != null
                ? " (render node " + node.RenderNode + ")"
                : " (/dev/nvidia* device in the container, no render node)";

        private static bool OpenVinoProvider(string neuralModelDirectory, IGpuEnvironment env)
        {
            try
            {
                string models = string.IsNullOrWhiteSpace(neuralModelDirectory)
                    ? "/usr/lib/jellyfin-ffmpeg-oidn/models"
                    : neuralModelDirectory.Trim();
                string root = Path.GetDirectoryName(models.TrimEnd('/'));
                return !string.IsNullOrEmpty(root) && env.FileExists(Path.Combine(root, OpenVinoProviderFile));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>What the machine said, once.</summary>
        private sealed class Inventory
        {
            public List<GpuDevice> Devices { get; private set; }

            public bool NvidiaDevice { get; private set; }

            /// <summary>False means nothing usable was seen: possibly a driver still loading, so the answer is retried.</summary>
            public bool AnySupportedGpu
                => NvidiaDevice || Devices.Any(d => d.Vendor != null);

            public static Inventory Gather(IGpuEnvironment env)
            {
                var inv = new Inventory { Devices = new List<GpuDevice>() };
                foreach (string node in env.RenderNodes())
                {
                    string id = env.ReadVendorId(node);
                    inv.Devices.Add(new GpuDevice
                    {
                        RenderNode = env.DevicePath(node),
                        PciVendorId = id,
                        Vendor = GpuVendors.FromPciId(id),
                    });
                }

                inv.NvidiaDevice = env.NvidiaDevicePresent();
                return inv;
            }
        }
    }
}
