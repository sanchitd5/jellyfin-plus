#!/usr/bin/env python3
"""The shim's Vulkan device arguments (docs/architecture/GPU_VENDORS.md, PR 3).

    python3 -I scripts/test-shim-device.py

With no render_node the command is exactly what it was before the key existed; a plain render node
path derives the Vulkan device from it; anything else is ignored rather than put on a command line.
Exit status 0 when every check passed.
"""
import importlib.machinery
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SHIM = os.path.join(HERE, "..", "shim", "jellyfin-ffmpeg-upscale")

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


ARGS = ["-i", "in.mkv", "out.m3u8"]
VK0 = ["-init_hw_device", "vulkan=vk:0", "-filter_hw_device", "vk"]
TAIL = ["-i", "in.mkv", "-vf", "CHAIN", "out.m3u8"]

cfg = dict(shim.DEFAULTS)
check("default render_node is unset", None, cfg["render_node"])
check("unset keeps vk:0, byte for byte", VK0 + TAIL, shim.rewrite_args(cfg, list(ARGS), "CHAIN"))

cfg["render_node"] = "/dev/dri/renderD129"
check("a render node derives the Vulkan device from it",
      ["-init_hw_device", "drm=dr:/dev/dri/renderD129", "-init_hw_device", "vulkan=vk@dr",
       "-filter_hw_device", "vk"] + TAIL,
      shim.rewrite_args(cfg, list(ARGS), "CHAIN"))

for bad in ("", "renderD129", "/dev/dri/card0", "/dev/dri/renderD129,x", "/dev/dri/renderD12 9", 7, ["/dev/dri/renderD128"]):
    cfg["render_node"] = bad
    check("render_node %r is ignored, vk:0 kept" % (bad,), VK0 + TAIL, shim.rewrite_args(cfg, list(ARGS), "CHAIN"))

# Jellyfin's own device arguments are dropped whichever device is chosen.
cfg["render_node"] = "/dev/dri/renderD128"
out = shim.rewrite_args(cfg, ["-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-filter_hw_device", "va"] + ARGS, "CHAIN")
check("Jellyfin's own -init_hw_device and -filter_hw_device are replaced, not kept beside ours",
      ["-init_hw_device", "drm=dr:/dev/dri/renderD128", "-init_hw_device", "vulkan=vk@dr",
       "-filter_hw_device", "vk"] + TAIL, out)

print()
print("%d failed" % failed)
sys.exit(1 if failed else 0)
