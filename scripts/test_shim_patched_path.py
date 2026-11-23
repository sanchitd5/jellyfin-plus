#!/usr/bin/env python3
"""The patched ffmpeg path agrees everywhere it is written, and the shim says so when it is gone.

    python3 -I scripts/test_shim_patched_path.py

The plugin's IntelHardwareDecode chain (VA-API decode, hwmap into Vulkan) only runs on the patched
binary. Three files name that binary and cannot share one definition across the Python/C#/shell
boundary, so this test fails when they drift. A bridge chain that reaches the shim with no
executable patched binary would otherwise run on stock and die with "Conversion failed!", silently.
Exit status 0 when every check passed.
"""
import importlib.machinery
import importlib.util
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..")
SHIM = os.path.join(ROOT, "shim", "jellyfin-ffmpeg-upscale")
SHADER_LIBRARY = os.path.join(ROOT, "src", "patcher", "Transcoding", "ShaderLibrary.cs")
BUILD_SCRIPT = os.path.join(HERE, "proxmox-build.sh")

loader = importlib.machinery.SourceFileLoader("jellyfin_ffmpeg_upscale", SHIM)
spec = importlib.util.spec_from_loader(loader.name, loader)
shim = importlib.util.module_from_spec(spec)
loader.exec_module(shim)

failed = 0


def check(name, expected, actual):
    global failed
    ok = expected == actual
    failed += 0 if ok else 1
    print(("PASS  " if ok else "FAIL  ") + name + ("" if ok else "  (expected %r, got %r)" % (expected, actual)))


def read(path):
    with open(path) as fh:
        return fh.read()


# ShaderLibrary.PatchedFfmpegPath() is the parent of NeuralModelDirectory plus "ffmpeg".
model_dir = re.search(r'NeuralModelDirectory\s*=\s*"([^"]+)"', read(SHADER_LIBRARY))
plugin_path = os.path.join(os.path.dirname(model_dir.group(1)), "ffmpeg") if model_dir else None
check("the plugin names the patched binary", True, plugin_path is not None)

prefix = re.search(r'^FFMPEG_PREFIX="\$\{FFMPEG_PREFIX:-([^}]+)\}"', read(BUILD_SCRIPT), re.M)
build_path = prefix.group(1) + "/ffmpeg" if prefix else None
check("the build script names the patched binary", True, build_path is not None)

check("the shim and the plugin name the same binary", plugin_path, shim.PATCHED_FFMPEG)
check("the shim and the build script name the same binary", build_path, shim.PATCHED_FFMPEG)

# A bridge command as the plugin writes it: -hwaccel_device names the VA-API device, hwmap derives Vulkan.
BRIDGE = ["-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-init_hw_device", "vulkan=vk@va",
          "-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi",
          "-filter_hw_device", "vk", "-i", "in.mkv",
          "-vf", "hwmap=derive_device=vulkan,libplacebo=w=1920:h=1080,hwdownload,format=nv12", "out.m3u8"]
SOFTWARE = ["-init_hw_device", "vulkan=vk:0", "-filter_hw_device", "vk",
            "-i", "in.mkv", "-vf", "format=yuv420p,hwupload,libplacebo=w=1920:h=1080", "out.m3u8"]

logged = []
real_log, real_access = shim.log, shim.os.access
shim.log = lambda cfg, msg: logged.append(msg)
cfg = dict(shim.DEFAULTS)
try:
    shim.os.access = lambda path, mode: False
    binary, argv = shim.pick_binary(cfg, list(BRIDGE))
    check("no patched binary: the bridge chain still runs on stock", shim.REAL_FFMPEG, binary)
    check("no patched binary: the arguments are untouched", BRIDGE, argv)
    check("no patched binary: exactly one warning", 1, len(logged))
    warning = logged[0] if logged else ""
    check("the warning names the patched binary", True, shim.PATCHED_FFMPEG in warning)
    check("the warning says it is not executable", True, "not executable" in warning)
    check("the warning says the session will fail", True, "Conversion failed" in warning)

    del logged[:]
    shim.pick_binary(cfg, list(SOFTWARE))
    check("no patched binary: a software chain logs nothing", [], logged)

    shim.os.access = lambda path, mode: path == shim.PATCHED_FFMPEG
    binary, _ = shim.pick_binary(cfg, list(BRIDGE))
    check("with the patched binary the bridge chain routes to it", shim.PATCHED_FFMPEG, binary)
    check("with the patched binary there is no warning", False, any("not executable" in m for m in logged))
finally:
    shim.log, shim.os.access = real_log, real_access

print()
print("%d failed" % failed)
sys.exit(1 if failed else 0)
