#!/usr/bin/env python3
"""The Intel image and the 13-row matrix (docs/architecture/GPU_VENDORS.md, 5.8).

    python3 -I scripts/test_bench_intel.py

Needs no GPU, ffmpeg, docker, network or Jellyfin. It runs the real scripts/bench-intel.sh and proves:

  - --dry-run prints the whole plan from hand-written goldens: 13 rows in order, each row's must-pass
    line, the thresholds, and the exact commands of rows 2, 7 and 9; it runs nothing and writes nothing;
  - the plan has no NVIDIA-only string in any command and follows the env overrides (node, title, clips);
  - each refusal exits 2 (bad row, unknown argument, BENCH_RUNS=0, no BENCH_ASSERT_QUIET);
  - a hermetic run against stub ffmpeg/curl/jq: row 13 is MANUAL, every row lacking an input is SKIP
    and never PASS, and results.jsonl is written;
  - docker/intel/Dockerfile keeps its pins (base digest, FSR2 commit, SDK sha512), builds the Intel
    variant, carries the acceptance checks, and its digest matches the spec page;
  - mutation: each deliberate breakage of bench-intel.sh is caught by the plan checks.

It does not build the image and does not run a row against a GPU. Exit status: 0 every check passed,
1 a check failed.
"""
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


def stub_bin(tmp):
    d = os.path.join(tmp, "stubs")
    os.makedirs(d)
    for name, body in {
        "ffmpeg": '#!/bin/sh\ncase "$*" in *-filters*) for f in oidn ort fsr2 libplacebo xpsnr; do echo " T.. $f  V->V stub"; done;; esac\n',
        "ffprobe": "#!/bin/sh\nexit 0\n",
        "curl": "#!/bin/sh\nexit 22\n",
        "jq": "#!/bin/sh\nexit 0\n",
    }.items():
        p = os.path.join(d, name)
        open(p, "w").write(body)
        os.chmod(p, 0o755)
    return d


def test_stub_run(tmp):
    stubs = stub_bin(tmp)
    clips = os.path.join(tmp, "clips")
    out = os.path.join(tmp, "out")
    os.makedirs(clips)
    env = clean_env({"BENCH_FFMPEG": os.path.join(stubs, "ffmpeg"), "BENCH_STOCK_FFMPEG": os.path.join(stubs, "ffmpeg"),
                     "BENCH_CLIPS": clips, "BENCH_OUT": out}, path=stubs)
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
    for n in ("ffprobe", "curl", "jq"):
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

    ignore = open(os.path.join(REPO, "docker", "intel", "Dockerfile.dockerignore")).read().split()
    need = ["scripts", "src", "web", "web/src", "shim", "assets", "ffmpeg", "docker", ".", "web/theme", "patches"]
    check("dockerignore keeps what the build needs", not [n for n in need if n in ignore], str(ignore))
    for rel in ["shim/jellyfin-ffmpeg-upscale", "scripts/bench-intel.sh", "scripts/ffmpeg-vendor.sh", "scripts/build-ffmpeg.sh",
                "scripts/install-shaders.sh", "scripts/jellyfin-plus-webinject", "scripts/build-web-panel.sh",
                "ffmpeg/gen_perm.py", "ffmpeg/fsr2compat.h", "web/theme/jellyfinplus.css", "web/package-lock.json",
                "assets/jellyfinplus.png", "assets/plugin-meta.json", "docker/intel/stage-plugin.sh"]:
        check(f"{rel} exists", os.path.isfile(os.path.join(REPO, rel)))
    for rel in ["scripts/bench-intel.sh", "docker/intel/stage-plugin.sh"]:
        p = subprocess.run([BASH, "-n", os.path.join(REPO, rel)], capture_output=True, text=True)
        check(f"bash -n {rel}", p.returncode == 0, p.stderr)
    check("the spec page has section 5.8", "### 5.8 " in spec)

    out = subprocess.run([BASH, "-c", f". {VENDOR}; ffmpeg_required_filters intel"], capture_output=True, text=True).stdout.split()
    check("the Intel filter list the image checks is oidn ort", out == ["oidn", "ort"], str(out))


def main():
    if not BASH:
        print("no bash")
        return 1
    with tempfile.TemporaryDirectory() as tmp:
        for part in (test_plan_and_refusals, test_stub_run, test_mutants):
            sub = os.path.join(tmp, part.__name__)
            os.makedirs(sub)
            part(sub)
        dockerfile_checks(tmp)
    print(f"{PASSES} checks passed, {len(FAILS)} failed")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
