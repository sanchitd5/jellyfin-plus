#!/usr/bin/env python3
"""The GPU_VENDOR variants of the ffmpeg build scripts (docs/architecture/GPU_VENDORS.md, PR 8).

    python3 -I scripts/test_build_ffmpeg_vendor.py

Needs no root, GPU, network, compiler or jfbin. It runs the real scripts/build-ffmpeg.sh with
PLAN_ONLY=1 (preflight, then the plan it would run, then exit before any apt, download or write) and
the real scripts/proxmox-build.sh against a stub repo with a fake id, dotnet and git. It proves:

  - the NVIDIA plan is a hand-written golden, the same with GPU_VENDOR unset as with GPU_VENDOR=nvidia,
    so the NVIDIA build keeps the apt list, patch order and configure flags it had;
  - the Intel plan: the VA-API/QSV packages and flags, no nv-codec-headers, no CUDA flags, patches 0008
    and 0009 only, required filters oidn and ort, no bridge test;
  - every refusal names the variable and happens before the plan, with nothing created on disk;
  - the real run reads the arrays the plan prints (static wiring checks);
  - proxmox-build.sh requires the vendor's filter set, restores the previous binary when one is
    missing, refuses an unknown vendor before snapshotting, and passes the NVIDIA-only filters at 0
    to an Intel build;
  - mutation: each deliberate breakage of the scripts is caught by this suite.

It does not run apt, patch, configure or make, and it does not build ffmpeg. Exit status: 0 every check
passed, 1 a check failed.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
BASH = shutil.which("bash")

BASE_APT = ("build-essential git curl ca-certificates pkg-config nasm yasm meson ninja-build cmake "
            "python3 libx264-dev libxcb1-dev libvulkan-dev glslang-tools")
NV_HWACCEL = "--enable-ffnvcodec --enable-cuda --enable-cuvid --enable-nvdec --enable-nvenc"

GOLDEN_NVIDIA = [
    "plan: vendor: nvidia",
    "plan: build: oidn=1 optix=0 ort=0 ort_ep=cuda fsr2=0 dlss=0 maxine_vsr=0 rtxdlpp=0 rtxvsr=0 crashlog=1",
    "plan: apt: " + BASE_APT,
    "plan: nv-codec-headers: yes n13.0.19.1",
    "plan: patches: 0006 0007 0008 0009 0010 0014 0011 0012 0013",
    "plan: hwaccel: " + NV_HWACCEL,
    "plan: cuda-llvm: --enable-cuda-llvm --nvcc=/fake/clang",
    "plan: required filters: oidn optix ort fsr2 dlss",
    "plan: bridge test default: 1",
]
# PR 5's NVIDIA-binary-with-OpenVINO build: only the ort fields and the bridge default move.
GOLDEN_NVIDIA_OPENVINO = [
    line.replace("ort=0 ort_ep=cuda", "ort=1 ort_ep=openvino").replace("bridge test default: 1", "bridge test default: 0")
    for line in GOLDEN_NVIDIA
]
GOLDEN_INTEL = [
    "plan: vendor: intel",
    "plan: build: oidn=1 optix=0 ort=1 ort_ep=openvino fsr2=0 dlss=0 maxine_vsr=0 rtxdlpp=0 rtxvsr=0 crashlog=1",
    "plan: apt: " + BASE_APT + " libva-dev libvpl-dev libdrm-dev",
    "plan: nv-codec-headers: no",
    "plan: patches: 0008 0009",
    "plan: hwaccel: --enable-vaapi --enable-libvpl --enable-libdrm",
    "plan: cuda-llvm: none",
    "plan: required filters: oidn ort",
    "plan: bridge test default: 0",
]

INTEL_REFUSALS = [
    ({"WITH_OPTIX": "1"}, "GPU_VENDOR=intel cannot be built with WITH_OPTIX=1"),
    ({"WITH_FSR2": "1"}, "GPU_VENDOR=intel cannot be built with WITH_FSR2=1"),
    ({"WITH_DLSS": "1"}, "GPU_VENDOR=intel cannot be built with WITH_DLSS=1"),
    ({"WITH_MAXINE_VSR": "1"}, "GPU_VENDOR=intel cannot be built with WITH_MAXINE_VSR=1"),
    ({"WITH_RTXDLPP": "1"}, "GPU_VENDOR=intel cannot be built with WITH_RTXDLPP=1"),
    ({"WITH_RTXVSR": "1"}, "GPU_VENDOR=intel cannot be built with WITH_RTXVSR=1"),
    ({"ORT_EP": "cuda"}, "GPU_VENDOR=intel cannot be built with ORT_EP=cuda"),
    ({"WITH_ORT": "0"}, "GPU_VENDOR=intel needs WITH_ORT=1"),
    ({"WITH_OIDN": "0"}, "GPU_VENDOR=intel needs WITH_OIDN=1"),
    ({"BRIDGE_TEST": "1"}, "GPU_VENDOR=intel cannot run BRIDGE_TEST=1"),
]


class Suite:
    def __init__(self, quiet):
        self.quiet = quiet
        self.passed = 0
        self.failed = []

    def check(self, name, ok, detail=""):
        if ok:
            self.passed += 1
        else:
            self.failed.append(name)
        if not self.quiet:
            print(("PASS  " if ok else "FAIL  ") + name + ("" if ok or not detail else "\n        " + detail))


def run(cmd, env, cwd=None):
    p = subprocess.run(cmd, env=env, cwd=cwd, capture_output=True, text=True, timeout=120)
    return p.returncode, p.stdout, p.stderr


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def plan_env(tmp, extra, with_build=True):
    env = {
        "PATH": "/usr/bin:/bin",
        "HOME": tmp,
        "TMPDIR": os.path.join(tmp, "tmpdir"),
        "PLAN_ONLY": "1",
        "CLANG_CUDA": "/fake/clang",
        "PREFIX": os.path.join(tmp, "prefix"),
    }
    if with_build:
        env["BUILD"] = os.path.join(tmp, "build")
    env.update(extra)
    return env


def plan(scripts, tmp, extra, with_build=True):
    return run([BASH, os.path.join(scripts, "build-ffmpeg.sh")], plan_env(tmp, extra, with_build))


def plan_lines(out):
    return [ln for ln in out.splitlines() if ln]


def untouched(tmp):
    return (not os.path.exists(os.path.join(tmp, "build")) and not os.path.exists(os.path.join(tmp, "prefix"))
            and os.listdir(os.path.join(tmp, "tmpdir")) == [])


def suite_build_ffmpeg(s, scripts, tmp):
    os.makedirs(os.path.join(tmp, "tmpdir"), exist_ok=True)

    def expect_plan(name, extra, golden, with_build=True):
        rc, out, err = plan(scripts, tmp, extra, with_build)
        s.check(name + ": exits 0 with no stderr", rc == 0 and err == "", "rc=%s stderr=%r" % (rc, err))
        s.check(name + ": plan matches the golden", plan_lines(out) == golden,
                "got:\n          " + "\n          ".join(plan_lines(out)))
        s.check(name + ": nothing created on disk", untouched(tmp), os.listdir(tmp).__repr__())

    expect_plan("nvidia (GPU_VENDOR unset)", {}, GOLDEN_NVIDIA)
    expect_plan("nvidia (GPU_VENDOR=nvidia)", {"GPU_VENDOR": "nvidia"}, GOLDEN_NVIDIA)
    expect_plan("nvidia with ORT_EP=openvino", {"WITH_ORT": "1", "ORT_EP": "openvino"}, GOLDEN_NVIDIA_OPENVINO)
    expect_plan("intel", {"GPU_VENDOR": "intel"}, GOLDEN_INTEL)
    expect_plan("intel (BUILD not given, no stray temp dir)", {"GPU_VENDOR": "intel"}, GOLDEN_INTEL, with_build=False)

    if not (shutil.which("clang-18", path="/usr/bin:/bin") or shutil.which("clang", path="/usr/bin:/bin")):
        rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "nvidia", "CLANG_CUDA": ""})
        s.check("nvidia without clang: no cuda-llvm flags", rc == 0 and "plan: cuda-llvm: none" in out, out)

    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "amd"})
    s.check("unknown vendor: refused naming GPU_VENDOR",
            rc == 1 and "GPU_VENDOR must be nvidia or intel, not 'amd'" in err and "plan:" not in out, "rc=%s %r" % (rc, err))

    for extra, message in INTEL_REFUSALS:
        env = dict(extra, GPU_VENDOR="intel")
        rc, out, err = plan(scripts, tmp, env)
        s.check("intel refuses %s" % "+".join("%s=%s" % kv for kv in extra.items()),
                rc == 1 and message in err and "plan:" not in out, "rc=%s stderr=%r" % (rc, err))
    s.check("refusals created nothing on disk", untouched(tmp))

    # What a refusal must not be mistaken for: the same variables are fine on the NVIDIA build.
    rc, out, err = plan(scripts, tmp, {"BRIDGE_TEST": "1"})
    s.check("nvidia accepts BRIDGE_TEST=1", rc == 0 and plan_lines(out) == GOLDEN_NVIDIA, err)


def suite_wiring(s, scripts):
    b = read(os.path.join(scripts, "build-ffmpeg.sh"))
    p = read(os.path.join(scripts, "proxmox-build.sh"))
    v = read(os.path.join(scripts, "ffmpeg-vendor.sh"))

    for name in ("build-ffmpeg.sh", "proxmox-build.sh", "ffmpeg-vendor.sh"):
        rc, _, err = run([BASH, "-n", os.path.join(scripts, name)], {"PATH": "/usr/bin:/bin"})
        s.check("bash -n %s" % name, rc == 0, err)

    # The real run must read the arrays the plan prints, or the plan is a story.
    for needle in ('"${APT_PKGS[@]}"', 'for p in "${VENDOR_PATCHES[@]}"', '"${HWACCEL_FLAGS[@]}"',
                   '"${CUDA_LLVM_FLAGS[@]}"', 'if [[ "$NVCODEC" == "1" ]]; then'):
        s.check("build-ffmpeg.sh uses %s" % needle, b.count(needle) >= 1)
    s.check("--enable-ffnvcodec is spelled once (the array)", b.count("--enable-ffnvcodec ") == 1)
    s.check("no literal patch line for 0006-0014 is left outside the loop",
            re.search(r'patch -p1 < "\$HERE/ffmpeg/00(0[6-9]|1[0-4])', b) is None)
    block = re.search(r'if \[\[ "\$NVCODEC" == "1" \]\]; then\n(.*?)\nfi\n', b, re.S)
    s.check("the nv-codec-headers clone sits inside the NVCODEC gate",
            block is not None and b.count("nv-codec-headers.git") == 1 and "nv-codec-headers.git" in block.group(1))
    s.check("the transpose_cuda check follows the CUDA-llvm flags, not the clang variable",
            'if [[ ${#CUDA_LLVM_FLAGS[@]} -gt 0 ]]; then' in b)
    s.check("the Intel verify captures ffmpeg's output before grepping it",
            'hwaccels_out=$(' in b and 'encoders_out=$(' in b
            and re.search(r'(-hwaccels|-encoders)[^\n]*\|\s*grep', b) is None)

    # One list of required filters, in one place.
    for text, name in ((b, "build-ffmpeg.sh"), (p, "proxmox-build.sh")):
        s.check("%s holds no copy of the NVIDIA filter list" % name, "oidn optix ort fsr2 dlss" not in text)
    s.check("proxmox-build.sh reads the required filters from ffmpeg-vendor.sh",
            'ffmpeg_required_filters "$GPU_VENDOR"' in p)
    s.check("ffmpeg-vendor.sh lists both vendors",
            'nvidia) echo "oidn optix ort fsr2 dlss"' in v and 'intel)  echo "oidn ort"' in v)


def suite_proxmox(s, scripts, tmp):
    bindir = os.path.join(tmp, "bin")
    os.makedirs(bindir)
    fakes = {
        "id": '#!/bin/sh\necho 0\n',
        "dotnet": '#!/bin/sh\nexit 0\n',
        "git": '#!/bin/sh\ncase "$*" in\n  *"remote get-url"*) echo "$STUB_REPO_URL" ;;\n  *" log "*) echo "abc1234 stub" ;;\nesac\nexit 0\n',
    }
    for name, body in fakes.items():
        path = os.path.join(bindir, name)
        with open(path, "w") as f:
            f.write(body)
        os.chmod(path, 0o755)

    jbin = os.path.join(tmp, "jfbin")
    os.makedirs(jbin)
    repo = os.path.join(tmp, "repo")
    os.makedirs(os.path.join(repo, ".git"))
    os.makedirs(os.path.join(repo, "scripts"))
    shutil.copy(os.path.join(scripts, "ffmpeg-vendor.sh"), os.path.join(repo, "scripts"))
    # The stub build records what proxmox-build.sh handed it and writes a fake ffmpeg listing STUB_FILTERS.
    stub_build = (
        '#!/bin/bash\n'
        'env | grep -E "^(GPU_VENDOR|WITH_[A-Z0-9_]+)=" | sort > "$STUB_ENV_OUT"\n'
        'mkdir -p "$PREFIX"\n'
        'cat > "$PREFIX/ffmpeg" <<\'EOF\'\n'
        '#!/bin/sh\n'
        'case "$*" in *-filters*) for f in $STUB_FILTERS; do echo " T.. $f            V->V       stub"; done ;; esac\n'
        'exit 0\n'
        'EOF\n'
        'chmod +x "$PREFIX/ffmpeg"\n'
    )
    stub_migrate = '#!/bin/bash\nexit 42\n'
    for name, body in (("build-ffmpeg.sh", stub_build), ("migrate-to-jellyfin-plus.sh", stub_migrate)):
        path = os.path.join(repo, "scripts", name)
        with open(path, "w") as f:
            f.write(body)
        os.chmod(path, 0o755)

    prefix = os.path.join(tmp, "ffprefix")
    ffmpeg = os.path.join(prefix, "ffmpeg")
    env_out = os.path.join(tmp, "stub-env")
    url = "https://example.invalid/stub"

    def case(name, extra, filters, want_rc, out_has=(), err_has=(), restored=None, prev_kept=None, env_has=(), env_lacks=()):
        os.makedirs(prefix, exist_ok=True)
        for leftover in (ffmpeg, ffmpeg + ".prev", env_out):
            if os.path.exists(leftover):
                os.remove(leftover)
        with open(ffmpeg, "w") as f:
            f.write("OLD-BINARY")
        env = {"PATH": bindir + ":/usr/bin:/bin", "HOME": tmp, "STUB_REPO_URL": url, "STUB_ENV_OUT": env_out,
               "STUB_FILTERS": filters}
        env.update(extra)
        rc, out, err = run([BASH, os.path.join(scripts, "proxmox-build.sh"), "--with-ffmpeg", "--repo=" + url,
                            "--repo-dir=" + repo, "--ffmpeg-prefix=" + prefix, "--jellyfin-bin=" + jbin,
                            "--dotnet-root=" + bindir], env)
        s.check("proxmox %s: exit %s" % (name, want_rc), rc == want_rc, "rc=%s\n  stdout=%r\n  stderr=%r" % (rc, out, err))
        for text in out_has:
            s.check("proxmox %s: stdout has %r" % (name, text), text in out, out)
        for text in err_has:
            s.check("proxmox %s: stderr has %r" % (name, text), text in err, err)
        if restored is not None:
            s.check("proxmox %s: previous binary %s" % (name, "restored" if restored else "not restored"),
                    (read(ffmpeg) == "OLD-BINARY") == restored)
        if prev_kept is not None:
            s.check("proxmox %s: rollback copy %s" % (name, "kept" if prev_kept else "absent"),
                    os.path.exists(ffmpeg + ".prev") == prev_kept)
        recorded = read(env_out).split() if os.path.exists(env_out) else []
        for item in env_has:
            s.check("proxmox %s: build-ffmpeg.sh got %s" % (name, item), item in recorded, " ".join(recorded))
        for item in env_lacks:
            s.check("proxmox %s: build-ffmpeg.sh did not get %s" % (name, item), item not in recorded)
        if not env_has and not env_lacks:
            s.check("proxmox %s: build-ffmpeg.sh never ran" % name, not recorded)

    nv5 = "oidn optix ort fsr2 dlss"
    case("nvidia, all five", {}, nv5, 42, out_has=["all five custom filters present"], prev_kept=True,
         env_has=["GPU_VENDOR=nvidia", "WITH_OIDN=1", "WITH_OPTIX=1", "WITH_ORT=1", "WITH_FSR2=1", "WITH_DLSS=1",
                  "WITH_RTXDLPP=1", "WITH_RTXVSR=1", "WITH_MAXINE_VSR=0"])
    case("nvidia, dlss missing", {}, "oidn optix ort fsr2", 1, err_has=["the rebuilt ffmpeg is missing: dlss"], restored=True,
         env_has=["GPU_VENDOR=nvidia"])
    case("intel, oidn and ort", {"GPU_VENDOR": "intel"}, "oidn ort", 42,
         out_has=["all required filters present (oidn ort)"], prev_kept=True,
         env_has=["GPU_VENDOR=intel", "WITH_OIDN=1", "WITH_ORT=1", "WITH_OPTIX=0", "WITH_FSR2=0", "WITH_DLSS=0",
                  "WITH_RTXDLPP=0", "WITH_RTXVSR=0", "WITH_MAXINE_VSR=0"])
    case("intel, ort missing", {"GPU_VENDOR": "intel"}, "oidn", 1, err_has=["the rebuilt ffmpeg is missing: ort"], restored=True,
         env_has=["GPU_VENDOR=intel"])
    case("intel, oidn missing", {"GPU_VENDOR": "intel"}, "ort", 1, err_has=["the rebuilt ffmpeg is missing: oidn"], restored=True,
         env_has=["GPU_VENDOR=intel"])
    case("intel, an explicit WITH_OPTIX=1 is passed on to be refused there", {"GPU_VENDOR": "intel", "WITH_OPTIX": "1"},
         "oidn ort", 42, env_has=["WITH_OPTIX=1"])
    case("unknown vendor", {"GPU_VENDOR": "amd"}, nv5, 1,
         err_has=["GPU_VENDOR must be nvidia or intel, not 'amd'"], restored=True, prev_kept=False)

    rc, out, err = run([BASH, os.path.join(scripts, "proxmox-build.sh"), "--help"], {"PATH": "/usr/bin:/bin"})
    s.check("proxmox --help documents GPU_VENDOR=intel and stops before set -e", rc == 0 and "GPU_VENDOR=intel" in out
            and "set -euo pipefail" not in out, out)


def run_suite(scripts, quiet):
    s = Suite(quiet)
    with tempfile.TemporaryDirectory(prefix="test-ffmpeg-vendor.") as tmp:
        for name, fn, args in (("build", suite_build_ffmpeg, (scripts,)), ("proxmox", suite_proxmox, (scripts,))):
            sub = os.path.join(tmp, name)
            os.makedirs(sub)
            if not quiet:
                print("\n" + {"build": "build-ffmpeg.sh PLAN_ONLY", "proxmox": "proxmox-build.sh against a stub repo"}[name])
            fn(s, *args, sub)
    if not quiet:
        print("\nstatic wiring")
    suite_wiring(s, scripts)
    return s


# (name, file, old text, new text): one deliberate breakage each. old must occur exactly once, so a
# mutant that stops matching after an edit fails loudly instead of testing nothing.
MUTANTS = [
    ("intel requires the NVIDIA five", "ffmpeg-vendor.sh",
     'intel)  echo "oidn ort"', 'intel)  echo "oidn optix ort fsr2 dlss"'),
    ("intel no longer refuses WITH_OPTIX", "build-ffmpeg.sh",
     "for v in WITH_OPTIX WITH_FSR2 WITH_DLSS WITH_MAXINE_VSR", "for v in WITH_FSR2 WITH_DLSS WITH_MAXINE_VSR"),
    ("intel applies the CUDA patch 0006", "build-ffmpeg.sh",
     "VENDOR_PATCHES=(0008-hwmap-chain-format.patch", "VENDOR_PATCHES=(0006-vulkan-to-cuda-hwmap.patch 0008-hwmap-chain-format.patch"),
    ("NVIDIA patch order changes", "build-ffmpeg.sh",
     "        0010-add-transpose-cuda-filter.patch\n        0014-cuda-vector-helpers-saturate-rintf.patch\n",
     "        0014-cuda-vector-helpers-saturate-rintf.patch\n        0010-add-transpose-cuda-filter.patch\n"),
    ("intel fetches nv-codec-headers", "build-ffmpeg.sh", "    NVCODEC=0\n", "    NVCODEC=1\n"),
    ("the default vendor becomes intel", "build-ffmpeg.sh",
     'GPU_VENDOR="${GPU_VENDOR:-nvidia}"', 'GPU_VENDOR="${GPU_VENDOR:-intel}"'),
    ("intel loses --enable-libdrm", "build-ffmpeg.sh",
     "HWACCEL_FLAGS=(--enable-vaapi --enable-libvpl --enable-libdrm)", "HWACCEL_FLAGS=(--enable-vaapi --enable-libvpl)"),
    ("NVIDIA loses --enable-cuvid", "build-ffmpeg.sh", "--enable-cuda --enable-cuvid ", "--enable-cuda "),
    ("proxmox-build.sh ignores the vendor's filter set", "proxmox-build.sh",
     'required_filters="$(ffmpeg_required_filters "$GPU_VENDOR")"', 'required_filters="oidn optix ort fsr2 dlss"'),
    ("proxmox-build.sh turns OptiX on for intel", "proxmox-build.sh",
     'WITH_OPTIX="${WITH_OPTIX:-$nv_default}"', 'WITH_OPTIX="${WITH_OPTIX:-1}"'),
    ("proxmox-build.sh no longer restores on a missing filter", "proxmox-build.sh",
     '            mv "$PATCHED_FFMPEG.prev" "$PATCHED_FFMPEG"\n            echo "    restored the previous binary; nothing was deployed" >&2\n        else\n            echo "    no previous',
     '            echo "    restored the previous binary; nothing was deployed" >&2\n        else\n            echo "    no previous'),
]


def mutation_phase():
    caught = 0
    for name, fname, old, new in MUTANTS:
        with tempfile.TemporaryDirectory(prefix="test-ffmpeg-vendor-mutant.") as tmp:
            for f in ("build-ffmpeg.sh", "proxmox-build.sh", "ffmpeg-vendor.sh"):
                shutil.copy(os.path.join(HERE, f), os.path.join(tmp, f))
            text = read(os.path.join(tmp, fname))
            if text.count(old) != 1:
                print("FAIL  mutant %r: its target text occurs %d times, not once" % (name, text.count(old)))
                continue
            with open(os.path.join(tmp, fname), "w", encoding="utf-8") as f:
                f.write(text.replace(old, new))
            s = run_suite(tmp, quiet=True)
        if s.failed:
            caught += 1
            print("CAUGHT  mutant %r: %d check(s) failed, first: %s" % (name, len(s.failed), s.failed[0]))
        else:
            print("FAIL  mutant %r survived: the suite passed against a broken script" % name)
    return caught


def main():
    if BASH is None:
        print("test_build_ffmpeg_vendor: bash not found", file=sys.stderr)
        return 2
    s = run_suite(HERE, quiet=False)
    print("\nchecks: %d passed, %d failed" % (s.passed, len(s.failed)))
    print("\nmutation (each breakage must fail the suite)")
    caught = mutation_phase()
    print("mutants: %d caught of %d" % (caught, len(MUTANTS)))
    return 0 if not s.failed and caught == len(MUTANTS) else 1


if __name__ == "__main__":
    sys.exit(main())
