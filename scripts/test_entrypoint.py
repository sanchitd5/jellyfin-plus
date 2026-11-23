#!/usr/bin/env python3
"""The Arc image's entrypoint wrapper (docker/intel/entrypoint.py).

    python3 -I scripts/test_entrypoint.py

Needs no docker, Jellyfin or network. It loads the real wrapper and drives it against temp dist and data
directories, and proves:

  - an empty data dir is seeded with plugins/JellyfinPlus_<ver> (DLL, deps.json, icon, meta.json);
  - a second run changes nothing (bytes and mtimes);
  - a version upgrade moves the old dir to the new name, replaces only our files and the meta.json keys the
    dist owns, and keeps Jellyfin's own keys (status, autoUpdate) and any other file in the plugin dir;
  - a stale dir with our guid is removed, a foreign plugin dir is never touched;
  - the user's plugins/configurations/ files are never touched;
  - a missing dist or an unwritable data dir does not raise (startup must go on);
  - the wrapper execs the real entrypoint with the original args (checked with a stub binary).

It does not start Jellyfin: docker/intel/accept-plugin.sh does that, inside the image build. Exit status:
0 every check passed, 1 a check failed.
"""
import importlib.util
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
ENTRY = os.path.join(REPO, "docker", "intel", "entrypoint.py")

GUID = "6f2a9c31-4d7b-4e2a-9d15-8a1c0b7e3f44"
FAILS = []
PASSES = 0


def check(name, ok, detail=""):
    global PASSES
    if ok:
        PASSES += 1
    else:
        FAILS.append(name)
        print(f"FAIL {name} {detail}".rstrip())


def load():
    spec = importlib.util.spec_from_file_location("jp_entrypoint", ENTRY)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb" if isinstance(data, bytes) else "w") as f:
        f.write(data)


def make_dist(root, version, dll=b"dll-v1"):
    """A dist tree as docker/intel/stage-plugin.sh lays it out."""
    d = os.path.join(root, f"JellyfinPlus_{version}")
    write(os.path.join(d, "Jellyfin.Plugin.Plus.dll"), dll)
    write(os.path.join(d, "Jellyfin.Plugin.Plus.deps.json"), b'{"deps":1}')
    write(os.path.join(d, "jellyfinplus.png"), b"png")
    meta = {"guid": GUID, "name": "Jellyfin Plus", "version": version, "targetAbi": "12.3.0.0", "owner": "Sanchit",
            "overview": "o", "category": "General", "description": f"desc {version}", "imagePath": "jellyfinplus.png"}
    write(os.path.join(d, "meta.json"), json.dumps(meta, indent=2))
    return d


def snapshot(root):
    """{relative path: (bytes, mtime_ns)} of every file under root."""
    out = {}
    for base, _, files in os.walk(root):
        for f in files:
            p = os.path.join(base, f)
            out[os.path.relpath(p, root)] = (open(p, "rb").read(), os.stat(p).st_mtime_ns)
    return out


def meta_of(plugin_dir):
    return json.load(open(os.path.join(plugin_dir, "meta.json")))


def test_seed_empty(ep, tmp):
    dist = os.path.join(tmp, "dist")
    data = os.path.join(tmp, "data")
    make_dist(dist, "1.0.0.0")
    ep.seed(dist, data)
    target = os.path.join(data, "plugins", "JellyfinPlus_1.0.0.0")
    for f in ("Jellyfin.Plugin.Plus.dll", "Jellyfin.Plugin.Plus.deps.json", "jellyfinplus.png", "meta.json"):
        check(f"empty dir: {f} seeded", os.path.isfile(os.path.join(target, f)), str(os.listdir(target) if os.path.isdir(target) else "no dir"))
    check("empty dir: DLL bytes copied", open(os.path.join(target, "Jellyfin.Plugin.Plus.dll"), "rb").read() == b"dll-v1")
    check("empty dir: meta.json carries the guid and name",
          os.path.isfile(os.path.join(target, "meta.json")) and meta_of(target)["guid"] == GUID and meta_of(target)["name"] == "Jellyfin Plus")
    check("empty dir: nothing is written outside plugins/", os.listdir(data) == ["plugins"], str(os.listdir(data)))


def test_idempotent(ep, tmp):
    dist = os.path.join(tmp, "dist")
    data = os.path.join(tmp, "data")
    make_dist(dist, "1.0.0.0")
    ep.seed(dist, data)
    before = snapshot(data)
    ep.seed(dist, data)
    check("second run changes nothing (bytes and mtimes)", snapshot(data) == before)
    check("the first run wrote something", len(before) == 4, str(sorted(before)))

    # a deleted file comes back, the rest is left alone
    target = os.path.join(data, "plugins", "JellyfinPlus_1.0.0.0")
    os.remove(os.path.join(target, "jellyfinplus.png"))
    ep.seed(dist, data)
    check("a missing file is restored", os.path.isfile(os.path.join(target, "jellyfinplus.png")))

    # a changed DLL of the same version is replaced (a rebuilt image)
    write(os.path.join(dist, "JellyfinPlus_1.0.0.0", "Jellyfin.Plugin.Plus.dll"), b"dll-rebuilt")
    ep.seed(dist, data)
    check("a rebuilt DLL of the same version is replaced",
          open(os.path.join(target, "Jellyfin.Plugin.Plus.dll"), "rb").read() == b"dll-rebuilt")


def test_upgrade(ep, tmp):
    dist = os.path.join(tmp, "dist")
    data = os.path.join(tmp, "data")
    make_dist(dist, "1.0.0.0")
    ep.seed(dist, data)
    old = os.path.join(data, "plugins", "JellyfinPlus_1.0.0.0")
    # what Jellyfin and the user add to the plugin dir
    m = meta_of(old)
    m.update({"status": "Active", "autoUpdate": False, "description": "stale text"})
    write(os.path.join(old, "meta.json"), json.dumps(m))
    write(os.path.join(old, "notes.txt"), "keep me")

    shutil_dist = os.path.join(tmp, "dist2")
    make_dist(shutil_dist, "1.1.0.0", dll=b"dll-v2")
    ep.seed(shutil_dist, data)
    new = os.path.join(data, "plugins", "JellyfinPlus_1.1.0.0")
    check("upgrade: the new version dir exists", os.path.isdir(new))
    check("upgrade: the old version dir is gone", not os.path.exists(old), str(os.listdir(os.path.join(data, "plugins"))))
    check("upgrade: our DLL is replaced", open(os.path.join(new, "Jellyfin.Plugin.Plus.dll"), "rb").read() == b"dll-v2")
    got = meta_of(new)
    check("upgrade: version and description follow the dist", got["version"] == "1.1.0.0" and got["description"] == "desc 1.1.0.0", str(got))
    check("upgrade: Jellyfin's own keys are kept", got.get("status") == "Active" and got.get("autoUpdate") is False, str(got))
    check("upgrade: a foreign file in the plugin dir is kept", os.path.isfile(os.path.join(new, "notes.txt")))

    # a stale dir that is not the one being moved (two old versions) is removed too
    write(os.path.join(data, "plugins", "JellyfinPlus_0.9.0.0", "meta.json"), json.dumps({"guid": GUID, "name": "Jellyfin Plus", "version": "0.9.0.0"}))
    ep.seed(shutil_dist, data)
    check("upgrade: an extra stale dir with our guid is removed", not os.path.exists(os.path.join(data, "plugins", "JellyfinPlus_0.9.0.0")))
    check("upgrade: the current dir survives that cleanup", os.path.isfile(os.path.join(new, "Jellyfin.Plugin.Plus.dll")))


def test_foreign_and_config(ep, tmp):
    dist = os.path.join(tmp, "dist")
    data = os.path.join(tmp, "data")
    make_dist(dist, "1.0.0.0")
    plugins = os.path.join(data, "plugins")
    write(os.path.join(plugins, "configurations", "Jellyfin.Plugin.Plus.xml"), "<PluginConfiguration>user</PluginConfiguration>")
    write(os.path.join(plugins, "configurations", "Other.xml"), "<x/>")
    # a foreign plugin whose dir name starts the same but whose guid is not ours
    write(os.path.join(plugins, "JellyfinPlus_Other_1.0", "meta.json"), json.dumps({"guid": "00000000-0000-0000-0000-000000000000", "name": "Other"}))
    write(os.path.join(plugins, "SSO-Auth_4.0", "meta.json"), json.dumps({"guid": "11111111-1111-1111-1111-111111111111", "name": "SSO"}))
    # a stale dir of ours with an unreadable meta.json is left alone, not guessed at
    write(os.path.join(plugins, "JellyfinPlus_0.1", "meta.json"), "{not json")
    before = snapshot(plugins)
    keep = {k: v for k, v in before.items() if not k.startswith("JellyfinPlus_1.0.0.0")}

    ep.seed(dist, data)
    after = snapshot(plugins)
    check("user plugin configuration is untouched", all(after.get(k) == v for k, v in keep.items() if k.startswith("configurations")))
    check("foreign plugin dirs are untouched", all(after.get(k) == v for k, v in keep.items() if not k.startswith("configurations")),
          str(sorted(set(keep) - set(after))))
    check("our plugin dir was still seeded", os.path.isfile(os.path.join(plugins, "JellyfinPlus_1.0.0.0", "meta.json")))


def test_fail_soft(ep, tmp):
    # no dist at all
    try:
        ep.seed(os.path.join(tmp, "nope"), os.path.join(tmp, "data1"))
        ok = True
    except Exception as e:  # noqa: BLE001
        ok = False
        print("raised:", e)
    check("a missing dist does not raise", ok)
    check("a missing dist creates nothing", not os.path.exists(os.path.join(tmp, "data1", "plugins", "JellyfinPlus_1.0.0.0")))

    # a data dir that is a file: makedirs fails
    dist = os.path.join(tmp, "dist")
    make_dist(dist, "1.0.0.0")
    blocker = os.path.join(tmp, "blocker")
    write(blocker, "x")
    try:
        ep.seed(dist, blocker)
        ok = True
    except Exception as e:  # noqa: BLE001
        ok = False
        print("raised:", e)
    check("an unusable data dir does not raise", ok)

    # a dist whose DLL is a directory: copy fails
    dist2 = os.path.join(tmp, "dist2")
    d = make_dist(dist2, "1.0.0.0")
    os.remove(os.path.join(d, "Jellyfin.Plugin.Plus.dll"))
    os.makedirs(os.path.join(d, "Jellyfin.Plugin.Plus.dll"))
    try:
        ep.seed(dist2, os.path.join(tmp, "data3"))
        ok = True
    except Exception as e:  # noqa: BLE001
        ok = False
        print("raised:", e)
    check("a failed copy does not raise", ok)

    # main() swallows a seed that blows up and still execs
    calls = []
    orig_seed, orig_exec = ep.seed, ep.os.execv
    ep.seed = lambda *a, **k: (_ for _ in ()).throw(RuntimeError("boom"))
    ep.os.execv = lambda path, argv: calls.append((path, argv))
    try:
        ep.main(["wrapper", "--x"], {"JELLYFIN_PLUS_REAL_ENTRYPOINT": "/bin/real"})
    finally:
        ep.seed, ep.os.execv = orig_seed, orig_exec
    check("main still execs when seeding raises", calls == [("/bin/real", ["/bin/real", "--x"])], str(calls))


def test_main_exec(ep, tmp):
    stub = os.path.join(tmp, "real-jellyfin")
    write(stub, '#!/bin/sh\necho "ARGV:$*"\necho "DATA:$JELLYFIN_DATA_DIR"\n')
    os.chmod(stub, 0o755)
    dist = os.path.join(tmp, "dist")
    data = os.path.join(tmp, "data")
    make_dist(dist, "1.0.0.0")
    env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "JELLYFIN_PLUS_REAL_ENTRYPOINT": stub,
           "JELLYFIN_PLUS_DIST": dist, "JELLYFIN_DATA_DIR": data}
    p = subprocess.run([sys.executable, "-I", ENTRY, "--ffmpeg", "/x y", "--service"], capture_output=True, text=True, env=env, timeout=30)
    check("the wrapper exits with the real entrypoint's status", p.returncode == 0, p.stderr)
    check("the real entrypoint gets the original args", "ARGV:--ffmpeg /x y --service" in p.stdout, p.stdout)
    check("the real entrypoint sees the environment", f"DATA:{data}" in p.stdout, p.stdout)
    check("the wrapper seeded before it exec'd", os.path.isfile(os.path.join(data, "plugins", "JellyfinPlus_1.0.0.0", "meta.json")))

    # a failing seed (data dir is a file) still starts the real entrypoint
    blocker = os.path.join(tmp, "blocker")
    write(blocker, "x")
    p = subprocess.run([sys.executable, "-I", ENTRY, "a"], capture_output=True, text=True, env=dict(env, JELLYFIN_DATA_DIR=blocker), timeout=30)
    check("a failing seed still starts the real entrypoint", p.returncode == 0 and "ARGV:a" in p.stdout, f"{p.stdout} {p.stderr}")
    check("a failing seed says so on stderr", "jellyfin-plus-entrypoint" in p.stderr, p.stderr)

    ep_env = {}
    check("the default real entrypoint is /jellyfin/jellyfin", ep.real_entrypoint(ep_env) == "/jellyfin/jellyfin")
    check("the default data dir is /config", ep.data_dir(ep_env) == "/config")
    check("the default dist is /usr/lib/jellyfin-plus-dist", ep.dist_dir(ep_env) == "/usr/lib/jellyfin-plus-dist")
    check("the env overrides win", ep.real_entrypoint({"JELLYFIN_PLUS_REAL_ENTRYPOINT": "/x"}) == "/x")

    check("the script's shebang is env python3", open(ENTRY).readline().strip() == "#!/usr/bin/env python3")


def main():
    if not os.path.isfile(ENTRY):
        print(f"FAIL no {ENTRY}")
        return 1
    ep = load()
    for part in (test_seed_empty, test_idempotent, test_upgrade, test_foreign_and_config, test_fail_soft, test_main_exec):
        with tempfile.TemporaryDirectory() as tmp:
            part(ep, tmp)
    print(f"{PASSES} checks passed, {len(FAILS)} failed")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
