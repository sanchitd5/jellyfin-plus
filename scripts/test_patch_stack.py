#!/usr/bin/env python3
"""Check that ffmpeg/0016 stacks on ffmpeg/0001 and ffmpeg/0015, with the standard library only.

    python3 scripts/test_patch_stack.py [--tree DIR]

Default: rebuild, for each file the three patches touch, the line numbers and text the patches
describe (0001 and 0015 new sides, then 0016's old side must sit on them) and check every hunk's
line counts against its body, its old-side lines against what the earlier patches left at those
line numbers, and its new start against old start plus the net growth of the hunks before it.

--tree DIR: DIR holds pristine configure, libavfilter/Makefile and libavfilter/allfilters.c of the
FFmpeg release; 0001, 0015 and 0016 are applied to them in order, in memory, like `git apply` (a hunk
may sit at an offset from its header, which is reported). The tree is read, never written.

This is not `git apply`: it does not know whitespace or fuzz rules. scripts/test-gu-inputs.sh runs
the real `git apply --check` against the FFmpeg tarball.
"""
import argparse
import copy
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FFMPEG = os.path.join(HERE, "..", "ffmpeg")
STACK = [
    "0001-add-oidn-filter-to-build.patch",
    "0015-add-ort-filter-to-build-standalone.patch",
    "0016-add-fsr2-filter-to-build-standalone.patch",
]
HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


def parse(text):
    """-> [(path, [hunk])], hunk = dict(old_start, old_count, new_start, new_count, old, new)."""
    lines = text.split("\n")
    files, i = [], 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("--- ") and i + 1 < len(lines) and lines[i + 1].startswith("+++ "):
            path = lines[i][4:].split("\t")[0]
            files.append((re.sub(r"^a/", "", path), []))
            i += 2
            continue
        m = HUNK.match(line)
        if not m or not files:
            i += 1
            continue
        oc = int(m.group(2) if m.group(2) is not None else 1)
        nc = int(m.group(4) if m.group(4) is not None else 1)
        hunk = {"old_start": int(m.group(1)), "old_count": oc, "new_start": int(m.group(3)),
                "new_count": nc, "old": [], "new": [], "short": False, "long": False}
        i += 1
        while len(hunk["old"]) < oc or len(hunk["new"]) < nc:
            if i >= len(lines):
                hunk["short"] = True
                break
            body = lines[i]
            tag, rest = body[:1], body[1:]
            if tag == "\\":
                i += 1
                continue
            if tag in (" ", ""):
                hunk["old"].append(rest)
                hunk["new"].append(rest)
            elif tag == "-":
                hunk["old"].append(rest)
            elif tag == "+":
                hunk["new"].append(rest)
            else:
                hunk["short"] = True
                break
            i += 1
        if i < len(lines) and lines[i] not in ("",) and not lines[i].startswith(("@@", "--- ")):
            hunk["long"] = True
        files[-1][1].append(hunk)
    return files


def load():
    out = []
    for name in STACK:
        with open(os.path.join(FFMPEG, name), encoding="utf-8") as f:
            out.append((name, parse(f.read())))
    return out


def check_windows(patches):
    """-> (failures, checked old-side lines, old-side lines total)"""
    fails, known, checked, total = [], {}, 0, 0
    for name, files in patches:
        for path, hunks in files:
            k = known.setdefault(path, {})
            delta = 0
            for n, h in enumerate(hunks, 1):
                where = "%s %s hunk %d" % (name.split("-")[0], path, n)
                if h["short"] or len(h["old"]) != h["old_count"] or len(h["new"]) != h["new_count"]:
                    fails.append("%s: body does not have the %d/%d lines its header counts"
                                 % (where, h["old_count"], h["new_count"]))
                if h["long"]:
                    fails.append("%s: body has more lines than its header counts" % where)
                if h["new_start"] != h["old_start"] + delta:
                    fails.append("%s: new start is %d, old start %d plus the earlier hunks' %+d is %d"
                                 % (where, h["new_start"], h["old_start"], delta,
                                    h["old_start"] + delta))
                for j, text in enumerate(h["old"]):
                    ln = h["old_start"] + j
                    total += 1
                    if ln in k:
                        checked += 1
                        if k[ln] != text:
                            fails.append("%s: line %d is %r after the earlier patches, the hunk expects %r"
                                         % (where, ln, k[ln], text))
                delta += h["new_count"] - h["old_count"]
            nxt = {}
            for ln, text in k.items():
                if any(h["old_start"] <= ln < h["old_start"] + h["old_count"] for h in hunks):
                    continue
                shift = sum(h["new_count"] - h["old_count"] for h in hunks
                            if h["old_start"] + h["old_count"] <= ln)
                nxt[ln + shift] = text
            for h in hunks:
                for j, text in enumerate(h["new"]):
                    nxt[h["new_start"] + j] = text
            known[path] = nxt
    return fails, checked, total


def apply_tree(tree, patches):
    """-> (failures, notes). Applies every patch in order to the files under tree, in memory."""
    fails, notes, files_now = [], [], {}
    for name, files in patches:
        for path, hunks in files:
            if path not in files_now:
                full = os.path.join(tree, path)
                if not os.path.isfile(full):
                    fails.append("%s: %s is not in the tree" % (name, path))
                    continue
                with open(full, encoding="utf-8", errors="surrogateescape") as f:
                    files_now[path] = f.read().split("\n")
            lines, shifted, drift = files_now[path], 0, 0
            for n, h in enumerate(hunks, 1):
                want, where = h["old"], "%s %s hunk %d" % (name.split("-")[0], path, n)
                expect = h["old_start"] - 1 + shifted + drift
                pos = None
                for d in range(len(lines) + 1):
                    for p in (expect + d, expect - d):
                        if 0 <= p <= len(lines) - len(want) and lines[p:p + len(want)] == want:
                            pos = p
                            break
                    if pos is not None:
                        break
                if pos is None:
                    fails.append("%s: its context is not in the file near line %d"
                                 % (where, h["old_start"] + shifted + drift))
                    break
                if pos != expect:
                    notes.append("%s: applies at an offset of %+d lines" % (where, pos - expect))
                    drift += pos - expect
                lines[pos:pos + len(want)] = h["new"]
                shifted += len(h["new"]) - len(want)
    return fails, notes


def selfcheck(patches):
    """The checker must be able to fail: bump one new start and change one context line."""
    bad = copy.deepcopy(patches)
    cfg = [h for _, files in bad[-1:] for path, hs in files if path == "configure" for h in hs]
    cfg[1]["new_start"] += 1
    f1, _, _ = check_windows(bad)
    bad = copy.deepcopy(patches)
    mk = [h for _, files in bad[-1:] for path, hs in files if path.endswith("Makefile") for h in hs][0]
    mk["old"][0] = mk["old"][0] + "x"
    f2, _, _ = check_windows(bad)
    return bool(f1) and bool(f2)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--tree", help="directory with pristine configure and libavfilter/{Makefile,allfilters.c}")
    args = ap.parse_args()

    patches = load()
    failures = 0

    def report(ok, what):
        nonlocal failures
        print("%s  %s" % ("PASS" if ok else "FAIL", what))
        failures += 0 if ok else 1

    fails, checked, total = check_windows(patches)
    for msg in fails:
        print("      " + msg)
    report(not fails, "0001 + 0015 + 0016 line counts, old-side context and new starts agree "
                      "(%d of %d old-side lines were pinned by an earlier patch)" % (checked, total))
    report(selfcheck(patches), "the checker fails on a wrong new start and on a wrong context line")

    if args.tree:
        fails, notes = apply_tree(args.tree, patches)
        for msg in notes + fails:
            print("      " + msg)
        report(not fails, "0001, 0015 and 0016 apply in order to %s" % args.tree)
    else:
        print("SKIP  --tree not given: not applied to a real FFmpeg tree")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
