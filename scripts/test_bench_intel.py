#!/usr/bin/env python3
"""The Intel image and the 13-row matrix (docs/architecture/GPU_VENDORS.md, 5.8).

    python3 -I scripts/test_bench_intel.py

Needs no GPU, ffmpeg, docker, network or Jellyfin. It runs the real scripts/bench-intel.sh and proves:

  - --dry-run prints the whole plan from hand-written goldens: 13 rows in order, each row's must-pass
    line, the thresholds, and the exact commands of rows 2, 7 and 9; it runs nothing and writes nothing;
  - the plan has no NVIDIA-only string in any command and follows the env overrides (node, title, clips);
  - each refusal exits 2 (bad row, unknown argument, BENCH_RUNS=0, no BENCH_ASSERT_QUIET);
  - a hermetic run against stub ffmpeg/curl: row 13 is MANUAL, every row lacking an input is SKIP
    and never PASS, and results.jsonl is written;
  - runs against a fake Jellyfin (a stub curl serving JSON by path; real jq, or a stand-in when absent):
    the API rows PASS on a good server and FAIL on a failing one, the token stays off curl's command
    line, rows 1 and 12 read only the shim-log lines of this run, row 6 sets and restores the plugin
    configuration, row 5 reads the device of its own stream, the gates read HHMM as decimal;
  - scripts/plugin-meta.sh holds the plugin GUID and target ABI once (Plugin.cs and manifest.json agree);
  - docker/intel/Dockerfile keeps its pins (base digest, FSR2 commit, SDK sha512), builds the Intel
    variant, carries the acceptance checks, and its digest matches the spec page;
  - mutation: each deliberate breakage of bench-intel.sh is caught by the plan checks.

It does not build the image and does not run a row against a GPU. Exit status: 0 every check passed,
1 a check failed.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
BASH = shutil.which("bash")
BENCH = os.path.join(HERE, "bench-intel.sh")
VENDOR = os.path.join(HERE, "ffmpeg-vendor.sh")
DOCKERFILE = os.path.join(REPO, "docker", "intel", "Dockerfile")
SPEC = os.path.join(REPO, "docs", "architecture", "GPU_VENDORS.md")

FF = "/usr/lib/jellyfin-ffmpeg-oidn/ffmpeg"
NODE = "/dev/dri/renderD128"
SHADER = "/usr/share/jellyfin-shaders/FSRCNNX_x2_8-0-4-1.glsl"
INIT = f"-init_hw_device drm=dr:{NODE} -init_hw_device vulkan=vk@dr -filter_hw_device vk"
OUT = "/clips/bench-out"


def shader(w, h):
    return f"format=yuv420p,hwupload,libplacebo=w={w}:h={h}:upscaler=ewa_lanczos:custom_shader_path={SHADER},hwdownload,format=yuv420p"


def bicubic(w, h):
    return f"scale={w}:{h}:flags=bicubic,format=yuv420p"


FSR2_CHAIN = ("format=yuv420p,format=gbrpf32le,fsr2=w=1920:h=1080,format=yuv420p,hwupload,"
              "libplacebo=w=1920:h=1080,hwdownload,format=yuv420p")
HWDEC_CHAIN = (f"setparams=range=tv,hwmap=derive_device=vulkan,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos:"
               f"custom_shader_path={SHADER},hwdownload,format=nv12")


def run_cmd(clip, chain, out=None):
    tail = f"-c:v ffv1 {out}" if out else "-f null -"
    return f"{FF} -hide_banner -nostdin -y -benchmark -stats {INIT} -i {clip} -vf {chain} -an {tail}"


TITLES = {
    1: "Vendor record",
    2: "FSRCNNX 360p to 1080p, software decode",
    3: "FSRCNNX 720p to 1080p",
    4: "FSRCNNX 1080p to 2160p",
    5: "Neural (ORT on OpenVINO), 360p and 720p to 1080p",
    6: "Routing by source height",
    7: "fsr2 without CUDA, still pixels",
    8: "fsr2 without CUDA, moving content",
    9: "Hardware decode off/on",
    10: "QSV encode",
    11: "Contention with one Plex hardware transcode",
    12: "Client, headless",
    13: "Client, human",
}

# A fragment each row's must-pass line has to carry (the figures the owner's decision names).
MUST = {
    1: ["vendor=intel", "Vulkan vk@dr", "no nvenc/cuda string"],
    2: ["fps>=100", "bicubic+0.5dB"],
    3: ["fps>=1.2x source fps", "bicubic+0.3dB"],
    4: ["scaler-only fps>=34", "QSV encode >=1.2x", "bicubic+0.3dB"],
    5: ["device=openvino", "slower than real time is a valid result"],
    6: ["NeuralMaxSourceHeight=480", "shader path"],
    7: ["ceil(8*scale^2)", "<=0.5", "<=16 in 99%", "control exactly 0"],
    8: ["panning cut >= bicubic", "not a blocker"],
    9: ["-hwaccel vaapi", "hwmap=derive_device=vulkan", "Conversion failed!", ">=30% lower", ">=50dB"],
    10: ["h264_qsv/hevc_qsv", "encoderSessions", "+-10 points"],
    11: ["fps>=36", "Plex hardware transcode"],
    12: ["target height", "QSV codec"],
    13: ["window.__jellyfinPlus.installed===true", "no stall over 2s"],
}

THRESHOLD = {
    2: "plan: row 2 threshold: fps>=100 xpsnr>=+0.5",
    3: "plan: row 3 threshold: e2e>=1.2x xpsnr>=+0.3",
    4: "plan: row 4 threshold: fps>=34 e2e>=1.2x xpsnr>=+0.3",
    5: "plan: row 5 threshold: device=openvino; largest NeuralMaxSourceHeight holding >=1.2x",
    7: "plan: row 7 threshold: mean<=0.5 peak<=16 in 99% control==0",
    8: "plan: row 8 threshold: xpsnr(fsr2)>=xpsnr(bicubic)",
    9: "plan: row 9 threshold: cpu gain>=30% xpsnr(on,off)>=50dB no 'Conversion failed!'",
    10: "plan: row 10 threshold: |load - fdinfo| <= 10 points",
    11: "plan: row 11 threshold: fps>=36 with BENCH_PLEX_TRANSCODING=1",
}

EXACT = [
    "plan: bench-intel: rows=1 2 3 4 5 6 7 8 9 10 11 12 13 runs=3 mode=dry-run",
    "plan: required filters: oidn ort fsr2",
    "plan: row 2 cmd: " + run_cmd("/clips/tears_360p.mkv", shader(1920, 1080)),
    "plan: row 2 cmd: " + run_cmd("/clips/tears_360p.mkv", bicubic(1920, 1080)),
    "plan: row 4 cmd: " + run_cmd("/clips/tears_1080p.mkv", shader(3840, 2160)),
    "plan: row 7 cmd: " + (f"{FF} -hide_banner -nostdin -y {INIT} -loop 1 -framerate 24 -t 10 -i /clips/still_720p.png "
                           f"-vf {FSR2_CHAIN},tblend=all_mode=difference,signalstats,"
                           f"metadata=print:key=lavfi.signalstats.YAVG:file={OUT}/still-fsr2.txt -an -f null -"),
    "plan: row 7 skip frames: 3x=72 2x=32 1.5x=18",
    "plan: row 9 cmd: " + (f"{FF} -hide_banner -nostdin -y -benchmark -stats -init_hw_device vaapi=va:{NODE} "
                           f"-init_hw_device vulkan=vk@va -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device vk "
                           f"-i /clips/tears_1080p.mkv -vf {HWDEC_CHAIN} -an -c:v ffv1 {OUT}/hwdec-on.mkv"),
    "plan: row 9 cmd: " + run_cmd("/clips/tears_1080p.mkv", shader(1920, 1080), f"{OUT}/hwdec-off.mkv"),
    "plan: row 13: MANUAL, the owner's browser check over a port-forward",
]

FAILS = []
PASSES = 0


def check(name, ok, detail=""):
    global PASSES
    if ok:
        PASSES += 1
    else:
        FAILS.append(name)
        print(f"FAIL {name} {detail}".rstrip())


def clean_env(extra=None, path=None):
    env = {k: v for k, v in os.environ.items() if not k.startswith("BENCH_")}
    env["PATH"] = (path + os.pathsep if path else "") + os.environ.get("PATH", "/usr/bin:/bin")
    env["LC_ALL"] = "C"
    env.update(extra or {})
    return env


def run(script, args, env=None):
    p = subprocess.run([BASH, script, *args], capture_output=True, text=True, env=env or clean_env(), timeout=60)
    return p.returncode, p.stdout, p.stderr


def title_rows(lines):
    """The row numbers of the `plan: row N: <title>` lines (not the MANUAL line of row 13)."""
    return [int(m.group(1)) for m in (re.match(r"plan: row (\d+): (?!MANUAL)", l) for l in lines) if m]


def plan_failures(text):
    """Everything wrong with a default dry-run output, as a list of names (empty = the golden holds)."""
    bad = []
    lines = text.splitlines()
    for want in EXACT:
        if want not in lines:
            bad.append("missing:" + want[:70])
    rows = title_rows(lines)
    if rows != list(range(1, 14)):
        bad.append(f"rows:{rows}")
    for n, title in TITLES.items():
        if f"plan: row {n}: {title}" not in lines:
            bad.append(f"title:{n}")
    for n, frags in MUST.items():
        got = [l for l in lines if l.startswith(f"plan: row {n} must pass: ")]
        if len(got) != 1 or not all(f in got[0] for f in frags):
            bad.append(f"must:{n}")
    for n, want in THRESHOLD.items():
        if want not in lines:
            bad.append(f"threshold:{n}")
    for l in lines:
        if " cmd: " in l and re.search(r"nvenc|cuda|cuvid|nvdec", l, re.I):
            bad.append("nvidia-in-cmd")
    if f"drm=dr:{NODE}" not in text:
        bad.append("no-drm-device")
    return bad


def mutant_dir(tmp, name, old, new):
    src = open(BENCH).read()
    assert old in src, f"mutant {name}: pattern not in script"
    d = os.path.join(tmp, name)
    os.makedirs(d)
    shutil.copy(VENDOR, d)
    path = os.path.join(d, "bench-intel.sh")
    open(path, "w").write(src.replace(old, new, 1))
    return path


def test_plan_and_refusals(tmp):
    rc, out, err = run(BENCH, ["--dry-run"])
    check("dry-run exits 0", rc == 0, err)
    check("dry-run plan matches the golden", not plan_failures(out), str(plan_failures(out)))
    check("--plan is --dry-run", run(BENCH, ["--plan"])[1] == out)

    rc, out2, _ = run(BENCH, ["--dry-run"], clean_env({"BENCH_FFMPEG": "/nonexistent/ffmpeg", "BENCH_OUT": os.path.join(tmp, "never")}))
    check("dry-run needs no binary", rc == 0 and "/nonexistent/ffmpeg" in out2)
    check("dry-run creates nothing", not os.path.exists(os.path.join(tmp, "never")))

    rc, out3, _ = run(BENCH, ["--dry-run", "--rows", "2,7"])
    got = title_rows(out3.splitlines())
    check("--rows limits the plan", rc == 0 and got == [2, 7], str(got))

    env = clean_env({"BENCH_RENDER_NODE": "/dev/dri/renderD129", "BENCH_TITLE": "anim", "BENCH_CLIPS": "/data"})
    rc, out4, _ = run(BENCH, ["--dry-run"], env)
    check("env overrides reach the commands",
          "drm=dr:/dev/dri/renderD129" in out4 and "/data/anim_360p.mkv" in out4 and "renderD128" not in out4 and "/clips" not in out4)

    rc, out5, _ = run(BENCH, ["--list"])
    check("--list names 13 rows", rc == 0 and len(out5.splitlines()) == 13)

    for name, args, extra in [
        ("a row past 13", ["--dry-run", "--rows", "14"], None),
        ("an unknown argument", ["--frobnicate"], None),
        ("BENCH_RUNS=0", ["--dry-run"], {"BENCH_RUNS": "0"}),
        ("a real run without BENCH_ASSERT_QUIET", ["--rows", "13"], None),
    ]:
        rc, _, err = run(BENCH, args, clean_env(extra))
        check(f"{name} exits 2", rc == 2, f"rc={rc} {err}")
    rc, _, err = run(BENCH, ["--rows", "13"])
    check("the gate refusal says gate", "gate:" in err, err)


JQ = shutil.which("jq")
REAL_DATE = shutil.which("date")
TOKEN = "tok-SECRET-4242"

# Used only when this machine has no jq: it answers the exact filters bench-intel.sh sends and nothing
# else, so a filter the script changes is a failure here, not a silent pass. Real jq is preferred.
FAKE_JQ = r'''#!@PY@
import json, re, sys

args = sys.argv[1:]
raw = compact = null_in = False
named, rest, i = {}, [], 0
while i < len(args):
    a = args[i]
    if a in ("-r", "--raw-output"):
        raw = True
    elif a in ("-c", "--compact-output"):
        compact = True
    elif a in ("-n", "--null-input"):
        null_in = True
    elif a in ("-nc", "-cn"):
        null_in = compact = True
    elif a == "--arg":
        named[args[i + 1]] = args[i + 2]
        i += 2
    elif a == "--argjson":
        named[args[i + 1]] = json.loads(args[i + 2])
        i += 2
    else:
        rest.append(a)
    i += 1
flt = rest[0]
data = None
if not null_in:
    text = sys.stdin.read()
    if not text.strip():
        sys.exit(0)
    data = json.loads(text)


def path(d, p):
    for k in [k for k in p.split(".") if k]:
        d = d.get(k) if isinstance(d, dict) else None
    return d


def emit(v):
    if raw and isinstance(v, str):
        print(v)
    else:
        print(json.dumps(v, separators=(",", ":")) if compact else json.dumps(v))


m_alt = re.fullmatch(r"(\.[A-Za-z.]+) // (.+)", flt)
m_ne = re.fullmatch(r"(\.[A-Za-z.]+) != false", flt)
m_path = re.fullmatch(r"\.[A-Za-z.]+", flt)
if flt == "{row:$row,status:$status,detail:$detail}":
    emit({"row": named["row"], "status": named["status"], "detail": named["detail"]})
elif flt == '[.Levels.Denoise[]?, .Levels.Neural[]?, .Levels.Game[]?] | join(" ")':
    lv = data.get("Levels") or {}
    emit(" ".join(lv.get("Denoise", []) + lv.get("Neural", []) + lv.get("Game", [])))
elif flt == 'first(.Items[] | select(.Path | endswith("/" + $n)) | .Id) // empty':
    for it in data.get("Items", []):
        if it["Path"].endswith("/" + named["n"]):
            emit(it["Id"])
            break
elif flt == 'first(.[] | select(.Name == "Jellyfin Plus") | .Id) // empty':
    for it in data:
        if it["Name"] == "Jellyfin Plus":
            emit(it["Id"])
            break
elif flt == ".NeuralMaxSourceHeight = 480 | .NeuralAllowed = true":
    data["NeuralMaxSourceHeight"] = 480
    data["NeuralAllowed"] = True
    emit(data)
elif m_ne:
    emit(path(data, m_ne.group(1)) is not False)
elif m_alt:
    v, d = path(data, m_alt.group(1)), m_alt.group(2).strip()
    if v is None or v is False:
        if d != "empty":
            emit(json.loads(d))
    else:
        emit(v)
elif m_path:
    emit(path(data, flt))
else:
    sys.stderr.write("fake jq: unsupported filter: %s\n" % flt)
    sys.exit(3)
'''

# The filter list is followed by thousands of lines, as the real binary's is: a `| grep -q` that quits at
# its match then SIGPIPEs the writer, and under pipefail that reads as "no such filter".
FFMPEG_STUB = r'''#!/bin/sh
case "$*" in
*-filters*)
    for f in oidn ort fsr2 libplacebo xpsnr; do echo " T.. $f  V->V stub"; done
    i=0; while [ $i -lt 4000 ]; do echo " T.. pad$i  V->V padding so the pipe fills"; i=$((i + 1)); done
    exit 0;;
*xpsnr=*)
    case "$*" in *shader.mkv*) echo "XPSNR y: 40.0 u: 40.0 v: 40.0" >&2;; *) echo "XPSNR y: 30.0 u: 30.0 v: 30.0" >&2;; esac
    exit 0;;
*-benchmark*)
    [ "${FAKE_FFMPEG_FAIL:-}" = 1 ] && exit 1
    printf 'frame=  240 fps=120\rbench: utime=1.0s stime=0.1s rtime=2.0s\n' >&2;;
esac
exit 0
'''

FFPROBE_STUB = '#!/bin/sh\ncase "$*" in *r_frame_rate*) echo 24/1;; *height,codec_name*) echo h264,1080;; *) echo h264;; esac\n'

# `date +%H%M` is the only thing it fakes (FAKE_HM); everything else is the real date. With FAKE_TZ_LOG set
# it appends the TZ the script called it with, so a test can see which zone the gate asked for.
DATE_STUB = ('#!/bin/sh\nfor a in "$@"; do\n  if [ "$a" = "+%H%M" ] && [ -n "${FAKE_HM:-}" ]; then\n'
             '    [ -z "${FAKE_TZ_LOG:-}" ] || echo "${TZ:-}" >> "$FAKE_TZ_LOG"\n    echo "$FAKE_HM"; exit 0\n  fi\n'
             'done\nexec "$REAL_DATE" "$@"\n')

# A fake Jellyfin: serves JSON by URL path from $FAKE_DIR/scenario.json, logs every call (argv, stdin,
# POST body) to $FAKE_DIR/log.jsonl, and keeps the plugin configuration and started sessions in state.json.
FAKE_CURL = r'''#!@PY@
import json, os, sys, urllib.parse

d = os.environ["FAKE_DIR"]
argv = sys.argv[1:]
sc = json.load(open(os.path.join(d, "scenario.json")))
sp = os.path.join(d, "state.json")
st = json.load(open(sp)) if os.path.exists(sp) else {"config": sc["config"], "sessions": {}}
stdin = ""
if "--config" in argv and argv[argv.index("--config") + 1] == "-":
    stdin = sys.stdin.read()
url = next((a for a in argv if a.startswith("http")), "")
body = ""
for i, a in enumerate(argv):
    if a in ("-d", "--data", "--data-binary") and argv[i + 1].startswith("@"):
        body = open(argv[i + 1][1:]).read()
with open(os.path.join(d, "log.jsonl"), "a") as f:
    f.write(json.dumps({"argv": argv, "stdin": stdin, "url": url, "body": body}) + "\n")
u = urllib.parse.urlparse(url)
path, q = u.path, urllib.parse.parse_qs(u.query)


def out(obj, raw=False):
    sys.stdout.write(obj if raw else json.dumps(obj))
    sys.exit(0)


def fail():
    sys.stderr.write("curl: (22) The requested URL returned error: 404\n")
    sys.exit(22)


def shimlog(text):
    p = os.environ.get("FAKE_SHIM_LOG")
    if p and text:
        open(p, "a").write(text + "\n")


def save():
    json.dump(st, open(sp, "w"))


if path == "/System/Info/Public":
    out({"ServerName": "fake"})
if sc.get("fail_all"):
    fail()
cfg_max = st["config"].get("NeuralMaxSourceHeight", 0)
if path == "/Plugins":
    out([{"Name": "Other", "Id": "0000"}, {"Name": "Jellyfin Plus", "Id": "plugin-guid"}])
if path.startswith("/Plugins/") and path.endswith("/Configuration"):
    if "-X" in argv and argv[argv.index("-X") + 1] == "POST":
        st["config"] = json.loads(body)
        save()
        sys.exit(0)
    out(st["config"])
if path == "/JellyfinPlus/Status":
    out({"Gpu": {"Vendor": "intel", "RenderNode": "/dev/dri/renderD128"}})
if path == "/JellyfinPlus/Gpu/Load":
    out({"gpuBusyPercent": 12})
if path == "/Items":
    out({"Items": [{"Id": "id%d" % h, "Path": "/media/tears_%dp.mkv" % h} for h in (360, 720, 1080, 2160)]})
if path.startswith("/JellyfinPlus/Session/"):
    sid = path.rsplit("/", 1)[1]
    if sid == "bench-probe":
        shimlog(sc.get("probe_logs"))
    s = st["sessions"].get(sid)
    if s is None:
        levels = {"Denoise": ["off", "nlmeans"], "Neural": ["off", "realesr-anime-x2"], "Game": ["off", "fsr2"]}
        if cfg_max > 0 and sc.get("config_applies", True):
            levels["NeuralMaxSourceHeight"] = cfg_max
        out({"Known": False, "Levels": levels})
    routing = None
    if cfg_max > 0 and s["neural"] and s["src_height"] > cfg_max:
        routing = "neural %s NOT run: the source is %dp, taller than NeuralMaxSourceHeight %d, so the shader path ran" % (
            s["neural"], s["src_height"], cfg_max)
    out({"Known": True, "SourceHeight": s["src_height"], "NeuralApplied": sc.get("applied", False),
         "NeuralDevice": sc.get("device", "openvino"), "NeuralRouting": sc["routing"] if "routing" in sc else routing})
if path.endswith("/master.m3u8"):
    if sc.get("fail_master"):
        fail()
    sid = q["PlaySessionId"][0]
    st["sessions"][sid] = {"src_height": int(path.split("/")[2][2:]), "neural": q.get("neural", [""])[0]}
    save()
    shimlog(sc.get("stream_logs"))
    out("#EXTM3U\nmain.m3u8?PlaySessionId=" + sid + "\n", raw=True)
if path.endswith("/main.m3u8"):
    out("#EXTM3U\n" + "".join("hls1/main/%d.mp4\n" % i for i in range(7)), raw=True)
if "/hls1/main/" in path:
    if sc.get("fail_segments"):
        fail()
    out("segment-bytes", raw=True)
fail()
'''

JSON_ROW = re.compile(r"^result: row (\d+) (\w+) ?(.*)$", re.M)


def results(out):
    """row number -> (status, detail) from the `result:` lines of a run."""
    return {int(n): (s, d) for n, s, d in JSON_ROW.findall(out)}


def stub_bin(tmp, fake_api=False):
    """Stub tools; jq is the real one (the script writes JSONL with it). fake_api swaps curl for FAKE_CURL."""
    d = os.path.join(tmp, "stubs")
    os.makedirs(d)
    bodies = {
        "ffmpeg": FFMPEG_STUB,
        "ffprobe": FFPROBE_STUB,
        "date": DATE_STUB,
        "curl": FAKE_CURL.replace("@PY@", sys.executable) if fake_api else "#!/bin/sh\nexit 22\n",
    }
    for name, body in bodies.items():
        p = os.path.join(d, name)
        open(p, "w").write(body)
        os.chmod(p, 0o755)
    if JQ:
        os.symlink(JQ, os.path.join(d, "jq"))
    else:
        p = os.path.join(d, "jq")
        open(p, "w").write(FAKE_JQ.replace("@PY@", sys.executable))
        os.chmod(p, 0o755)
    open(os.path.join(d, "fsr2-nocuda"), "w").close()
    return d


class World:
    """One hermetic bench run: stub tools, a clips dir, a fake Jellyfin with a scenario, a shim log."""

    def __init__(self, tmp, name, scenario=None, extra=None):
        self.root = os.path.join(tmp, name)
        os.makedirs(self.root)
        self.stubs = stub_bin(self.root, fake_api=True)
        self.clips = os.path.join(self.root, "clips")
        self.out = os.path.join(self.root, "out")
        self.fake = os.path.join(self.root, "fake")
        self.shim = os.path.join(self.root, "shim.log")
        os.makedirs(self.clips)
        os.makedirs(self.fake)
        open(self.shim, "w").close()
        sc = {"config": {"NeuralAllowed": True, "NeuralMaxSourceHeight": 0, "Keep": "me"}}
        sc.update(scenario or {})
        self.config0 = dict(sc["config"])
        json.dump(sc, open(os.path.join(self.fake, "scenario.json"), "w"))
        ff = os.path.join(self.stubs, "ffmpeg")
        self.env = clean_env(dict({
            "BENCH_FFMPEG": ff, "BENCH_STOCK_FFMPEG": ff, "BENCH_CLIPS": self.clips, "BENCH_OUT": self.out,
            "BENCH_JELLYFIN_URL": "http://jf.test", "BENCH_JELLYFIN_TOKEN": TOKEN, "BENCH_SHIM_LOG": self.shim,
            "BENCH_RUNS": "1", "BENCH_CONFIRM_NO_GATES": "1", "FAKE_DIR": self.fake, "FAKE_SHIM_LOG": self.shim,
            "REAL_DATE": REAL_DATE or "date"}, **(extra or {})), path=self.stubs)

    def run(self, args, script=BENCH, **extra):
        # the quiet-box gates read the real clock and /proc: the gate tests call run() themselves
        return run(script, ["--no-gates", *args], dict(self.env, **extra))

    def log(self):
        p = os.path.join(self.fake, "log.jsonl")
        return [json.loads(l) for l in open(p)] if os.path.exists(p) else []

    def urls(self):
        return [e["url"] for e in self.log() if e["url"]]

    def posts(self):
        return [json.loads(e["body"]) for e in self.log() if "POST" in e["argv"]]

    def jsonl(self):
        p = os.path.join(self.out, "results.jsonl")
        return open(p).read().splitlines() if os.path.exists(p) else []


def test_stub_run(tmp):
    stubs = stub_bin(tmp)
    clips = os.path.join(tmp, "clips")
    out = os.path.join(tmp, "out")
    os.makedirs(clips)
    env = clean_env({"BENCH_FFMPEG": os.path.join(stubs, "ffmpeg"), "BENCH_STOCK_FFMPEG": os.path.join(stubs, "ffmpeg"),
                     "BENCH_CLIPS": clips, "BENCH_OUT": out, "BENCH_CONFIRM_NO_GATES": "1"}, path=stubs)
    rc, so, err = run(BENCH, ["--no-gates", "--rows", "1,5,6,7,8,10,11,12,13"], env)
    status = dict(re.findall(r"^result: row (\d+) (\w+)", so, re.M))
    check("stub run exits 0", rc == 0, f"rc={rc} {err}")
    check("row 13 is MANUAL", status.get("13") == "MANUAL", so)
    skipped = [r for r in "1 5 6 7 8 10 11 12".split() if status.get(r) == "SKIP"]
    check("rows without an input are SKIP, never PASS", len(skipped) == 8, f"{status}")
    check("no PASS without evidence", "PASS" not in status.values())
    jl = os.path.join(out, "results.jsonl")
    check("results.jsonl has one line per row", os.path.isfile(jl) and len(open(jl).read().splitlines()) == 9)

    # a binary lacking a required filter is refused, not run
    bare = os.path.join(tmp, "bare")
    os.makedirs(bare)
    p = os.path.join(bare, "ffmpeg")
    open(p, "w").write("#!/bin/sh\nexit 0\n")
    os.chmod(p, 0o755)
    for n in ("ffprobe", "curl"):
        shutil.copy(os.path.join(stubs, n), bare)
    env2 = dict(env, BENCH_FFMPEG=p, BENCH_STOCK_FFMPEG=p, PATH=bare + os.pathsep + env["PATH"])
    rc, _, err = run(BENCH, ["--no-gates", "--rows", "13"], env2)
    check("a binary without the Intel filters is refused", rc == 2 and "not the Intel build" in err, f"rc={rc} {err}")


def test_mutants(tmp):
    for name, old, new in [
        ("threshold-fps", "FPS_R2=100", "FPS_R2=10"),
        ("threshold-peak", "STILL_PEAK_MAX=16", "STILL_PEAK_MAX=32"),
        ("nvidia-device", 'INIT=(-init_hw_device "drm=dr:$NODE"', 'INIT=(-init_hw_device "cuda=cu:0"'),
        ("row-dropped", "ALL_ROWS=(1 2 3 4 5 6 7 8 9 10 11 12 13)", "ALL_ROWS=(1 2 3 4 5 6 7 8 10 11 12 13)"),
        ("skip-frames", "v = 8 * s * s", "v = 4 * s * s"),
        ("row-order", "ALL_ROWS=(1 2 3 4 5 6 7 8 9 10 11 12 13)", "ALL_ROWS=(2 1 3 4 5 6 7 8 9 10 11 12 13)"),
        ("title", '"Vendor record"', '"Vendor"'),
    ]:
        path = mutant_dir(tmp, name, old, new)
        rc, out, _ = run(path, ["--dry-run"])
        check(f"mutant {name} is caught", rc != 0 or bool(plan_failures(out)))

    for name, old, new, fn in [
        ("octal-hhmm", 'hm=$((10#$(TZ="$BLOCK_TZ" date +%H%M)))', 'hm=$(TZ="$BLOCK_TZ" date +%H%M)', gate_failures),
        ("octal-window-start", "lo=$((10#${item%-*}))", "lo=$((${item%-*}))", gate_failures),
        ("octal-window-end", "hi=$((10#${item#*-}))", "hi=$((${item#*-}))", gate_failures),
        ("window-end-inclusive", "hm < BLOCK_HI[i]", "hm <= BLOCK_HI[i]", gate_failures),
        ("tz-hard-coded", 'TZ="$BLOCK_TZ" date', "TZ=Australia/Melbourne date", window_failures),
        ("windows-ignored", 'BLOCK_WINDOWS="${BENCH_BLOCK_WINDOWS:-0100-0700,1630-1830}"', 'BLOCK_WINDOWS="0100-0700,1630-1830"', window_failures),
        ("malformed-accepted", "^[0-9]{4}-[0-9]{4}(,[0-9]{4}-[0-9]{4})*$", "^[0-9-]+(,[0-9-]+)*$", window_failures),
        ("run-fps-lost-failure", '|| { echo 0; rm -f "$log"; return 1; }', "|| true", runfps_failures),
        ("token-on-argv", 'curl -fsS --config - "$@" "$JF_URL$p"', 'curl -fsS -H "X-Emby-Token: $JF_TOKEN" "$@" "$JF_URL$p"', token_failures),
    ]:
        path = mutant_dir(tmp, name, old, new)
        check(f"mutant {name} is caught by its behaviour check", bool(fn(path, tmp, "mut-" + name)))


def build_apt_checks(df):
    """The build stage's apt line carries every dev package build-ffmpeg.sh installs for the Intel build.

    The FSR2 compile runs before the script's own apt step, and a header missing there (vulkan/vulkan.h)
    failed the first CI build.
    """
    stage = re.search(r"^FROM \S+ AS build\n(.*?)(?=^FROM )", df, re.M | re.S)
    check("the Dockerfile has a build stage", bool(stage))
    body = stage.group(1) if stage else ""
    apt = re.search(r"apt-get install[^\n]*?--no-install-recommends(.*?)&&\s*rm -rf", body, re.S)
    pkgs = apt.group(1).replace("\\", " ").split() if apt else []
    check("the build stage has an apt-get install line", bool(pkgs))
    for pkg in ["libvulkan-dev", "libva-dev", "libvpl-dev", "libdrm-dev", "glslang-tools"]:
        check(f"the build stage installs {pkg}", pkg in pkgs, str(pkgs))

    script = open(os.path.join(HERE, "build-ffmpeg.sh")).read()
    base = re.search(r"^APT_PKGS=\(([^)]*)\)", script, re.M)
    intel = re.search(r"^if \[\[ \"\$GPU_VENDOR\" == \"intel\" \]\]; then\n\s+APT_PKGS\+=\(([^)]*)\)", script, re.M)
    check("build-ffmpeg.sh APT_PKGS and its intel addition are found", bool(base) and bool(intel))
    wanted = (base.group(1).split() if base else []) + (intel.group(1).split() if intel else [])
    check("the script's apt list is not empty", len(wanted) >= 15, str(wanted))
    missing = [p for p in wanted if p not in pkgs]
    check("the build stage installs everything build-ffmpeg.sh apt-installs for intel", not missing, str(missing))

    fetch = body.find("git fetch")
    at = apt.start() if apt else -1
    check("the apt install comes before the FSR2 fetch and compile", 0 <= at < fetch, f"{at} {fetch}")
    check("the distro shaderc is not installed (it broke nlmeans_vulkan)", "libshaderc" not in " ".join(pkgs))

    check("every -dev package has a runtime pair listed", not unpaired_dev(pkgs + wanted, DEV_RUNTIME),
          str(unpaired_dev(pkgs + wanted, DEV_RUNTIME)))
    check("mutant: a -dev package with no runtime pair is caught", unpaired_dev(pkgs + ["libnew-dev"], DEV_RUNTIME) == ["libnew-dev"])


# Each -dev package of the build stage and the runtime package the final stage gets for it. The final
# stage installs the ldd-derived runtime-packages.txt, so these arrive by themselves; None is a build tool.
DEV_RUNTIME = {
    "libvulkan-dev": "libvulkan1",
    "libva-dev": "libva2",
    "libvpl-dev": "libvpl2",
    "libdrm-dev": "libdrm2",
    "libx264-dev": "libx264-164",
    "libxcb1-dev": "libxcb1",
    "glslang-tools": None,
}


def unpaired_dev(pkgs, table):
    return sorted({p for p in pkgs if p.endswith("-dev") and p not in table})


def local_libs_failures(df):
    """What keeps a source-built shared library (shaderc, under /usr/local) out of the final image.

    dpkg -S finds no package for it, so runtime-packages.txt cannot name it: the build stage must copy it
    and the final stage must put it where ldconfig looks, before the ldd 'not found' check.
    """
    bad = []
    build, _, final = df.partition("AS build")[2].rpartition("\nFROM ")
    if not re.search(r"/usr/local/\*\).*?/out/local-libs/", build, re.S):
        bad.append("the build stage does not copy /usr/local libraries to /out/local-libs")
    # stdout of that loop is the package list: a stray echo would land in runtime-packages.txt and break apt
    if re.search(r'echo "local: [^"]*"', build) and not re.search(r'echo "local: [^"]*" >&2', build):
        bad.append("the local-lib message goes to stdout, into runtime-packages.txt")
    copy = final.find("COPY --from=build /out/local-libs/ /usr/local/lib/")
    ldconfig = final.find("ldconfig")
    gate = final.find("grep 'not found'")
    if copy < 0:
        bad.append("the final stage does not copy /out/local-libs to /usr/local/lib")
    if ldconfig < 0:
        bad.append("the final stage does not run ldconfig")
    if not 0 <= copy < ldconfig < gate:
        bad.append(f"order must be COPY, ldconfig, then the not-found check ({copy} {ldconfig} {gate})")
    return bad


def local_libs_checks(df):
    check("the source-built /usr/local libraries reach the final image", not local_libs_failures(df), str(local_libs_failures(df)))
    no_copy = df.replace("COPY --from=build /out/local-libs/ /usr/local/lib/", "")
    check("mutant: no copy of the local libraries is caught", bool(local_libs_failures(no_copy)))
    no_ldconfig = df.replace("ldconfig", "true")
    check("mutant: no ldconfig is caught", bool(local_libs_failures(no_ldconfig)))
    late = df.replace("RUN ldconfig\n", "") + "\nRUN ldconfig\n"
    check("mutant: ldconfig after the not-found check is caught", bool(local_libs_failures(late)))
    stdout_echo = df.replace('echo "local: $n" >&2', 'echo "local: $n"')
    check("mutant: a message on stdout (into runtime-packages.txt) is caught", bool(local_libs_failures(stdout_echo)))
    no_build = df.replace("/out/local-libs/", "/out/x/")
    check("mutant: a build stage that copies nothing is caught", bool(local_libs_failures(no_build)))


DRIVER_PKGS = ["mesa-vulkan-drivers", "intel-media-va-driver-non-free", "libigdgmm12"]
NONFREE_SRC = "/etc/apt/sources.list.d/intel-non-free.sources"


def final_runs(final):
    return re.findall(r"^RUN (?:[^\n]*\\\n)*[^\n]*", final, re.M)


def driver_failures(df):
    """What leaves the Arc image without the drivers libvulkan and libva dlopen (bench rows 2-4 and 9 failed).

    runtime-packages.txt comes from ldd of the patched ffmpeg, which never sees a dlopen'd driver, so the
    final stage must install the Intel Vulkan ICD and the iHD VA driver itself, and a build-time RUN must
    prove the ICD JSON exists and that both driver libraries resolve every dependency.
    """
    bad = []
    final = df.rpartition("\nFROM ")[2]
    runs = final_runs(final)
    install = next((r for r in runs if "apt-get install" in r and "mesa-vulkan-drivers" in r), None)
    if install is None:
        bad.append("the final stage has no apt-get install of the Intel drivers")
    else:
        for pkg in DRIVER_PKGS:
            if not re.search(rf"(?<![\w.-]){re.escape(pkg)}(?![\w.-])", install):
                bad.append(f"the driver install does not name {pkg}")
        order = [install.find(s) for s in (f"> {NONFREE_SRC}", "apt-get update", "apt-get install", f"rm -f {NONFREE_SRC}")]
        if "non-free" not in install or min(order) < 0 or order != sorted(order):
            bad.append("the non-free component must be written, updated, installed from and removed within the one RUN")
        if "rm -rf /var/lib/apt/lists/*" not in install:
            bad.append("the driver install does not clean the apt lists")
    gate = next((r for r in runs if "/usr/share/vulkan/icd.d" in r), None)
    if gate is None:
        bad.append("no RUN checks the Intel ICD JSON in /usr/share/vulkan/icd.d")
    else:
        if "*intel*.json" not in gate:
            bad.append("the ICD check does not look for an Intel ICD JSON")
        if "library_path" not in gate:
            bad.append("the ICD check does not read the library the JSON names")
        if "iHD_drv_video.so" not in gate:
            bad.append("the acceptance does not check iHD_drv_video.so")
        if len(re.findall(r"ldd ", gate)) < 2 or len(re.findall(r"grep 'not found'", gate)) < 2:
            bad.append("the acceptance must run ldd on both the ICD library and iHD and fail on 'not found'")
        if install is not None:
            if final.find(gate) < final.find(install):
                bad.append("the driver acceptance runs before the drivers are installed")
            if final.find(gate) < final.find("RUN ldconfig"):
                bad.append("the driver acceptance runs before ldconfig")
    comments = "\n".join(re.findall(r"^#[^\n]*", final, re.M))
    if not (re.search(r"\bvainfo\b", comments) and re.search(r"\bvulkaninfo\b", comments) and "GPU" in comments):
        bad.append("no comment says vainfo/vulkaninfo need a GPU so the Arc bench is the real proof")
    return bad


def driver_checks(df, spec):
    check("the final image installs the Intel drivers and proves they load", not driver_failures(df), str(driver_failures(df)))
    for pkg in DRIVER_PKGS:
        check(f"mutant: no {pkg} is caught", bool(driver_failures(df.replace(pkg, "removed-pkg"))))
    check("mutant: the free iHD package instead of non-free is caught",
          bool(driver_failures(df.replace("intel-media-va-driver-non-free", "intel-media-va-driver"))))
    check("mutant: a non-free source left behind is caught", bool(driver_failures(df.replace(f"rm -f {NONFREE_SRC};", "true;"))))
    check("mutant: no ICD check is caught", bool(driver_failures(df.replace("icd.d", "icd.x"))))
    check("mutant: no ldd of iHD is caught", bool(driver_failures(df.replace("iHD_drv_video.so", "x.so"))))
    check("mutant: no ldd of the ICD library is caught", bool(driver_failures(df.replace("library_path", "x"))))
    check("mutant: no 'not found' gate is caught", bool(driver_failures(df.replace("grep 'not found'", "true"))))
    check("mutant: no GPU caveat is caught", bool(driver_failures(df.replace("vulkaninfo", "x"))))
    final = df.rpartition("\nFROM ")[2]
    gate = next((r for r in final_runs(final) if "/usr/share/vulkan/icd.d" in r), "")
    early = df.replace(gate, "", 1).replace("COPY --from=build /out/runtime-packages.txt", gate + "\nCOPY --from=build /out/runtime-packages.txt", 1)
    check("mutant: the driver check before the install is caught", bool(gate) and bool(driver_failures(early)))
    check("the spec page says why the drivers are installed by name",
          all(p in spec.partition("### 5.8 ")[2] for p in ["dlopen", *DRIVER_PKGS]))


SHIM_PATH = "/usr/local/bin/jellyfin-ffmpeg-upscale"


def shim_interpreter_failures(df, shim_src):
    """What lets an image ship whose shim Jellyfin cannot start (build 8: no python3, 'Failed to find valid ffmpeg').

    The shim is a script, so its interpreter must be in the FINAL stage (the build stage's python3 does not
    ship), and the acceptance RUN must make the exact call Jellyfin makes, not only test the real binary.
    python3-minimal is not enough: json, subprocess and re live in the stdlib package only python3 pulls in.
    """
    bad = []
    final = df.rpartition("\nFROM ")[2]
    shebang = shim_src.split("\n", 1)[0]
    if not re.search(r"^#!/usr/bin/env python3$", shebang):
        bad.append(f"the shim's interpreter is no longer python3 ({shebang!r}); update this check")
    apt = re.search(r"apt-get install[^\n]*(?:\\\n[^\n]*)*", final)
    if not apt or not re.search(r"(?<![\w.-])python3(?![\w.-])", apt.group(0)):
        bad.append("the final stage does not apt-get install python3 (python3-minimal does not count)")
    copy = final.find("jellyfin-ffmpeg-upscale /usr/local/bin/jellyfin-ffmpeg-upscale")
    call = final.find(f"$({SHIM_PATH} -version)")
    if copy < 0:
        bad.append("the final stage does not copy the shim")
    if call < 0:
        bad.append("the acceptance RUN does not run the shim with -version (the call Jellyfin makes)")
    elif copy > call:
        bad.append("the shim -version check runs before the shim is copied in")
    if call >= 0 and "grep -q '^ffmpeg version'" not in final[call:]:
        bad.append("the shim -version output is not required to start with 'ffmpeg version'")
    return bad


def shim_interpreter_checks(df):
    shim_src = open(os.path.join(REPO, "shim", "jellyfin-ffmpeg-upscale")).read()
    check("the final image carries the shim's interpreter and checks the shim", not shim_interpreter_failures(df, shim_src),
          str(shim_interpreter_failures(df, shim_src)))
    no_py = df.replace(" python3 jq", " jq")
    check("mutant: no python3 in the final stage is caught", bool(shim_interpreter_failures(no_py, shim_src)))
    minimal = df.replace(" python3 jq", " python3-minimal jq")
    check("mutant: python3-minimal is caught", bool(shim_interpreter_failures(minimal, shim_src)))
    no_call = df.replace(f"$({SHIM_PATH} -version)", "$(true)")
    check("mutant: no shim -version check is caught", bool(shim_interpreter_failures(no_call, shim_src)))
    no_grep = df.replace("grep -q '^ffmpeg version'", "true")
    check("mutant: a shim check that does not read the output is caught", bool(shim_interpreter_failures(no_grep, shim_src)))
    early = df.replace(f"$({SHIM_PATH} -version)", "$(true)").replace("ENV DEBIAN_FRONTEND=noninteractive\n",
                       f"ENV DEBIAN_FRONTEND=noninteractive\nRUN v=$({SHIM_PATH} -version); echo \"$v\" | grep -q '^ffmpeg version'\n", 1)
    check("mutant: a shim check before the shim is copied is caught", bool(shim_interpreter_failures(early, shim_src)))
    other = shim_src.replace("#!/usr/bin/env python3", "#!/usr/bin/env python3.12", 1)
    check("mutant: a changed shebang is caught", bool(shim_interpreter_failures(df, other)))


def dockerfile_checks(tmp):
    df = open(DOCKERFILE).read()
    spec = open(SPEC).read()
    digest = re.search(r"^ARG JELLYFIN_IMAGE=(\S+@sha256:[0-9a-f]{64})$", df, re.M)
    check("base image is pinned by digest", bool(digest))
    check("the spec page names the same base image", bool(digest) and digest.group(1).split("docker.io/")[1] in spec)
    commit = re.search(r"^ARG FSR2_COMMIT=([0-9a-f]{40})$", df, re.M)
    check("FSR2 is pinned to a full commit", bool(commit))
    check("the spec page names the FSR2 commit", bool(commit) and commit.group(1) in spec)
    check("the .NET SDK sha512 is pinned and checked",
          bool(re.search(r"^ARG DOTNET_SDK_SHA512=[0-9a-f]{128}$", df, re.M)) and "sha512sum -c" in df)
    check("no :latest", ":latest" not in df)
    froms = re.findall(r"^FROM (\S+)", df, re.M)
    check("every stage starts from the pinned base", froms and all(f == "${JELLYFIN_IMAGE}" for f in froms), str(froms))
    for needle in ["GPU_VENDOR=intel", "WITH_ORT=1", "ORT_EP=openvino", "WITH_FSR2=1", "FSR2_SDK=", "FSR2_LIB=",
                   "PREFIX=/usr/lib/jellyfin-ffmpeg-oidn", "scripts/build-ffmpeg.sh", "fsr2-nocuda",
                   "ffmpeg_required_filters intel", "-hwaccels", "h264_qsv", "cmp /tmp/base-ffmpeg",
                   "not found", "git rev-parse HEAD", "FSRCNNX_x2_8-0-4-1.glsl", "JELLYFIN_FFMPEG=",
                   "/var/cache/jellyfin/jellyfin-upscale.json", "jellyfin-ffmpeg-upscale"]:
        check(f"Dockerfile carries {needle}", needle in df)
    check("Dockerfile publishes nothing and sets no USER", not re.search(r"docker push|^USER ", df, re.M))
    check("build-ffmpeg.sh is not edited by the image", "sed -i" not in df)
    check("NVIDIA build variables absent", not re.search(r"GPU_VENDOR=nvidia|WITH_DLSS|WITH_OPTIX", df))

    build_apt_checks(df)
    local_libs_checks(df)
    driver_checks(df, spec)
    shim_interpreter_checks(df)
    entrypoint_checks(df)
    web_step_checks(df)

    ignore = open(os.path.join(REPO, "docker", "intel", "Dockerfile.dockerignore")).read().split()
    need = ["scripts", "src", "web", "web/src", "shim", "assets", "ffmpeg", "docker", ".", "web/theme", "patches"]
    check("dockerignore keeps what the build needs", not [n for n in need if n in ignore], str(ignore))
    for rel in ["shim/jellyfin-ffmpeg-upscale", "scripts/bench-intel.sh", "scripts/ffmpeg-vendor.sh", "scripts/build-ffmpeg.sh",
                "scripts/install-shaders.sh", "scripts/jellyfin-plus-webinject", "scripts/build-web-panel.sh",
                "ffmpeg/gen_perm.py", "ffmpeg/fsr2compat.h", "web/theme/jellyfinplus.css", "web/package-lock.json",
                "assets/jellyfinplus.png", "assets/plugin-meta.json", "docker/intel/stage-plugin.sh",
                "docker/intel/entrypoint.py", "docker/intel/accept-plugin.sh", "scripts/test_entrypoint.py"]:
        check(f"{rel} exists", os.path.isfile(os.path.join(REPO, rel)))
    for rel in ["scripts/bench-intel.sh", "docker/intel/stage-plugin.sh", "docker/intel/accept-plugin.sh"]:
        p = subprocess.run([BASH, "-n", os.path.join(REPO, rel)], capture_output=True, text=True)
        check(f"bash -n {rel}", p.returncode == 0, p.stderr)
    check("the spec page has section 5.8", "### 5.8 " in spec)

    out = subprocess.run([BASH, "-c", f". {VENDOR}; ffmpeg_required_filters intel"], capture_output=True, text=True).stdout.split()
    check("the Intel filter list the image checks is oidn ort", out == ["oidn", "ort"], str(out))


def entrypoint_failures(df):
    """What lets an image ship that starts Jellyfin without the plugin (the first Arc image did).

    The plugin is only staged in /usr/lib/jellyfin-plus-dist; Jellyfin loads from $JELLYFIN_DATA_DIR/plugins.
    So the final stage must set the seeding wrapper as ENTRYPOINT, copy it executable, and end with a RUN
    that starts the server headless and requires the plugin to load, after everything it depends on.
    """
    bad = []
    final = df.rpartition("\nFROM ")[2]
    wrapper = "/usr/local/bin/jellyfin-plus-entrypoint"
    if not re.search(r"^ENTRYPOINT \[\"" + re.escape(wrapper) + r"\"\]$", final, re.M):
        bad.append("the final stage does not set the seeding wrapper as ENTRYPOINT (exec form, no args)")
    if re.search(r"^CMD ", final, re.M):
        bad.append("a CMD would be passed to Jellyfin as arguments")
    copy = re.search(r"^COPY --chmod=0755 docker/intel/entrypoint\.py " + re.escape(wrapper) + r"$", final, re.M)
    if not copy:
        bad.append("the wrapper is not copied executable to " + wrapper)
    accept = re.search(r"^RUN --mount=type=bind,source=docker/intel/accept-plugin\.sh,target=(\S+) \\\n\s+bash \1 " + re.escape(wrapper) + r"$",
                       final, re.M)
    if not accept:
        bad.append("the final stage has no acceptance RUN of accept-plugin.sh against the wrapper")
    plugin_copy = final.find("COPY --from=build /out/plugin-dist /usr/lib/jellyfin-plus-dist")
    patcher_copy = final.find("COPY --from=build /out/patcher /usr/lib/jellyfin-plus\n")
    ffmpeg_accept = final.find("RUN --mount=type=bind,from=base-pkgs")
    if accept and copy:
        if not 0 <= patcher_copy < accept.start():
            bad.append("the acceptance RUN runs before the patcher is copied in")
        if not 0 <= plugin_copy < copy.start() < accept.start():
            bad.append("order must be the plugin COPY, the wrapper COPY, then the acceptance RUN")
        if not 0 <= ffmpeg_accept < accept.start():
            bad.append("the acceptance RUN runs before the ffmpeg acceptance RUN")
    return bad


def entrypoint_checks(df):
    check("the final image seeds the plugin through an ENTRYPOINT wrapper and proves it loads", not entrypoint_failures(df),
          str(entrypoint_failures(df)))
    no_ep = df.replace('ENTRYPOINT ["/usr/local/bin/jellyfin-plus-entrypoint"]\n', "")
    check("mutant: no ENTRYPOINT is caught", bool(entrypoint_failures(no_ep)))
    base_ep = df.replace('ENTRYPOINT ["/usr/local/bin/jellyfin-plus-entrypoint"]', 'ENTRYPOINT ["/jellyfin/jellyfin"]')
    check("mutant: the base ENTRYPOINT is caught", bool(entrypoint_failures(base_ep)))
    shell_form = df.replace('ENTRYPOINT ["/usr/local/bin/jellyfin-plus-entrypoint"]', "ENTRYPOINT /usr/local/bin/jellyfin-plus-entrypoint")
    check("mutant: a shell-form ENTRYPOINT is caught", bool(entrypoint_failures(shell_form)))
    not_exec = df.replace("COPY --chmod=0755 docker/intel/entrypoint.py", "COPY docker/intel/entrypoint.py")
    check("mutant: a wrapper that is not executable is caught", bool(entrypoint_failures(not_exec)))
    no_accept = re.sub(r"\nRUN --mount=type=bind,source=docker/intel/accept-plugin\.sh.*?jellyfin-plus-entrypoint\n", "\n", df, flags=re.S)
    check("mutant: no acceptance RUN is caught", bool(entrypoint_failures(no_accept)) and no_accept != df)
    with_cmd = df.replace("ENTRYPOINT [", 'CMD ["--x"]\nENTRYPOINT [', 1)
    check("mutant: a CMD is caught", bool(entrypoint_failures(with_cmd)))
    early = (df.replace(re.search(r"\n# The plugin acceptance:.*?jellyfin-plus-entrypoint\n", df, re.S).group(0), "\n")
               .replace("COPY --from=build /out/patcher /usr/lib/jellyfin-plus\n",
                        "RUN --mount=type=bind,source=docker/intel/accept-plugin.sh,target=/tmp/accept-plugin.sh \\\n"
                        "    bash /tmp/accept-plugin.sh /usr/local/bin/jellyfin-plus-entrypoint\n"
                        "COPY --from=build /out/patcher /usr/lib/jellyfin-plus\n", 1))
    check("mutant: an acceptance RUN before the plugin is in place is caught", bool(entrypoint_failures(early)))

    script = open(os.path.join(REPO, "docker", "intel", "accept-plugin.sh")).read()
    check("the acceptance script greps for the plugin's load line", "Loaded plugin: Jellyfin Plus" in script)
    check("the acceptance script asserts the plugin dir was seeded", "plugins/JellyfinPlus_" in script)
    check("the acceptance script stops the server it started", "kill " in script and "rm -rf" in script)


def web_step(df):
    """The build stage's RUN that builds the client and injects it (the one that failed silently in CI)."""
    for m in re.finditer(r"^RUN (?:[^\n]*\\\n)*[^\n]*", df, re.M):
        if "jellyfin-plus-webinject" in m.group(0):
            return m.group(0)
    return ""


def web_step_checks(df):
    step = web_step(df)
    check("the Dockerfile has the web build and inject step", "jellyfin-plus-webinject" in step)
    check("the web step starts with set -eux so a failure names its command",
          bool(re.match(r"RUN set -[a-z]*e[a-z]*x[a-z]*;", step) or re.match(r"RUN set -[a-z]*x[a-z]*e[a-z]*;", step)), step[:60])
    check("the web step echoes which phase it is in", len(re.findall(r"\becho\b", step)) >= 3, step)

    # JP_SRC / JP_THEME_SRC must name the files the step installs just before it injects.
    installed = re.findall(r"install -m 0644 \S+ ([^\s;]+)", step)
    src = re.search(r"JP_SRC=(\S+)", step)
    theme = re.search(r"JP_THEME_SRC=(\S+)", step)
    check("JP_SRC is the installed client bundle", bool(src) and src.group(1) in installed, f"{installed}")
    check("JP_THEME_SRC is the installed theme css", bool(theme) and theme.group(1) in installed, f"{installed}")
    check("the step injects into $JELLYFIN_WEB", 'JP_WEB="$JELLYFIN_WEB"' in step)
    check("the step runs after stage-plugin.sh creates /out/patcher",
          0 <= df.find("stage-plugin.sh /src") < df.find(step[:40]) if step else False)


def test_webinject_without_dpkg_package(tmp):
    """The injector must not abort silently when jellyfin-web is a plain tree, not a dpkg package.

    In the image build stage dpkg-query exits 1 for jellyfin-web; under set -e + pipefail that killed the
    script before any output (Arc image build, step 12/13).
    """
    web = os.path.join(tmp, "web")
    os.makedirs(os.path.join(web, "themes", "dark"))
    open(os.path.join(web, "index.html"), "w").write(
        '<html><head><title>x</title><script defer="defer" src="main.js"></script></head><body></body></html>')
    open(os.path.join(web, "config.json"), "w").write('{"themes": []}')
    open(os.path.join(web, "themes", "dark", "theme.css"), "w").write("")
    src = os.path.join(tmp, "src.js")
    open(src, "w").write("// bundle\n")
    css = os.path.join(tmp, "theme.css")
    open(css, "w").write("/* theme */\n")

    stubs = os.path.join(tmp, "stubs")
    os.makedirs(stubs)
    p = os.path.join(stubs, "dpkg-query")
    open(p, "w").write("#!/bin/sh\nexit 1\n")
    os.chmod(p, 0o755)

    env = clean_env({"JP_WEB": web, "JP_SRC": src, "JP_THEME_SRC": css}, path=stubs)
    env.pop("JP_CONF_MD5", None)
    rc, out, err = run(os.path.join(HERE, "jellyfin-plus-webinject"), [], env)
    check("webinject exits 0 when dpkg-query fails", rc == 0, f"rc={rc} out={out!r} err={err!r}")
    check("webinject reports the index.html hook", "index.html hooked" in out, out)
    check("webinject installed jellyfin-plus.js", os.path.isfile(os.path.join(web, "jellyfin-plus.js")))
    html = open(os.path.join(web, "index.html")).read()
    check("index.html carries the script tag", "jellyfin-plus.js?v=" in html)
    check("the theme was installed", os.path.isfile(os.path.join(web, "themes", "jellyfinplus", "theme.css")), out)

    # the apt hook's pre-dpkg call reads the same md5, so it must not die either
    rc, out, err = run(os.path.join(HERE, "jellyfin-plus-webinject"), ["--restore-conffile"], env)
    check("--restore-conffile exits 0 when dpkg-query fails", rc == 0, f"rc={rc} err={err!r}")


GATE_CASES = [("0830", True), ("0900", True), ("0700", True), ("1200", True), ("1830", True),
              ("0158", False), ("0100", False), ("0130", False), ("1630", False), ("1700", False)]
PSI_OK = "some avg10=0.00 avg60=0.00 avg300=0.00 total=0\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n"
PSI_HOT = PSI_OK.replace("avg60=0.00", "avg60=5.00", 1)


# (BENCH_BLOCK_WINDOWS, clock, open?): half-open [start,end), bounds read as decimal (0830 and 0900 are
# invalid octal), and the owner's real night window ends at 0415.
WINDOW_CASES = [("0100-0415,1630-1830", "0414", False), ("0100-0415,1630-1830", "0415", True),
                ("0100-0415,1630-1830", "0100", False), ("0100-0415,1630-1830", "0700", True),
                ("0100-0415,1630-1830", "0059", True), ("0100-0415,1630-1830", "1629", True),
                ("0100-0415,1630-1830", "1630", False), ("0100-0415,1630-1830", "1829", False),
                ("0100-0415,1630-1830", "1830", True),
                ("0800-0930", "0759", True), ("0800-0930", "0830", False), ("0800-0930", "0900", False),
                ("0800-0930", "0930", True),
                ("0830-0900", "0829", True), ("0830-0900", "0830", False), ("0830-0900", "0859", False),
                ("0830-0900", "0900", True),
                ("0900-1000", "0830", True), ("0900-1000", "0900", False),
                ("0000-2400", "2359", False), ("0000-2400", "0000", False), ("2300-2400", "2230", True)]


def gate_run(script, w, hm, psi, windows=None, **extra):
    """One real run of row 13 with the clock faked to hm: 'open', 'closed' (a gate: refusal) or 'rcN'."""
    env = dict(w.env, BENCH_ASSERT_QUIET="1", FAKE_HM=hm, BENCH_PSI_FILE=psi, **extra)
    if windows is not None:
        env["BENCH_BLOCK_WINDOWS"] = windows
    rc, out, err = run(script, ["--rows", "13"], env)
    if rc == 0 and "MANUAL" in out:
        return "open", err
    if rc == 2 and "gate:" in err:
        return "closed", err
    return f"rc{rc}", err


def gate_failures(script, tmp, tag):
    """What the time-window and PSI gates get wrong (empty = right). `date +%H%M` is faked per case; the
    leading-zero values 0830, 0900 and 0158 are the ones bash reads as invalid octal."""
    bad = []
    w = World(tmp, tag)
    psi, hot = os.path.join(w.root, "psi"), os.path.join(w.root, "psi-hot")
    open(psi, "w").write(PSI_OK)
    open(hot, "w").write(PSI_HOT)
    for hm, ok in GATE_CASES:
        got = gate_run(script, w, hm, psi)[0]
        if got != ("open" if ok else "closed"):
            bad.append(f"{hm}:{'open' if ok else 'closed'}-but-{got}")
    for windows, hm, ok in WINDOW_CASES:
        got = gate_run(script, w, hm, psi, windows)[0]
        if got != ("open" if ok else "closed"):
            bad.append(f"{windows}@{hm}:{'open' if ok else 'closed'}-but-{got}")
    rc, out, err = run(script, ["--rows", "13"], dict(w.env, BENCH_ASSERT_QUIET="1", FAKE_HM="1200", BENCH_PSI_FILE=hot))
    if not (rc == 2 and "PSI" in err):
        bad.append(f"psi:rc{rc}")
    return bad


BAD_WINDOWS = ["abc", "0100-0700,", ",0100-0700", "0100-0700,,1630-1830", "100-700", "0100", "0100-", "-0700",
               "2500-2600", "0160-0700", "0100-0760", "0100-2401", "2400-2400", "0700-0100", "0100-0100",
               "01:00-07:00", "0100-0700 1630-1830", "0100-0700, 1630-1830", "0100-0700;1630-1830", "0100–0700"]
BAD_TZ = ["Australia Melbourne", "Australia/Melbourne;date", "$(date)", "Aus*", "Australia\\Melbourne"]


def window_failures(script, tmp, tag):
    """What BENCH_BLOCK_WINDOWS and BENCH_TZ get wrong beyond the clock cases (empty = right): the plan
    line, the zone handed to date, the refusal of malformed values in --dry-run and in a real run."""
    bad = []
    w = World(tmp, tag)
    psi = os.path.join(w.root, "psi")
    open(psi, "w").write(PSI_OK)
    tail = "memory PSI some avg60 < 1%; BENCH_ASSERT_QUIET=1 (no vzdump"

    for extra, want in [
        ({}, "plan: gates: window outside 01:00-07:00 and 16:30-18:30 Australia/Melbourne; " + tail),
        ({"BENCH_BLOCK_WINDOWS": ""}, "plan: gates: window outside 01:00-07:00 and 16:30-18:30 Australia/Melbourne; " + tail),
        ({"BENCH_BLOCK_WINDOWS": "0100-0415,1630-1830", "BENCH_TZ": "Australia/Adelaide"},
         "plan: gates: window outside 01:00-04:15 and 16:30-18:30 Australia/Adelaide; " + tail),
        ({"BENCH_BLOCK_WINDOWS": "0830-0900"}, "plan: gates: window outside 08:30-09:00 Australia/Melbourne; " + tail),
    ]:
        rc, out, err = run(script, ["--dry-run"], dict(w.env, **extra))
        if rc != 0 or not any(l.startswith(want) for l in out.splitlines()):
            bad.append(f"plan:{extra}:rc{rc}")

    log = os.path.join(w.root, "tz.log")
    for extra, zone in [({}, "Australia/Melbourne"), ({"BENCH_TZ": "Australia/Adelaide"}, "Australia/Adelaide"),
                        ({"BENCH_TZ": ""}, "Australia/Melbourne")]:
        open(log, "w").close()
        got, err = gate_run(script, w, "0200", psi, FAKE_TZ_LOG=log, **extra)
        zones = open(log).read().split()
        if got != "closed" or zones != [zone] or zone not in err:
            bad.append(f"tz:{extra}:{got}:{zones}")

    for windows in BAD_WINDOWS:
        rc, out, err = run(script, ["--dry-run"], dict(w.env, BENCH_BLOCK_WINDOWS=windows))
        if rc != 2 or "BENCH_BLOCK_WINDOWS" not in err or "plan:" in out:
            bad.append(f"dry:{windows}:rc{rc}")
        got, err = gate_run(script, w, "1200", psi, windows)
        if got != "rc2" or "BENCH_BLOCK_WINDOWS" not in err or "gate:" in err:
            bad.append(f"run:{windows}:{got}")
    for zone in BAD_TZ:
        rc, out, err = run(script, ["--dry-run"], dict(w.env, BENCH_TZ=zone))
        if rc != 2 or "BENCH_TZ" not in err:
            bad.append(f"dry-tz:{zone}:rc{rc}")
        got, err = gate_run(script, w, "1200", psi, BENCH_TZ=zone)
        if got != "rc2" or "BENCH_TZ" not in err:
            bad.append(f"run-tz:{zone}:{got}")
    return bad


def runfps_failures(script, tmp, tag):
    """A failed ffmpeg run must reach the FAIL branch of rows 2 and 11; a good one must still pass row 11."""
    bad = []
    w = World(tmp, tag, extra={"BENCH_JELLYFIN_TOKEN": "", "BENCH_PLEX_TRANSCODING": "1"})
    rc, out, err = w.run(["--rows", "2,11"], script=script, FAKE_FFMPEG_FAIL="1")
    res = results(out)
    for row in (2, 11):
        st, detail = res.get(row, ("", ""))
        if st != "FAIL" or "ffmpeg failed" not in detail:
            bad.append(f"row{row}:{st}:{detail[:40]}")
    rc, out, err = w.run(["--rows", "11"], script=script)
    st, detail = results(out).get(11, ("", ""))
    if st != "PASS" or "fps=120" not in detail:
        bad.append(f"row11-good:{st}:{detail[:40]}")
    return bad


def token_failures(script, tmp, tag):
    """The admin token goes to curl on stdin (--config -), never on its command line, on every call."""
    bad = []
    w = World(tmp, tag)
    w.run(["--rows", "3"], script=script)
    log = w.log()
    if not log:
        return ["no curl call logged"]
    if any(TOKEN in " ".join(e["argv"]) for e in log):
        bad.append("token on argv")
    if not all(TOKEN in e["stdin"] for e in log):
        bad.append("token missing from stdin on some call")
    if not all("--config" in e["argv"] for e in log):
        bad.append("not --config -")
    return bad


def test_gates_and_confirmation(tmp):
    bad = gate_failures(BENCH, tmp, "gates")
    check("the window and PSI gates read HHMM as decimal", not bad, str(bad))
    bad = window_failures(BENCH, tmp, "windows")
    check("BENCH_BLOCK_WINDOWS and BENCH_TZ: plan line, zone, malformed values refused", not bad, str(bad))

    w = World(tmp, "nogates")
    rc, out, err = w.run(["--no-gates", "--rows", "13"], BENCH_CONFIRM_NO_GATES="")
    check("--no-gates without BENCH_CONFIRM_NO_GATES=1 exits 2", rc == 2 and "BENCH_CONFIRM_NO_GATES" in err, f"rc={rc} {err}")
    rc, out, err = w.run(["--no-gates", "--rows", "13"])
    check("--no-gates with BENCH_CONFIRM_NO_GATES=1 proceeds", rc == 0 and "MANUAL" in out, f"rc={rc} {err}")
    rc, out, err = w.run(["--help"])
    check("--help names --no-gates and its confirmation", rc == 0 and "--no-gates" in out and "BENCH_CONFIRM_NO_GATES" in out, out)
    check("--help names BENCH_BLOCK_WINDOWS and BENCH_TZ", "BENCH_BLOCK_WINDOWS" in out and "BENCH_TZ" in out, out)


def test_hermetic_api(tmp):
    """The run against a fake Jellyfin (a stub curl serving JSON by path) and the real jq."""
    # every call is judged, even when each one works
    w = World(tmp, "happy", {"stream_logs": "libplacebo=w=1920"})
    rc, out, err = w.run(["--rows", "2,3,4,12"])
    res = results(out)
    check("happy run exits 0 with a summary", rc == 0 and "summary:" in out, f"rc={rc} {err} {out[-300:]}")
    check("rows 2, 3, 4 and 12 PASS against the fake API", all(res.get(r, ("",))[0] == "PASS" for r in (2, 3, 4, 12)), str(res))
    segs = [u for u in w.urls() if "/hls1/main/" in u]
    check("each of the 6 streams fetches its 5 segments (the loop keeps its token)", len(segs) == 6 * 5, str(len(segs)))
    check("a dying stream is not what passes: row 12 saw each height", all(f"{h}p:" in res.get(12, ("", ""))[1] for h in (480, 720, 1080, 2160)), str(res.get(12)))

    # item 2: an API that answers only /System/Info/Public
    w = World(tmp, "failall", {"fail_all": True}, {"BENCH_ORT_MODEL": "/m/model.onnx"})
    rc, out, err = w.run(["--rows", "3,4,5,6,10,12"])
    res = results(out)
    check("every row that calls a failing API records FAIL", all(res.get(r, ("",))[0] == "FAIL" for r in (3, 4, 5, 6, 10, 12)), f"{res} {err}")
    check("the run still prints summary: and exits 1", rc == 1 and "summary:" in out, f"rc={rc} {err}")
    w = World(tmp, "failmaster", {"fail_master": True})
    rc, out, err = w.run(["--rows", "3,4,10,12"])
    res = results(out)
    check("a stream that cannot start records FAIL for rows 3, 4, 10 and 12", all(res.get(r, ("",))[0] == "FAIL" for r in (3, 4, 10, 12)) and "summary:" in out, f"{res} {err}")
    w = World(tmp, "failseg", {"fail_segments": True})
    rc, out, err = w.run(["--rows", "3,12"])
    res = results(out)
    check("a stream with no segment is a FAIL, not a 0x PASS", res.get(3, ("",))[0] == "FAIL" and res.get(12, ("",))[0] == "FAIL", str(res))

    bad = token_failures(BENCH, tmp, "token")
    check("the token is on curl's stdin, not its command line", not bad, str(bad))
    bad = runfps_failures(BENCH, tmp, "runfps")
    check("a failed ffmpeg run reaches the FAIL branch of rows 2 and 11", not bad, str(bad))


def test_row_1_scope(tmp):
    w = World(tmp, "row1-old")
    open(w.shim, "w").write("old session: h264_nvenc\n")
    rc, out, err = w.run(["--rows", "1"])
    st, detail = results(out).get(1, ("", ""))
    check("row 1 ignores shim-log lines written before the run", st == "PASS", f"{st} {detail} {err}")
    w = World(tmp, "row1-new", {"probe_logs": "new session: h264_nvenc"})
    open(w.shim, "w").write("old line\n")
    rc, out, err = w.run(["--rows", "1"])
    st, detail = results(out).get(1, ("", ""))
    check("row 1 fails on an nvenc line written during the run", st == "FAIL" and "nvenc/cuda-in-log" in detail, f"{st} {detail}")
    w = World(tmp, "row12-old")
    open(w.shim, "w").write("old libplacebo line\n")
    rc, out, err = w.run(["--rows", "12"])
    st, detail = results(out).get(12, ("", ""))
    check("row 12 does not take an old libplacebo line as this run's", st == "FAIL" and "no-libplacebo-in-log" in detail, f"{st} {detail}")
    w = World(tmp, "row12-new", {"stream_logs": "libplacebo=w=1920"})
    rc, out, err = w.run(["--rows", "12"])
    check("row 12 passes on a libplacebo line written during the run", results(out).get(12, ("",))[0] == "PASS", str(results(out)))


def row6_case(tmp, tag, scenario=None, **extra):
    w = World(tmp, tag, scenario, extra={"BENCH_ORT_MODEL": "/m/model.onnx"})
    rc, out, err = w.run(["--rows", "6"], **extra)
    return w, rc, out, err, results(out).get(6, ("", ""))


def test_row_6(tmp):
    w, rc, out, err, (st, detail) = row6_case(tmp, "r6-pass")
    check("row 6 PASSes when the 720p source is routed to the shader path", st == "PASS", f"{st} {detail} {err}")
    masters = [u for u in w.urls() if "master.m3u8" in u]
    check("row 6 asks for a real neural level, never `fast`",
          len(masters) == 1 and "neural=realesr-anime-x2" in masters[0] and "neural=fast" not in masters[0], str(masters))
    check("row 6 streams the 720p item", bool(masters) and "/Videos/id720/" in masters[0], str(masters))
    posts = w.posts()
    check("row 6 sets NeuralMaxSourceHeight=480 (and NeuralAllowed) then restores the configuration",
          len(posts) == 2 and posts[0].get("NeuralMaxSourceHeight") == 480 and posts[0].get("NeuralAllowed") is True
          and posts[0].get("Keep") == "me" and posts[1] == w.config0, str(posts))

    w, rc, out, err, (st, detail) = row6_case(tmp, "r6-already", {"config": {"NeuralAllowed": True, "NeuralMaxSourceHeight": 480}})
    check("row 6 leaves a configuration that is already right alone", st == "PASS" and not w.posts(), f"{st} {detail} {w.posts()}")

    w, rc, out, err, (st, detail) = row6_case(tmp, "r6-noapply", {"config_applies": False})
    check("row 6 FAILs, saying how to fix it, when the probe does not show 480",
          st == "FAIL" and "NeuralMaxSourceHeight" in detail and w.posts()[-1] == w.config0, f"{st} {detail}")

    for tag, scn in [("chosen", {"routing": "neural realesr-anime-x2 was chosen"}), ("none", {"routing": None}),
                     ("applied", {"applied": True})]:
        w, rc, out, err, (st, detail) = row6_case(tmp, "r6-" + tag, scn)
        check(f"row 6 FAILs on a record that is not the NOT-run wording ({tag})", st == "FAIL", f"{st} {detail}")

    rc, out, err = World(tmp, "r6-fast").run(["--rows", "6"], BENCH_NEURAL_LEVEL="fast")
    check("BENCH_NEURAL_LEVEL=fast is refused (it is not a level)", rc == 2 and "BENCH_NEURAL_LEVEL" in err, f"rc={rc} {err}")

    # a server-supplied string with quotes, a backslash and a newline must not break results.jsonl
    nasty = 'say "hi" \\ back\nline two'
    w, rc, out, err, (st, detail) = row6_case(tmp, "r6-json", {"routing": nasty})
    lines = w.jsonl()
    parsed = []
    for l in lines:
        try:
            parsed.append(json.loads(l))
        except ValueError:
            parsed.append(None)
    check("every results.jsonl line is valid JSON", bool(lines) and all(parsed), str(lines))
    check("a detail with quotes, a backslash and a newline round-trips",
          bool(parsed) and parsed[-1] is not None and nasty in parsed[-1]["detail"] and parsed[-1]["status"] == "FAIL", str(lines))
    check("a multi-line detail stays on one result: line", "\nline two" not in out and "back line two" in out, out)


def test_row_5(tmp):
    extra = {"BENCH_ORT_MODEL": "/m/model.onnx"}
    w = World(tmp, "r5-ok", extra=extra)
    rc, out, err = w.run(["--rows", "5"])
    st, detail = results(out).get(5, ("", ""))
    check("row 5 on openvino is MANUAL with the device recorded", st == "MANUAL" and "device=openvino" in detail, f"{st} {detail} {err}")
    urls = w.urls()
    stream = next((i for i, u in enumerate(urls) if "master.m3u8" in u and "neural=realesr-anime-x2" in u), -1)
    sid = re.search(r"PlaySessionId=([^&]+)", urls[stream]).group(1) if stream >= 0 else ""
    sess = next((i for i, u in enumerate(urls) if u.endswith("/JellyfinPlus/Session/" + sid)), -1) if sid else -1
    check("row 5 starts its neural stream before it reads that stream's session", 0 <= stream < sess, f"{stream} {sess} {urls}")
    check("row 5 streams the 360p item", stream >= 0 and "/Videos/id360/" in urls[stream], str(urls[:2]))
    w = World(tmp, "r5-cpu", {"device": "cpu"}, extra)
    st, detail = results(w.run(["--rows", "5"])[1]).get(5, ("", ""))
    check("row 5 FAILs on a CPU fallback", st == "FAIL" and "cpu" in detail, f"{st} {detail}")
    w = World(tmp, "r5-nostream", {"fail_master": True}, extra)
    st, detail = results(w.run(["--rows", "5"])[1]).get(5, ("", ""))
    check("row 5 FAILs when its stream cannot start", st == "FAIL", f"{st} {detail}")


def stage_meta_check(tmp, label, guid, abi, strip_source):
    """Run the real stage-plugin.sh against a stub dotnet and read the meta.json it writes."""
    root = tempfile.mkdtemp(dir=tmp)
    repo = os.path.join(root, "repo")
    for d in ("scripts", "assets", "src/patcher", "docker/intel"):
        os.makedirs(os.path.join(repo, d))
    shutil.copy(os.path.join(HERE, "plugin-meta.sh"), os.path.join(repo, "scripts"))
    shutil.copy(os.path.join(REPO, "assets", "plugin-meta.json"), os.path.join(repo, "assets"))
    open(os.path.join(repo, "assets", "jellyfinplus.png"), "wb").close()
    script = open(os.path.join(REPO, "docker", "intel", "stage-plugin.sh")).read()
    if strip_source:
        script = "".join(l for l in script.splitlines(True) if not re.match(r"\s*\.\s+\"\$REPO/scripts/plugin-meta\.sh\"", l))
    staged = os.path.join(repo, "docker", "intel", "stage-plugin.sh")
    open(staged, "w").write(script)
    bindir = os.path.join(root, "bin")
    os.makedirs(bindir)
    fake = os.path.join(bindir, "dotnet")
    # dotnet publish <proj> ... -o <dir>: leave the files stage-plugin.sh copies out of it.
    open(fake, "w").write('#!/usr/bin/env bash\nwhile [ $# -gt 0 ]; do [ "$1" = -o ] && out="$2"; shift; done\n'
                          'mkdir -p "$out"; cd "$out"; touch Jellyfin.Plugin.Plus.dll Jellyfin.Plugin.Plus.deps.json '
                          'Jellyfin.Plugin.Plus.Patcher.dll 0Harmony.dll\n')
    os.chmod(fake, 0o755)
    env = dict(os.environ, PATH=bindir + os.pathsep + os.environ["PATH"], PLUGIN_VERSION="3.2.1.0")
    dist, patcher = os.path.join(root, "dist"), os.path.join(root, "patcher")
    p = subprocess.run([BASH, staged, repo, root, dist, patcher], capture_output=True, text=True, env=env)
    try:
        doc = json.load(open(os.path.join(dist, "JellyfinPlus_3.2.1.0", "meta.json")))
    except (OSError, ValueError):
        doc = {}
    ok = bool(doc) and p.returncode == 0 and doc.get("guid") == (guid.group(1) if guid else None) \
        and doc.get("targetAbi") == (abi.group(1) if abi else None)
    if strip_source:
        check(f"{label} fails instead of writing meta.json", not ok and p.returncode != 0, p.stdout + p.stderr)
    else:
        check(f"{label} writes meta.json with plugin-meta.sh's guid and targetAbi", ok, p.stdout + p.stderr)


def test_shared_files(tmp):
    meta = os.path.join(HERE, "plugin-meta.sh")
    check("scripts/plugin-meta.sh exists", os.path.isfile(meta))
    src = open(meta).read() if os.path.isfile(meta) else ""
    guid = re.search(r"^PLUGIN_GUID=\"?([0-9a-f-]{36})", src, re.M)
    abi = re.search(r"^PLUGIN_TARGET_ABI=\"?([0-9.]+)", src, re.M)
    plugin_cs = open(os.path.join(REPO, "src", "Plugin.cs")).read()
    manifest = json.load(open(os.path.join(REPO, "manifest.json")))[0]
    check("plugin-meta.sh GUID is Plugin.cs's Id", bool(guid) and f'Guid.Parse("{guid.group(1)}")' in plugin_cs)
    check("plugin-meta.sh GUID is manifest.json's", bool(guid) and manifest["guid"] == guid.group(1))
    check("plugin-meta.sh target ABI is manifest.json's", bool(abi) and manifest["versions"][0]["targetAbi"] == abi.group(1))
    for rel in ("scripts/proxmox-build.sh", "docker/intel/stage-plugin.sh"):
        text = open(os.path.join(REPO, rel)).read()
        if rel.startswith("scripts/"):
            check(f"{rel} sources plugin-meta.sh", re.search(r"^\.\s+\S*scripts/plugin-meta\.sh", text, re.M) is not None)
        check(f"{rel} carries neither the GUID nor the target ABI", not (guid and guid.group(1) in text) and not (abi and abi.group(1) in text.replace("12.1.0.0", "")))
    p = subprocess.run([BASH, "-c", f". {meta}; plugin_first_meta 9.8.7.6"], capture_output=True, text=True)
    try:
        doc = json.loads(p.stdout)
    except ValueError:
        doc = {}
    check("plugin_first_meta prints the first-install meta.json",
          doc.get("guid") == (guid.group(1) if guid else None) and doc.get("version") == "9.8.7.6" and doc.get("name") == "Jellyfin Plus"
          and doc.get("targetAbi") == (abi.group(1) if abi else None) and {"owner", "overview", "category"} <= set(doc), p.stdout + p.stderr)
    stage_meta_check(tmp, "stage-plugin.sh", guid, abi, strip_source=False)
    stage_meta_check(tmp, "stage-plugin.sh without its source line", guid, abi, strip_source=True)

    ignore = open(os.path.join(REPO, "docker", "intel", "Dockerfile.dockerignore")).read().split()
    for pat in ("**/.env*", "**/*.pem", "**/*.key"):
        check(f"Dockerfile.dockerignore excludes {pat}", pat in ignore, str(ignore))


def main():
    if not BASH:
        print("no bash")
        return 1
    with tempfile.TemporaryDirectory() as tmp:
        for part in (test_plan_and_refusals, test_stub_run, test_mutants, test_webinject_without_dpkg_package,
                     test_gates_and_confirmation, test_hermetic_api, test_row_1_scope, test_row_6, test_row_5,
                     test_shared_files):
            sub = os.path.join(tmp, part.__name__)
            os.makedirs(sub)
            part(sub)
        dockerfile_checks(tmp)
    print(f"{PASSES} checks passed, {len(FAILS)} failed")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
