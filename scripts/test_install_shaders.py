#!/usr/bin/env python3
"""scripts/install-shaders.sh derives the RCAS shaders through shaders/make-rcas.sh.

    python3 -I scripts/test_install_shaders.py

Needs no GPU, docker or network. Every fetch in install-shaders.sh is replaced by its local-copy
override, and each stub carries only the marker lines the script greps for. It proves:

  - the script finds make-rcas.sh (it once looked in scripts/ and died with "No such file");
  - RCAS-2.0 / 1.7 / 1.4 land in the output dir, non-empty, each with its own SHARPNESS;
  - each keeps the MIT attribution of the FSR.glsl it came from, and no EASUTEX reference is left;
  - two runs are byte-identical;
  - mutation: an FSR.glsl with no second //!HOOK block, or no licence text, is refused;
  - the FSRCNNX and Anime4K files are fetched from the pinned release / commit, sha256-checked,
    header-checked, and every shader the Arc Dockerfile checks or ShaderLibrary.cs maps is installed.

Exit status: 0 every check passed, 1 a check failed.
"""
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
BASH = shutil.which("bash")
INSTALL = os.path.join(HERE, "install-shaders.sh")
MAKE_RCAS = os.path.join(REPO, "shaders", "make-rcas.sh")

MIT = "Permission is hereby granted, free of charge, to any person obtaining a copy"
FSR_HEADER = (
    "// FidelityFX FSR v1.0.2 by AMD\n"
    "// Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved.\n"
    f"// {MIT}\n"
    "// of this software, subject to the conditions of the MIT licence.\n"
    "\n"
)
FSR_BODY = (
    "//!DESC FidelityFX Super Resolution v1.0.2 (EASU)\n"
    "//!HOOK LUMA\n"
    "//!BIND HOOKED\n"
    "//!SAVE EASUTEX\n"
    "//!WIDTH OUTPUT.w\n"
    "vec4 hook() { return HOOKED_tex(HOOKED_pos); }\n"
    "//!DESC FidelityFX Super Resolution v1.0.2 (RCAS)\n"
    "//!HOOK LUMA\n"
    "//!BIND EASUTEX\n"
    "//!WIDTH EASUTEX.w\n"
    "//!HEIGHT EASUTEX.h\n"
    "#define SHARPNESS 0.2 // lower is sharper\n"
    "vec4 hook() { return EASUTEX_tex(EASUTEX_pos); }\n"
)
FSR_STUB = FSR_HEADER + FSR_BODY

LGPL = "// GNU Lesser General Public License\n"
NIS_SCALER = f"// NVIDIA Image Scaling v1.0.2\n// {MIT}\n//!HOOK LUMA\nvoid hook() {{}}\n"
NIS_SHARPEN = (f"// NVIDIA Image Scaling v1.0.2\n// {MIT}\n//!HOOK LUMA\n"
               "//!WHEN OUTPUT.w LUMA.w / 1.0 > !\n#define SHARPNESS 0.5\nvoid hook() {}\n")
SSSR = LGPL + "// SSimSuperRes by Shiandow\n//!HOOK POSTKERNEL\n//!WHEN NATIVE_CROPPED.h OUTPUT.h <\n"
KRIG = LGPL + "// KrigBilateral by Shiandow\n//!HOOK CHROMA\n//!WHEN CHROMA.w LUMA.w <\n"
RAVU = (LGPL + "//!DESC RAVU-Zoom (luma, r3)\n//!HOOK LUMA\n"
        "//!WHEN HOOKED.w OUTPUT.w < HOOKED.h OUTPUT.h < *\n")
CUNNY = (LGPL + "// CuNNy (dp4a) by funnyplanter\n//!HOOK LUMA\n"
         "//!WHEN OUTPUT.w LUMA.w / 1.3 > OUTPUT.h LUMA.h / 1.3 > *\n")
CUNNY_FILES = ["CuNNy-fast-SOFT-Q.glsl", "CuNNy-4x16-SOFT-Q.glsl",
               "CuNNy-4x32-SOFT-Q.glsl", "CuNNy-4x16-DS-Q.glsl"]

FSRCNNX_FILES = {"FSRCNNX_x2_8-0-4-1.glsl": "FSRCNNX_8_SHA256", "FSRCNNX_x2_16-0-4-1.glsl": "FSRCNNX_16_SHA256"}
ANIME4K_FILES = {"Anime4K_Upscale_CNN_x2_S.glsl": ("S", "ANIME4K_S_SHA256"),
                 "Anime4K_Upscale_CNN_x2_M.glsl": ("M", "ANIME4K_M_SHA256")}
# The real header wraps "GNU Lesser General Public" / "License" across lines 4-5.
FSRCNNX = ("// Copyright (C) 2017-2021 igv\n//\n"
           "// This program is free software; you can redistribute it and/or\n"
           "// modify it under the terms of the GNU Lesser General Public\n"
           "// License as published by the Free Software Foundation; either\n"
           "//!HOOK LUMA\nvoid hook() {}\n")


def anime4k(variant):
    return (f"// MIT License\n// Copyright (c) 2019-2021 bloc97\n// {MIT}\n"
            f"//!DESC Anime4K-v3.2-Upscale-CNN-x2-({variant})-Conv-4x3x3x3\n//!HOOK MAIN\nvoid hook() {{}}\n")


FSRCNNX_BASE = "https://github.com/igv/FSRCNN-TensorFlow/releases/download/1.1"
ANIME4K_REV = "4029bf701ecaa15f163cdc49cffe5501c1acf410"
ANIME4K_BASE = f"https://raw.githubusercontent.com/bloc97/Anime4K/{ANIME4K_REV}/glsl/Upscale"
PINNED_SHA = {
    "FSRCNNX_x2_8-0-4-1.glsl": "e800dbc5c1c95185cc82216c597724533ff5f2880179f256eef600f03e8dc2ae",
    "FSRCNNX_x2_16-0-4-1.glsl": "d5a24a271e5d9a3f7f7a053b150c460a44c25b3cf7f770857d57cc3a2e1c9965",
    "Anime4K_Upscale_CNN_x2_S.glsl": "4c53ec2e287908f7ee7bcb266b0170421626d663576468b7d7dafc62962649a4",
    "Anime4K_Upscale_CNN_x2_M.glsl": "716e02098a68f0d648761f2b96b4dd139e1cb09b174bb369fca3aa34328fff7e",
}

# Shader names the plugin or the Arc image reference that install-shaders.sh deliberately does not
# write. CAS-*: this repo's own rollback shaders under shaders/, installed by hand. FSRCNN_x2_r1_32:
# accepted by the API but no upstream release carries it.
NOT_INSTALLED = {"CAS-low.glsl", "CAS-medium.glsl", "CAS-high.glsl", "FSRCNN_x2_r1_32-0-2.glsl"}

NIS_BASE = "https://gist.githubusercontent.com/agyild/7e8951915b2bf24526a9343d951db214/raw"
NIS_REV = "05f00864228871ffd157daa9beb2db8fa7412cfa"
NIS_SCALER_SHA = "bac778ec0108d272e4dad97794a3c257e65bbebd52aa762f95bce930cbd6b73c"
NIS_SHARPEN_SHA = "3d51ac2a9da18284c1f77e46a019e783eac0c609924e6b32e8011889f54f9a12"

FAILS = []


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("" if ok or not detail else f"  -- {detail}"))
    if not ok:
        FAILS.append(name)


def write(path, text):
    with open(path, "w") as f:
        f.write(text)


def read(path):
    with open(path) as f:
        return f.read()


def sha(text):
    return hashlib.sha256(text.encode()).hexdigest()


def stubs(root, fsr=FSR_STUB, scaler=NIS_SCALER, sharpen=NIS_SHARPEN, scaler_sha=None, sharpen_sha=None,
          texts=None, shas=None):
    """Write every stub input under root and return the env that points install-shaders.sh at them.

    The stubs are not the pinned files, so the expected sha256 is overridden to match them unless
    a mutation passes a different one. texts / shas override one FSRCNNX or Anime4K file by name."""
    texts, shas = texts or {}, shas or {}
    nis, cunny = os.path.join(root, "nis"), os.path.join(root, "cunny")
    fsrcnnx_dir, anime_dir = os.path.join(root, "fsrcnnx"), os.path.join(root, "anime4k")
    for d in (nis, cunny, fsrcnnx_dir, anime_dir):
        os.makedirs(d)
    shaenv = {}
    for name, var in FSRCNNX_FILES.items():
        text = texts.get(name, FSRCNNX)
        write(os.path.join(fsrcnnx_dir, name), text)
        shaenv[var] = shas.get(name) or sha(text)
    for name, (variant, var) in ANIME4K_FILES.items():
        text = texts.get(name, anime4k(variant))
        write(os.path.join(anime_dir, name), text)
        shaenv[var] = shas.get(name) or sha(text)
    write(os.path.join(root, "FSR.glsl"), fsr)
    write(os.path.join(nis, "NVScaler.glsl"), scaler)
    write(os.path.join(nis, "NVSharpen.glsl"), sharpen)
    for name in CUNNY_FILES:
        write(os.path.join(cunny, name), CUNNY)
    write(os.path.join(root, "sssr.glsl"), SSSR)
    write(os.path.join(root, "krig.glsl"), KRIG)
    write(os.path.join(root, "ravu.glsl"), RAVU)
    env = dict(os.environ)
    env.update(shaenv)
    env.update(FSR_GLSL=os.path.join(root, "FSR.glsl"), NIS_DIR=nis, CUNNY_DIR=cunny,
               FSRCNNX_DIR=fsrcnnx_dir, ANIME4K_DIR=anime_dir,
               SSSR_GLSL=os.path.join(root, "sssr.glsl"), KRIG_GLSL=os.path.join(root, "krig.glsl"),
               RAVU_ZOOM_HOOK=os.path.join(root, "ravu.glsl"),
               NIS_SCALER_SHA256=scaler_sha or sha(scaler), NIS_SHARPEN_SHA256=sharpen_sha or sha(sharpen))
    return env


def fake_curl(root, env):
    """Put a curl on PATH that logs each URL and serves the stub named by the URL's basename."""
    bindir = os.path.join(root, "bin")
    os.makedirs(bindir)
    log = os.path.join(root, "curl.log")
    script = os.path.join(bindir, "curl")
    dirs = " ".join(f'"{env[k]}"' for k in ("NIS_DIR", "FSRCNNX_DIR", "ANIME4K_DIR"))
    write(script, '#!/bin/bash\n'
                  'url=$2; dest=$4\n'
                  f'echo "$url" >> "{log}"\n'
                  f'for d in {dirs}; do\n'
                  '  [ -f "$d/$(basename "$url")" ] && exec cp "$d/$(basename "$url")" "$dest"\n'
                  'done\n'
                  'exit 22\n')
    os.chmod(script, 0o755)
    env = dict(env)
    for k in ("NIS_DIR", "FSRCNNX_DIR", "ANIME4K_DIR"):
        env.pop(k)
    env["PATH"] = bindir + os.pathsep + env["PATH"]
    return env, log


def install(env, out):
    return subprocess.run([BASH, INSTALL, out], env=env, capture_output=True, text=True, timeout=120)


def main():
    check("bash is available", BASH is not None)
    check("shaders/make-rcas.sh exists", os.path.isfile(MAKE_RCAS))
    if FAILS:
        return 1

    with tempfile.TemporaryDirectory() as tmp:
        env = stubs(os.path.join(tmp, "in"))
        out1, out2 = os.path.join(tmp, "out1"), os.path.join(tmp, "out2")
        r = install(env, out1)
        check("install-shaders.sh exits 0 on stub inputs", r.returncode == 0, r.stderr.strip()[-300:])
        check("it does not look for make-rcas.sh in the wrong directory",
              "No such file" not in r.stderr, r.stderr.strip()[-300:])

        for s in ("2.0", "1.7", "1.4"):
            p = os.path.join(out1, f"RCAS-{s}.glsl")
            text = read(p) if os.path.isfile(p) else ""
            check(f"RCAS-{s}.glsl is installed and non-empty", bool(text.strip()))
            check(f"RCAS-{s}.glsl has SHARPNESS {s}", f"#define SHARPNESS {s} " in text)
            check(f"RCAS-{s}.glsl keeps the MIT attribution", MIT in text)
            check(f"RCAS-{s}.glsl keeps the FSR copyright line", "Copyright (c) 2021 Advanced Micro" in text)
            check(f"RCAS-{s}.glsl has no EASUTEX left", "EASUTEX" not in text)
            check(f"RCAS-{s}.glsl is the RCAS pass only", "(EASU)" not in text and "SAVE" not in text)
            check(f"RCAS-{s}.glsl reads HOOKED", "//!BIND HOOKED" in text and "HOOKED_tex" in text)

        r2 = install(env, out2)
        check("a second run also exits 0", r2.returncode == 0, r2.stderr.strip()[-300:])
        same = all(
            os.path.isfile(os.path.join(out1, f"RCAS-{s}.glsl"))
            and os.path.isfile(os.path.join(out2, f"RCAS-{s}.glsl"))
            and read(os.path.join(out1, f"RCAS-{s}.glsl")) == read(os.path.join(out2, f"RCAS-{s}.glsl"))
            for s in ("2.0", "1.7", "1.4"))
        check("two runs give byte-identical RCAS files", same)

        # mutation: an FSR.glsl that is not the shape make-rcas.sh expects must be refused.
        mut = os.path.join(tmp, "mut")
        easu_only = FSR_BODY[:FSR_BODY.index("//!DESC", 1)]
        env_m = stubs(os.path.join(mut, "in"), fsr=FSR_HEADER + easu_only)
        r = install(env_m, os.path.join(mut, "out"))
        check("mutation: no second //!HOOK block is refused", r.returncode != 0, f"rc={r.returncode}")

        # NIS: the fetch is pinned to one gist revision, each file to its sha256, and the header is kept.
        script = read(INSTALL)
        check("NIS fetch is pinned to the verified gist revision", NIS_REV in script
              and f"agyild/7e8951915b2bf24526a9343d951db214/raw/$NIS_REV" in script)
        check("NIS sha256 pins are the verified values",
              NIS_SCALER_SHA in script and NIS_SHARPEN_SHA in script)

        net = os.path.join(tmp, "net")
        env_n, log = fake_curl(net, stubs(os.path.join(net, "in")))
        r = install(env_n, os.path.join(net, "out"))
        check("NIS fetched over the network path installs", r.returncode == 0, r.stderr.strip()[-300:])
        urls = read(log).split() if os.path.isfile(log) else []
        want = [f"{NIS_BASE}/{NIS_REV}/{n}" for n in ("NVScaler.glsl", "NVSharpen.glsl")]
        check("NIS URLs carry the pinned revision", urls[:2] == want, f"{urls}")
        want_sr = ([f"{FSRCNNX_BASE}/{n}" for n in FSRCNNX_FILES]
                   + [f"{ANIME4K_BASE}/{n}" for n in ANIME4K_FILES])
        check("FSRCNNX URLs carry the release tag and Anime4K URLs the pinned commit",
              urls[2:] == want_sr, f"{urls[2:]}")
        check("NVSharpen-0.25 and -0.65 are installed with the //!WHEN guard removed",
              all(os.path.isfile(os.path.join(net, "out", f"NVSharpen-{s}.glsl"))
                  and "!WHEN" not in read(os.path.join(net, "out", f"NVSharpen-{s}.glsl"))
                  for s in ("0.25", "0.65")))

        for label, kw in (
                ("NVScaler with a v1.0.3 header", dict(scaler=NIS_SCALER.replace("v1.0.2", "v1.0.3"))),
                ("NVSharpen with a v1.0.3 header", dict(sharpen=NIS_SHARPEN.replace("v1.0.2", "v1.0.3"))),
                ("NVScaler with a wrong sha256", dict(scaler_sha="0" * 64)),
                ("NVSharpen with a wrong sha256", dict(sharpen_sha="0" * 64))):
            d = os.path.join(tmp, "mut-" + label.replace(" ", "-"))
            r = install(stubs(os.path.join(d, "in"), **kw), os.path.join(d, "out"))
            check(f"mutation: {label} is refused", r.returncode != 0, f"rc={r.returncode}")

        # the header check must still fire when the sha256 is satisfied (a hash-matching wrong file).
        bad = NIS_SCALER.replace("v1.0.2", "v1.0.3")
        d = os.path.join(tmp, "mut-header-only")
        r = install(stubs(os.path.join(d, "in"), scaler=bad, scaler_sha=sha(bad)), os.path.join(d, "out"))
        check("mutation: a sha-matching file with the wrong header is refused",
              r.returncode != 0 and "v1.0.2" in r.stderr, r.stderr.strip()[-200:])

        # FSRCNNX / Anime4K: pinned sha256 values, and each family's header and hook markers.
        check("FSRCNNX and Anime4K pins in the script are the verified sha256 values",
              all(v in script for v in PINNED_SHA.values()) and ANIME4K_REV in script)

        out_sr = os.path.join(tmp, "out1")
        check("FSRCNNX and Anime4K files are installed and non-empty",
              all(os.path.isfile(os.path.join(out_sr, n)) and read(os.path.join(out_sr, n)).strip()
                  for n in PINNED_SHA))

        # Every shader the Arc image checks for, and every one the plugin maps a level to, must be
        # written by the installer. The Arc build once failed on its last step on a file nobody wrote.
        dockerfile = read(os.path.join(REPO, "docker", "intel", "Dockerfile"))
        library = read(os.path.join(REPO, "src", "patcher", "Transcoding", "ShaderLibrary.cs"))
        wanted = set(re.findall(r"test -f /usr/share/jellyfin-shaders/(\S+\.glsl)", dockerfile))
        wanted |= set(re.findall(r'\]\s*=\s*"([^"/]+\.glsl)"', library))
        check("the Dockerfile and ShaderLibrary name shaders at all", len(wanted) > 15, f"{sorted(wanted)}")
        missing = sorted(n for n in wanted - NOT_INSTALLED if not os.path.isfile(os.path.join(out_sr, n)))
        check("every shader the Dockerfile checks and ShaderLibrary maps is installed", not missing,
              f"missing: {missing}")
        check("the not-installed exemptions are real references",
              NOT_INSTALLED <= wanted, f"{sorted(NOT_INSTALLED - wanted)}")

        for label, kw in (
                ("FSRCNNX 8 with a wrong sha256", dict(shas={"FSRCNNX_x2_8-0-4-1.glsl": "0" * 64})),
                ("FSRCNNX 16 with a wrong sha256", dict(shas={"FSRCNNX_x2_16-0-4-1.glsl": "0" * 64})),
                ("Anime4K S with a wrong sha256", dict(shas={"Anime4K_Upscale_CNN_x2_S.glsl": "0" * 64})),
                ("Anime4K M with a wrong sha256", dict(shas={"Anime4K_Upscale_CNN_x2_M.glsl": "0" * 64})),
                ("FSRCNNX with no igv copyright",
                 dict(texts={"FSRCNNX_x2_8-0-4-1.glsl": FSRCNNX.replace("igv", "someone")})),
                ("FSRCNNX with no LGPL header",
                 dict(texts={"FSRCNNX_x2_16-0-4-1.glsl": FSRCNNX.replace("GNU Lesser", "GNU Lesser-less")})),
                ("Anime4K with no bloc97 copyright",
                 dict(texts={"Anime4K_Upscale_CNN_x2_S.glsl": anime4k("S").replace("bloc97", "other")})),
                ("Anime4K S that is really the M network",
                 dict(texts={"Anime4K_Upscale_CNN_x2_S.glsl": anime4k("M")})),
                ("Anime4K with no MIT text",
                 dict(texts={"Anime4K_Upscale_CNN_x2_M.glsl": anime4k("M").replace(MIT, "none")})),
                ("Anime4K that no longer hooks MAIN",
                 dict(texts={"Anime4K_Upscale_CNN_x2_M.glsl": anime4k("M").replace("HOOK MAIN", "HOOK LUMA")}))):
            d = os.path.join(tmp, "mut-" + label.replace(" ", "-"))
            r = install(stubs(os.path.join(d, "in"), **kw), os.path.join(d, "out"))
            check(f"mutation: {label} is refused", r.returncode != 0, f"rc={r.returncode}")

        nolic = os.path.join(tmp, "nolic")
        os.makedirs(nolic)
        write(os.path.join(nolic, "FSR.glsl"), FSR_STUB.replace(MIT, "no licence here"))
        r = subprocess.run([BASH, MAKE_RCAS, os.path.join(nolic, "FSR.glsl"), nolic],
                           capture_output=True, text=True, timeout=60)
        check("mutation: make-rcas.sh refuses an FSR.glsl with no MIT text", r.returncode != 0,
              f"rc={r.returncode}")

    print()
    print("FAILED: " + ", ".join(FAILS) if FAILS else "all checks passed")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
