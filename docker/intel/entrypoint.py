#!/usr/bin/env python3
"""Container entrypoint of the Arc image: seed the Jellyfin Plus plugin, then become Jellyfin.

The image stages the plugin under /usr/lib/jellyfin-plus-dist/JellyfinPlus_<ver> (docker/intel/stage-plugin.sh);
Jellyfin only loads plugins from $JELLYFIN_DATA_DIR/plugins, which is an empty volume on first start. This
copies the staged plugin in before the server starts, the same layout scripts/proxmox-build.sh creates. The
patcher needs nothing here: the plugin loads it from /usr/lib/jellyfin-plus itself.

  - idempotent: files already identical are left alone (mtimes included);
  - an upgrade moves the old JellyfinPlus_<old> dir (same guid) to the new name, so Jellyfin's own meta.json
    keys (status, autoUpdate) and any other file in it survive; only our files are replaced;
  - plugins/configurations/ (the user's settings) and every other plugin are never touched;
  - fails soft: a seed error is reported on stderr and Jellyfin starts anyway.

It then execs JELLYFIN_PLUS_REAL_ENTRYPOINT (default /jellyfin/jellyfin, the base image's ENTRYPOINT) with the
original arguments, so PID 1 and signal handling are the base's.
"""
import filecmp
import json
import os
import shutil
import sys

PREFIX = "JellyfinPlus_"
DEFAULT_REAL = "/jellyfin/jellyfin"
DEFAULT_DIST = "/usr/lib/jellyfin-plus-dist"
DEFAULT_DATA = "/config"


def real_entrypoint(env):
    return env.get("JELLYFIN_PLUS_REAL_ENTRYPOINT") or DEFAULT_REAL


def dist_dir(env):
    return env.get("JELLYFIN_PLUS_DIST") or DEFAULT_DIST


def data_dir(env):
    return env.get("JELLYFIN_DATA_DIR") or DEFAULT_DATA


def warn(msg):
    print(f"jellyfin-plus-entrypoint: {msg}", file=sys.stderr, flush=True)


def read_meta(plugin_dir):
    try:
        with open(os.path.join(plugin_dir, "meta.json")) as f:
            meta = json.load(f)
        return meta if isinstance(meta, dict) else None
    except (OSError, ValueError):
        return None


def copy_if_changed(src, dst):
    if os.path.isfile(dst) and filecmp.cmp(src, dst, shallow=False):
        return
    tmp = dst + ".tmp"
    shutil.copyfile(src, tmp)
    os.replace(tmp, dst)


def write_meta(path, meta):
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(meta, f, indent=2)
    os.replace(tmp, path)


def seed_one(src, plugins):
    name = os.path.basename(src)
    target = os.path.join(plugins, name)
    ours = read_meta(src)
    if not ours or not ours.get("guid"):
        warn(f"{src} has no usable meta.json, skipped")
        return
    os.makedirs(plugins, exist_ok=True)

    stale = sorted(d for d in os.listdir(plugins)
                   if d != name and d.startswith(PREFIX)
                   and (read_meta(os.path.join(plugins, d)) or {}).get("guid") == ours["guid"])
    if stale and not os.path.exists(target):
        os.rename(os.path.join(plugins, stale.pop()), target)
    for d in stale:
        shutil.rmtree(os.path.join(plugins, d), ignore_errors=True)

    os.makedirs(target, exist_ok=True)
    for f in sorted(os.listdir(src)):
        if f != "meta.json" and os.path.isfile(os.path.join(src, f)):
            copy_if_changed(os.path.join(src, f), os.path.join(target, f))

    have = read_meta(target)
    merged = dict(have or {}, **ours)
    if merged != have:
        write_meta(os.path.join(target, "meta.json"), merged)


def seed(dist, data):
    """Seed every plugin dir staged in dist into <data>/plugins. Never raises."""
    try:
        if not os.path.isdir(dist):
            warn(f"no staged plugin at {dist}, nothing to seed")
            return
        plugins = os.path.join(data, "plugins")
        for d in sorted(os.listdir(dist)):
            src = os.path.join(dist, d)
            if d.startswith(PREFIX) and os.path.isdir(src):
                try:
                    seed_one(src, plugins)
                except Exception as e:  # noqa: BLE001 - startup must go on
                    warn(f"could not seed {d} into {plugins}: {e}")
    except Exception as e:  # noqa: BLE001
        warn(f"could not seed the plugin: {e}")


def main(argv, env):
    try:
        seed(dist_dir(env), data_dir(env))
    except Exception as e:  # noqa: BLE001
        warn(f"seed failed: {e}")
    real = real_entrypoint(env)
    os.execv(real, [real, *argv[1:]])


if __name__ == "__main__":
    main(sys.argv, os.environ)
