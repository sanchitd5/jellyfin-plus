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
  - Intel with WITH_FSR2=1 keeps that plan and adds the no-CUDA fsr2 line (patch 0016, -DGU_NO_NVOF, the
    fsr2-nocuda marker), and still needs the FSR2 SDK files;
  - every refusal names the variable and happens before the plan, with nothing created on disk;
  - the real run reads the arrays and variables the plan prints (static wiring checks);
  - proxmox-build.sh requires the vendor's filter set (plus fsr2 on Intel when asked for), restores the
    previous binary when one is missing, refuses an unknown vendor before snapshotting, and passes the
    NVIDIA-only filters at 0 to an Intel build;
  - GPU_VENDOR=intel over a binary that lists optix is refused by both scripts, naming ALLOW_VENDOR_SWITCH,
    with the binary untouched; the override, nvidia, and an intel-only or unrunnable binary go through;
  - Intel needs libvpl-dev from apt (a stub apt-cache stands in for the host's), refused before the plan;
  - a refusal with BUILD unset leaves no temp dir, and the BUILD default and its trap sit after the plan exit;
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


def golden_intel_fsr2(prefix):
    # Intel plus fsr2: the Intel plan with fsr2=1 and the no-CUDA line (PR 6's path) as its last line.
    # The marker path is under PREFIX, which the harness sets.
    lines = [line.replace("fsr2=0", "fsr2=1") for line in GOLDEN_INTEL]
    lines.append("plan: fsr2: no CUDA: patch 0016, cflag -DGU_NO_NVOF, marker %s/fsr2-nocuda" % prefix)
    return lines


# Refused whatever else is set. WITH_FSR2 is not here: Intel allows it (see INTEL_FSR2_REFUSALS).
INTEL_REFUSALS = [
    ({"WITH_OPTIX": "1"}, "GPU_VENDOR=intel cannot be built with WITH_OPTIX=1"),
    ({"WITH_DLSS": "1"}, "GPU_VENDOR=intel cannot be built with WITH_DLSS=1"),
    ({"WITH_MAXINE_VSR": "1"}, "GPU_VENDOR=intel cannot be built with WITH_MAXINE_VSR=1"),
    ({"WITH_RTXDLPP": "1"}, "GPU_VENDOR=intel cannot be built with WITH_RTXDLPP=1"),
    ({"WITH_RTXVSR": "1"}, "GPU_VENDOR=intel cannot be built with WITH_RTXVSR=1"),
    ({"ORT_EP": "cuda"}, "GPU_VENDOR=intel cannot be built with ORT_EP=cuda"),
    ({"WITH_ORT": "0"}, "GPU_VENDOR=intel needs WITH_ORT=1"),
    ({"WITH_OIDN": "0"}, "GPU_VENDOR=intel needs WITH_OIDN=1"),
    ({"BRIDGE_TEST": "1"}, "GPU_VENDOR=intel cannot run BRIDGE_TEST=1"),
    # Allowing fsr2 must not let the other NVIDIA-only filters in beside it.
    ({"WITH_FSR2": "1", "WITH_OPTIX": "1"}, "GPU_VENDOR=intel cannot be built with WITH_OPTIX=1"),
    ({"WITH_FSR2": "1", "WITH_DLSS": "1"}, "GPU_VENDOR=intel cannot be built with WITH_DLSS=1"),
    ({"WITH_FSR2": "1", "WITH_MAXINE_VSR": "1"}, "GPU_VENDOR=intel cannot be built with WITH_MAXINE_VSR=1"),
    ({"WITH_FSR2": "1", "ORT_EP": "cuda"}, "GPU_VENDOR=intel cannot be built with ORT_EP=cuda"),
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


def fake_ffmpeg(directory, filters):
    # An existing binary that answers -filters with these names, as the real one lists them.
    os.makedirs(directory, exist_ok=True)
    path = os.path.join(directory, "ffmpeg")
    lines = "".join("echo ' T.. %s            V->V       stub'; " % f for f in filters.split())
    with open(path, "w") as f:
        f.write("#!/bin/sh\ncase \"$*\" in *-filters*) %sexit 0 ;; esac\nexit 0\n" % lines)
    os.chmod(path, 0o755)
    return path


def stub_apt_cache(tmp):
    # STUB_APT_LIBVPL: candidate (default), none ('Candidate: (none)') or unknown (no output at all).
    bindir = os.path.join(tmp, "aptbin")
    os.makedirs(bindir, exist_ok=True)
    path = os.path.join(bindir, "apt-cache")
    with open(path, "w") as f:
        f.write('#!/bin/sh\necho "$*" >> "$STUB_APT_LOG"\n'
                'case "${STUB_APT_LIBVPL:-candidate}" in\n'
                '  candidate) printf "libvpl-dev:\\n  Installed: (none)\\n  Candidate: 2:2023.3.0-1\\n" ;;\n'
                '  none) printf "libvpl-dev:\\n  Installed: (none)\\n  Candidate: (none)\\n" ;;\n'
                'esac\nexit 0\n')
    os.chmod(path, 0o755)


def plan_env(tmp, extra, with_build=True):
    env = {
        "PATH": os.path.join(tmp, "aptbin") + ":/usr/bin:/bin",
        "STUB_APT_LOG": os.path.join(tmp, "apt-calls"),
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


def fake_fsr2_sdk(tmp):
    # What the FSR2 preflight looks for: the header under FSR2_SDK and the archive under FSR2_LIB.
    sdk, lib = os.path.join(tmp, "fsr2-sdk"), os.path.join(tmp, "fsr2-lib")
    os.makedirs(sdk, exist_ok=True)
    os.makedirs(lib, exist_ok=True)
    for path in (os.path.join(sdk, "ffx_fsr2.h"), os.path.join(lib, "libffx_fsr2_vk.a")):
        with open(path, "w") as f:
            f.write("")
    return {"FSR2_SDK": sdk, "FSR2_LIB": lib}


def suite_build_ffmpeg(s, scripts, tmp):
    os.makedirs(os.path.join(tmp, "tmpdir"), exist_ok=True)
    stub_apt_cache(tmp)
    prefix = os.path.join(tmp, "prefix")
    fsr2 = fake_fsr2_sdk(tmp)

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
    expect_plan("intel with WITH_FSR2=1", dict(fsr2, GPU_VENDOR="intel", WITH_FSR2="1"), golden_intel_fsr2(prefix))

    # fsr2 on Intel still needs the SDK files: the preflight must not be skipped because the variant is new.
    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "intel", "WITH_FSR2": "1",
                                       "FSR2_SDK": os.path.join(tmp, "no-sdk"), "FSR2_LIB": fsr2["FSR2_LIB"]})
    s.check("intel+fsr2 without ffx_fsr2.h: refused naming FSR2_SDK",
            rc == 1 and "FSR2_SDK=" in err and "plan:" not in out, "rc=%s stderr=%r" % (rc, err))
    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "intel", "WITH_FSR2": "1",
                                       "FSR2_SDK": fsr2["FSR2_SDK"], "FSR2_LIB": os.path.join(tmp, "no-lib")})
    s.check("intel+fsr2 without libffx_fsr2_vk.a: refused naming FSR2_LIB",
            rc == 1 and "FSR2_LIB=" in err and "plan:" not in out, "rc=%s stderr=%r" % (rc, err))
    # No NVOF_SDK needed: there is no CUDA, so none of its headers are read.
    s.check("intel+fsr2 does not ask for NVOF_SDK", "NVOF_SDK" not in err)

    if not (shutil.which("clang-18", path="/usr/bin:/bin") or shutil.which("clang", path="/usr/bin:/bin")):
        rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "nvidia", "CLANG_CUDA": ""})
        s.check("nvidia without clang: no cuda-llvm flags", rc == 0 and "plan: cuda-llvm: none" in out, out)

    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "amd"})
    s.check("unknown vendor: refused naming GPU_VENDOR",
            rc == 1 and "GPU_VENDOR must be nvidia or intel, not 'amd'" in err and "plan:" not in out, "rc=%s %r" % (rc, err))

    for extra, message in INTEL_REFUSALS:
        env = dict(fsr2, GPU_VENDOR="intel", **extra)
        rc, out, err = plan(scripts, tmp, env)
        s.check("intel refuses %s" % "+".join("%s=%s" % kv for kv in extra.items()),
                rc == 1 and message in err and "plan:" not in out, "rc=%s stderr=%r" % (rc, err))
    s.check("refusals created nothing on disk", untouched(tmp))

    # What a refusal must not be mistaken for: the same variables are fine on the NVIDIA build.
    rc, out, err = plan(scripts, tmp, {"BRIDGE_TEST": "1"})
    s.check("nvidia accepts BRIDGE_TEST=1", rc == 0 and plan_lines(out) == GOLDEN_NVIDIA, err)

    # A mistyped GPU_VENDOR must not replace the NVIDIA binary: build-ffmpeg.sh installs over $PREFIX/ffmpeg.
    # The fake binary sits under its own prefix so untouched() keeps meaning "the script wrote nothing".
    optix_prefix = os.path.join(tmp, "optix-prefix")
    optix_bin = fake_ffmpeg(optix_prefix, "oidn optix ort fsr2 dlss")
    before = read(optix_bin)
    intel_only_prefix = os.path.join(tmp, "intel-prefix")
    fake_ffmpeg(intel_only_prefix, "oidn ort")
    broken_prefix = os.path.join(tmp, "broken-prefix")
    os.makedirs(broken_prefix)
    with open(os.path.join(broken_prefix, "ffmpeg"), "w") as f:
        f.write("OLD-BINARY")
    os.chmod(os.path.join(broken_prefix, "ffmpeg"), 0o755)

    for with_build in (True, False):
        tag = "" if with_build else " (BUILD not given)"
        rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "intel", "PREFIX": optix_prefix}, with_build)
        s.check("intel over an optix binary: refused naming ALLOW_VENDOR_SWITCH" + tag,
                rc == 1 and "ALLOW_VENDOR_SWITCH" in err and "plan:" not in out, "rc=%s stderr=%r" % (rc, err))
        s.check("the refused binary is untouched and nothing else was created" + tag,
                read(optix_bin) == before and untouched(tmp))
    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "intel", "PREFIX": optix_prefix, "ALLOW_VENDOR_SWITCH": "1"})
    s.check("intel over an optix binary with ALLOW_VENDOR_SWITCH=1: the intel plan",
            rc == 0 and plan_lines(out) == GOLDEN_INTEL, "rc=%s stderr=%r" % (rc, err))
    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "intel", "PREFIX": optix_prefix, "ALLOW_VENDOR_SWITCH": "0"})
    s.check("ALLOW_VENDOR_SWITCH=0 does not override", rc == 1 and "ALLOW_VENDOR_SWITCH" in err, "rc=%s" % rc)
    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "nvidia", "PREFIX": optix_prefix})
    s.check("nvidia over an optix binary: the nvidia plan", rc == 0 and plan_lines(out) == GOLDEN_NVIDIA, err)
    rc, out, err = plan(scripts, tmp, {"PREFIX": optix_prefix})
    s.check("GPU_VENDOR unset over an optix binary: the nvidia plan", rc == 0 and plan_lines(out) == GOLDEN_NVIDIA, err)
    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "intel", "PREFIX": intel_only_prefix})
    s.check("intel over an intel-only binary: the intel plan", rc == 0 and plan_lines(out) == GOLDEN_INTEL, err)
    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "intel", "PREFIX": broken_prefix})
    s.check("intel over a binary that will not run: the intel plan", rc == 0 and plan_lines(out) == GOLDEN_INTEL, err)

    # libvpl-dev: older bases lack it, and apt-get would fail minutes in.
    apt_log = os.path.join(tmp, "apt-calls")
    for mode in ("none", "unknown"):
        for with_build in (True, False):
            rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "intel", "STUB_APT_LIBVPL": mode}, with_build)
            s.check("intel with libvpl-dev %s in apt: refused naming libvpl-dev%s"
                    % (mode, "" if with_build else " (BUILD not given)"),
                    rc == 1 and "libvpl-dev" in err and "plan:" not in out and untouched(tmp), "rc=%s stderr=%r" % (rc, err))
    rc, out, err = plan(scripts, tmp, {"GPU_VENDOR": "intel", "STUB_APT_LIBVPL": "candidate"})
    s.check("intel with a libvpl-dev candidate: the intel plan", rc == 0 and plan_lines(out) == GOLDEN_INTEL, err)
    s.check("intel asked apt-cache about libvpl-dev", "libvpl-dev" in (read(apt_log) if os.path.exists(apt_log) else ""))
    if os.path.exists(apt_log):
        os.remove(apt_log)
    for extra in ({}, {"GPU_VENDOR": "nvidia"}, {"STUB_APT_LIBVPL": "none"}):
        rc, out, err = plan(scripts, tmp, extra)
        s.check("nvidia plan ignores apt-cache %s" % (extra or "(default)"), rc == 0 and plan_lines(out) == GOLDEN_NVIDIA, err)
    s.check("nvidia never called apt-cache", not os.path.exists(apt_log))

    # Every refusal that precedes the plan, with BUILD unset, must leave TMPDIR empty (no mktemp -d leak).
    for extra, message in [({"GPU_VENDOR": "amd"}, "GPU_VENDOR must be nvidia or intel")] + [
            (dict(fsr2, GPU_VENDOR="intel", **e), m) for e, m in INTEL_REFUSALS[:3]] + [
            ({"GPU_VENDOR": "intel", "WITH_FSR2": "1", "FSR2_SDK": os.path.join(tmp, "no-sdk"), "FSR2_LIB": fsr2["FSR2_LIB"]},
             "FSR2_SDK=")]:
        rc, out, err = plan(scripts, tmp, extra, with_build=False)
        s.check("refusal %r with BUILD unset leaves no temp dir" % message,
                rc == 1 and message in err and untouched(tmp), "rc=%s stderr=%r tmpdir=%r"
                % (rc, err, os.listdir(os.path.join(tmp, "tmpdir"))))


def suite_wiring(s, scripts):
    b = read(os.path.join(scripts, "build-ffmpeg.sh"))
    p = read(os.path.join(scripts, "proxmox-build.sh"))
    v = read(os.path.join(scripts, "ffmpeg-vendor.sh"))

    for name in ("build-ffmpeg.sh", "proxmox-build.sh", "ffmpeg-vendor.sh"):
        rc, _, err = run([BASH, "-n", os.path.join(scripts, name)], {"PATH": "/usr/bin:/bin"})
        s.check("bash -n %s" % name, rc == 0, err)

    # The real run must read the arrays the plan prints, or the plan is a story.
    for needle in ('"${APT_PKGS[@]}"', 'for p in "${VENDOR_PATCHES[@]}"', '"${HWACCEL_FLAGS[@]}"',
                   '${CUDA_LLVM_FLAGS[@]+"${CUDA_LLVM_FLAGS[@]}"}', 'if [[ "$NVCODEC" == "1" ]]; then'):
        s.check("build-ffmpeg.sh uses %s" % needle, b.count(needle) >= 1)
    # The array is empty on Intel; a bare "${arr[@]}" is an unbound variable under set -u on bash < 4.4.
    # Not runnable here (no old bash), so this is a text check.
    # The same holds for every other configure array that is empty on some build (OPTIX_FLAGS on Intel, ...).
    s.check("no bare \"${..._FLAGS[@]}\" expansion of a possibly-empty configure array is left",
            re.search(r'(?<!\+)"\$\{(CUDA_LLVM|OIDN|OPTIX|ORT|GAME|VSR)_FLAGS\[@\]\}"', b) is None)

    # BUILD's mktemp -d and the trap that removes it come after the plan exit, before apt: a refusal or a
    # kill in the preflight must have nothing to leak.
    plan_exit = b.find("    exit 0\nfi\n", b.find('if [[ "$PLAN_ONLY" == "1" ]]; then'))
    mk = b.find('BUILD="${BUILD:-$(mktemp -d)}"')
    trap = b.find("trap cleanup EXIT")
    apt = b.find("apt-get update")
    s.check("BUILD is created once, after the plan exit", b.count("$(mktemp -d)") == 1 and plan_exit != -1 and mk > plan_exit,
            "plan exit %d, mktemp %d" % (plan_exit, mk))
    s.check("the cleanup trap follows BUILD and precedes apt-get", mk != -1 and mk < trap < apt,
            "mktemp %d, trap %d, apt %d" % (mk, trap, apt))
    s.check("the CCACHE_BASEDIR export is not read before BUILD is set", b.find('export CCACHE_BASEDIR="$BUILD"') > mk)

    # One guard, defined once and called from both deploy paths.
    s.check("ffmpeg-vendor.sh defines the vendor-switch guard", "ffmpeg_vendor_switch_guard ()" in v)
    s.check("build-ffmpeg.sh calls the guard on its install target",
            'ffmpeg_vendor_switch_guard "$GPU_VENDOR" "$PREFIX/ffmpeg"' in b)
    s.check("proxmox-build.sh calls the guard before the .prev snapshot",
            0 < p.find('ffmpeg_vendor_switch_guard "$GPU_VENDOR" "$PATCHED_FFMPEG"') < p.find('cp -a "$PATCHED_FFMPEG"'))
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

    # The fsr2 plan line and the real run share one patch name, one flag and one marker path.
    s.check("the no-CUDA fsr2 patch name is spelled once, in a variable",
            b.count("0016-add-fsr2-filter-to-build-standalone.patch") == 1
            and 'FSR2_NOCUDA_PATCH="0016-add-fsr2-filter-to-build-standalone.patch"' in b
            and 'patch -p1 < "$HERE/ffmpeg/$FSR2_NOCUDA_PATCH"' in b)
    s.check("the -DGU_NO_NVOF flag is exported from its variable",
            'FSR2_NOCUDA_CFLAG="-DGU_NO_NVOF"' in b and 'export CFLAGS="$FSR2_NOCUDA_CFLAG ' in b
            and 'export CFLAGS="-DGU_NO_NVOF' not in b)
    s.check("the fsr2-nocuda marker is written and removed through its variable",
            'FSR2_NOCUDA_MARKER="$PREFIX/fsr2-nocuda"' in b and ': > "$FSR2_NOCUDA_MARKER"' in b
            and 'rm -f "$FSR2_NOCUDA_MARKER"' in b and b.count('"$PREFIX/fsr2-nocuda"') == 1)

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
    for name in ("ffmpeg-vendor.sh", "plugin-meta.sh"):
        shutil.copy(os.path.join(scripts, name), os.path.join(repo, "scripts"))
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

    def case(name, extra, filters, want_rc, out_has=(), err_has=(), restored=None, prev_kept=None, env_has=(), env_lacks=(),
             old_filters=None):
        os.makedirs(prefix, exist_ok=True)
        for leftover in (ffmpeg, ffmpeg + ".prev", env_out):
            if os.path.exists(leftover):
                os.remove(leftover)
        # The binary already on the box: opaque text by default, or one that lists old_filters for -filters.
        if old_filters is None:
            old = "OLD-BINARY"
            with open(ffmpeg, "w") as f:
                f.write(old)
        else:
            fake_ffmpeg(prefix, old_filters)
            old = read(ffmpeg)
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
                    (read(ffmpeg) == old) == restored)
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
    # fsr2 is not in Intel's default set, but a build that was asked for it must carry it.
    case("intel with WITH_FSR2=1, fsr2 missing", {"GPU_VENDOR": "intel", "WITH_FSR2": "1"}, "oidn ort", 1,
         err_has=["the rebuilt ffmpeg is missing: fsr2"], restored=True, env_has=["GPU_VENDOR=intel", "WITH_FSR2=1"])
    case("intel with WITH_FSR2=1, fsr2 present", {"GPU_VENDOR": "intel", "WITH_FSR2": "1"}, "oidn ort fsr2", 42,
         out_has=["all required filters present (oidn ort fsr2)"], prev_kept=True,
         env_has=["GPU_VENDOR=intel", "WITH_FSR2=1", "WITH_OPTIX=0", "WITH_DLSS=0"])
    case("unknown vendor", {"GPU_VENDOR": "amd"}, nv5, 1,
         err_has=["GPU_VENDOR must be nvidia or intel, not 'amd'"], restored=True, prev_kept=False)

    # A mistyped GPU_VENDOR must not replace the NVIDIA binary: refused before the snapshot and the build.
    nv_old = "oidn optix ort fsr2 dlss"
    case("intel over an optix binary", {"GPU_VENDOR": "intel"}, "oidn ort", 1, err_has=["ALLOW_VENDOR_SWITCH"],
         restored=True, prev_kept=False, old_filters=nv_old)
    case("intel over an optix binary, ALLOW_VENDOR_SWITCH=1", {"GPU_VENDOR": "intel", "ALLOW_VENDOR_SWITCH": "1"},
         "oidn ort", 42, prev_kept=True, env_has=["GPU_VENDOR=intel"], old_filters=nv_old)
    case("nvidia over an optix binary", {"GPU_VENDOR": "nvidia"}, nv5, 42, prev_kept=True,
         env_has=["GPU_VENDOR=nvidia"], old_filters=nv_old)
    case("default vendor over an optix binary", {}, nv5, 42, prev_kept=True, env_has=["GPU_VENDOR=nvidia"],
         old_filters=nv_old)
    case("intel over an intel-only binary", {"GPU_VENDOR": "intel"}, "oidn ort", 42, prev_kept=True,
         env_has=["GPU_VENDOR=intel"], old_filters="oidn ort")
    case("intel over a binary that will not run", {"GPU_VENDOR": "intel"}, "oidn ort", 42, prev_kept=True,
         env_has=["GPU_VENDOR=intel"])

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
     "for v in WITH_OPTIX WITH_DLSS WITH_MAXINE_VSR", "for v in WITH_DLSS WITH_MAXINE_VSR"),
    ("intel no longer refuses WITH_DLSS", "build-ffmpeg.sh",
     "for v in WITH_OPTIX WITH_DLSS WITH_MAXINE_VSR", "for v in WITH_OPTIX WITH_MAXINE_VSR"),
    ("intel refuses WITH_FSR2 again", "build-ffmpeg.sh",
     "for v in WITH_OPTIX WITH_DLSS WITH_MAXINE_VSR", "for v in WITH_OPTIX WITH_FSR2 WITH_DLSS WITH_MAXINE_VSR"),
    ("intel+fsr2 plan names the wrong patch", "build-ffmpeg.sh",
     'FSR2_NOCUDA_PATCH="0016-add-fsr2-filter-to-build-standalone.patch"',
     'FSR2_NOCUDA_PATCH="0004-add-fsr2-filter-to-build-standalone.patch"'),
    ("intel+fsr2 plan loses its line", "build-ffmpeg.sh",
     'if [[ "$WITH_FSR2" == "1" && "$ORT_EP" == "openvino" ]]; then\n        echo "plan: fsr2:',
     'if [[ "$WITH_FSR2" == "9" && "$ORT_EP" == "openvino" ]]; then\n        echo "plan: fsr2:'),
    ("the fsr2 marker is written elsewhere than the plan says", "build-ffmpeg.sh",
     ': > "$FSR2_NOCUDA_MARKER"', ': > "$PREFIX/fsr2-marker"'),
    ("the no-CUDA flag is dropped from the real run", "build-ffmpeg.sh",
     'export CFLAGS="$FSR2_NOCUDA_CFLAG ${CFLAGS:-}"', 'export CFLAGS="${CFLAGS:-}"'),
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
    ("proxmox-build.sh stops requiring fsr2 for intel+fsr2", "proxmox-build.sh",
     '[ "$GPU_VENDOR" = "intel" ] && [ "${WITH_FSR2:-0}" = "1" ] && required_filters="$required_filters fsr2"', ':'),
    ("proxmox-build.sh requires fsr2 of every intel build", "proxmox-build.sh",
     '[ "$GPU_VENDOR" = "intel" ] && [ "${WITH_FSR2:-0}" = "1" ] && required_filters',
     '[ "$GPU_VENDOR" = "intel" ] && required_filters'),
    ("proxmox-build.sh turns OptiX on for intel", "proxmox-build.sh",
     'WITH_OPTIX="${WITH_OPTIX:-$nv_default}"', 'WITH_OPTIX="${WITH_OPTIX:-1}"'),
    ("proxmox-build.sh no longer restores on a missing filter", "proxmox-build.sh",
     '            mv "$PATCHED_FFMPEG.prev" "$PATCHED_FFMPEG"\n            echo "    restored the previous binary; nothing was deployed" >&2\n        else\n            echo "    no previous',
     '            echo "    restored the previous binary; nothing was deployed" >&2\n        else\n            echo "    no previous'),
    ("proxmox-build.sh stops guarding the vendor switch", "proxmox-build.sh",
     'ffmpeg_vendor_switch_guard "$GPU_VENDOR" "$PATCHED_FFMPEG" || exit 1',
     'ffmpeg_vendor_switch_guard "$GPU_VENDOR" "$PATCHED_FFMPEG" || true'),
    ("build-ffmpeg.sh stops guarding the vendor switch", "build-ffmpeg.sh",
     'ffmpeg_vendor_switch_guard "$GPU_VENDOR" "$PREFIX/ffmpeg" || exit 1',
     'ffmpeg_vendor_switch_guard "$GPU_VENDOR" "$PREFIX/ffmpeg" || true'),
    ("the vendor-switch override is ignored", "ffmpeg-vendor.sh",
     '"${ALLOW_VENDOR_SWITCH:-0}" = "1"', '"${ALLOW_VENDOR_SWITCH:-0}" = "never"'),
    ("the vendor-switch guard looks for the wrong filter", "ffmpeg-vendor.sh",
     "'^ *[TSC.]* *optix '", "'^ *[TSC.]* *oidn '"),
    ("the vendor-switch guard also fires on nvidia", "ffmpeg-vendor.sh",
     '[ "${1:-}" = "intel" ] || return 0', ':'),
    ("the libvpl-dev preflight is removed", "build-ffmpeg.sh",
     "if command -v apt-cache >/dev/null 2>&1; then", "if false; then"),
    ("the libvpl-dev preflight accepts Candidate (none)", "build-ffmpeg.sh",
     '"$vpl_candidate" != "(none)"', '"$vpl_candidate" != "(never)"'),
    ("the libvpl-dev preflight accepts an empty answer", "build-ffmpeg.sh",
     '[[ -n "$vpl_candidate" && ', '[[ '),
    ("BUILD is created before the preflight again", "build-ffmpeg.sh",
     'PLAN_ONLY="${PLAN_ONLY:-0}"\n', 'PLAN_ONLY="${PLAN_ONLY:-0}"\nBUILD="${BUILD:-$(mktemp -d)}"\n'),
    ("the empty CUDA_LLVM_FLAGS expansion goes back to bare", "build-ffmpeg.sh",
     '${CUDA_LLVM_FLAGS[@]+"${CUDA_LLVM_FLAGS[@]}"}', '"${CUDA_LLVM_FLAGS[@]}"'),
]


def mutation_phase():
    caught = 0
    for name, fname, old, new in MUTANTS:
        with tempfile.TemporaryDirectory(prefix="test-ffmpeg-vendor-mutant.") as tmp:
            for f in ("build-ffmpeg.sh", "proxmox-build.sh", "ffmpeg-vendor.sh", "plugin-meta.sh"):
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
