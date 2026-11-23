#!/usr/bin/env python3
"""The shim's filter chain, per GPU vendor (docs/architecture/GPU_VENDORS.md 5.3).

    python3 -I scripts/test_shim_chain.py

On Intel (the render node's sysfs vendor is 0x8086) libplacebo outputs RGBA and the chain converts to
yuv420p on the CPU, because ANV's multi-planar YUV output from libplacebo comes back green. Every
other case (no render node, an invalid one, NVIDIA, unreadable sysfs) keeps the chain string it had
before, byte for byte. Exit status 0 when every check passed.
"""
import importlib.machinery
import importlib.util
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SHIM = os.path.join(HERE, "..", "shim", "jellyfin-ffmpeg-upscale")

# Keep the compiled shim out of the working tree.
sys.dont_write_bytecode = True
loader = importlib.machinery.SourceFileLoader("jellyfin_ffmpeg_upscale", SHIM)
spec = importlib.util.spec_from_loader(loader.name, loader)
shim = importlib.util.module_from_spec(spec)
loader.exec_module(shim)

failed = 0
passed = 0


def check(name, ok, detail=""):
    global failed, passed
    if ok:
        passed += 1
    else:
        failed += 1
    print(("PASS  " if ok else "FAIL  ") + name + ("" if ok else "  " + detail))


def equal(name, expected, actual):
    check(name, expected == actual, "(expected %r, got %r)" % (expected, actual))


def fake_sysfs(root, node, vendor):
    """<root>/<node>/device/vendor holding the PCI vendor id text, as the kernel writes it."""
    d = os.path.join(root, node, "device")
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, "vendor"), "w") as fh:
        fh.write(vendor + "\n")


def chain(cfg, ratio=2.0):
    return shim.build_chain(cfg, 1920, 1080, ratio)


NVIDIA_PLAIN = "format=yuv420p,hwupload,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos,hwdownload,format=yuv420p"

with tempfile.TemporaryDirectory() as tmp:
    drm = os.path.join(tmp, "drm")
    shader = os.path.join(tmp, "net.glsl")
    sharpener = os.path.join(tmp, "rcas.glsl")
    for p in (shader, sharpener):
        with open(p, "w") as fh:
            fh.write("//\n")
    fake_sysfs(drm, "renderD128", "0x8086")
    fake_sysfs(drm, "renderD129", "0x10de")
    fake_sysfs(drm, "renderD130", "0x1002")
    # setattr, not an assignment the old shim would never read: before the change the Intel cases
    # fail by count instead of the script dying on a missing name.
    setattr(shim, "DRM_ROOT", drm)

    def cfg_for(node, with_shader=False):
        c = dict(shim.DEFAULTS)
        c["render_node"] = node
        c["shader"] = shader if with_shader else None
        c["sharpener"] = sharpener if with_shader else None
        c["shader_cache_dir"] = os.path.join(tmp, "cache")
        return c

    # ---- unchanged, byte for byte ----
    equal("default config (no render node): the pre-change chain", NVIDIA_PLAIN, chain(cfg_for(None)))
    equal("NVIDIA render node: the pre-change chain", NVIDIA_PLAIN, chain(cfg_for("/dev/dri/renderD129")))
    equal("AMD render node: the pre-change chain", NVIDIA_PLAIN, chain(cfg_for("/dev/dri/renderD130")))
    equal("render node with no sysfs entry: the pre-change chain", NVIDIA_PLAIN, chain(cfg_for("/dev/dri/renderD199")))
    for bad in ("", "renderD128", "/dev/dri/card0", "/dev/dri/renderD128,x", 7, ["/dev/dri/renderD128"]):
        equal("invalid render_node %r: the pre-change chain" % (bad,), NVIDIA_PLAIN, chain(cfg_for(bad)))

    with_shader_nv = chain(cfg_for("/dev/dri/renderD129", True))
    check("NVIDIA with shaders: the pre-change tail and no format=rgba",
          with_shader_nv.startswith("format=yuv420p,hwupload,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos:custom_shader_path=")
          and with_shader_nv.endswith(",hwdownload,format=yuv420p") and "rgba" not in with_shader_nv, with_shader_nv)

    unreadable = os.path.join(drm, "renderD140", "device")
    os.makedirs(unreadable)
    os.makedirs(os.path.join(unreadable, "vendor"))  # a directory where the file should be
    equal("unreadable vendor file: the pre-change chain", NVIDIA_PLAIN, chain(cfg_for("/dev/dri/renderD140")))

    # ---- Intel ----
    INTEL_PLAIN = ("format=yuv420p,hwupload,libplacebo=w=1920:h=1080:upscaler=ewa_lanczos:format=rgba,"
                   "hwdownload,format=rgba,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p")
    equal("Intel render node: libplacebo outputs rgba, downloaded as rgba, converted on the CPU",
          INTEL_PLAIN, chain(cfg_for("/dev/dri/renderD128")))

    intel_shader = chain(cfg_for("/dev/dri/renderD128", True))
    check("Intel with shaders: still libplacebo with the shader", ":custom_shader_path=" in intel_shader, intel_shader)
    check("Intel with shaders: libplacebo is told to output rgba", ":format=rgba," in intel_shader, intel_shader)
    check("Intel with shaders: ends in the download, the CPU conversion and yuv420p",
          intel_shader.endswith(",hwdownload,format=rgba,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p"),
          intel_shader)
    check("Intel: no yuv420p download straight from Vulkan",
          ",hwdownload,format=yuv420p" not in intel_shader and ",hwdownload,format=yuv420p" not in chain(cfg_for("/dev/dri/renderD128")))

    # The sharpener-only chain (ratio below sr_min_scale_factor) takes the same tail.
    low = chain(cfg_for("/dev/dri/renderD128", True), ratio=1.2)
    check("Intel at a low ratio: same rgba tail", low.endswith(",hwdownload,format=rgba,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p"), low)

print()
print("%d passed, %d failed" % (passed, failed))
sys.exit(1 if failed else 0)
