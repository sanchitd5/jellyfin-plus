#!/usr/bin/env python3
"""Fetch the ONNX Runtime C headers vf_ort.c needs for one version from Microsoft's NuGet package.

    fetch-ort-header.py <version> <output directory> <header>:<sha256> [<header>:<sha256> ...]

onnxruntime_c_api.h and the one header it includes (onnxruntime_ep_c_api.h, since 1.24) are all
that is wanted from a 125 MB package, so the zip's central directory and those entries are read
with HTTP range requests, and each file is accepted only if its sha256 is the pinned one. The
headers must match the runtime's API version, and the pinned hashes are how that is held. Microsoft's GitHub release tarballs carry the same header; NuGet is a second official source
that serves range requests. The header is MIT-licensed and is not vendored in this repository.

Used by scripts/build-ffmpeg.sh (ORT_EP=openvino) and scripts/test-vf-ort.sh. Standard library only.
"""
import hashlib
import io
import os
import sys
import urllib.request
import zipfile

ENTRY_DIR = "build/native/include/"


class HttpFile(io.RawIOBase):
    """A read-only, seekable view of a URL, one range request per read."""

    def __init__(self, url):
        self.url = url
        head = urllib.request.urlopen(urllib.request.Request(url, method="HEAD"), timeout=60)
        self.size = int(head.headers["Content-Length"])
        self.pos = 0

    def seekable(self):
        return True

    def readable(self):
        return True

    def tell(self):
        return self.pos

    def seek(self, offset, whence=0):
        self.pos = offset if whence == 0 else (self.pos + offset if whence == 1 else self.size + offset)
        return self.pos

    def read(self, n=-1):
        if n < 0:
            n = self.size - self.pos
        if n == 0 or self.pos >= self.size:
            return b""
        end = min(self.pos + n, self.size) - 1
        req = urllib.request.Request(self.url, headers={"Range": "bytes=%d-%d" % (self.pos, end)})
        with urllib.request.urlopen(req, timeout=120) as resp:
            if resp.status != 206:
                sys.exit("%s ignored the Range request and sent HTTP %d, not 206 Partial Content" % (self.url, resp.status))
            data = resp.read()
        self.pos += len(data)
        return data

    def readinto(self, b):
        data = self.read(len(b))
        b[:len(data)] = data
        return len(data)


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    version, out = sys.argv[1:3]
    url = "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/%s/microsoft.ml.onnxruntime.%s.nupkg" % (version, version)
    package = zipfile.ZipFile(HttpFile(url))
    os.makedirs(out, exist_ok=True)
    for pin in sys.argv[3:]:
        name, _, want = pin.partition(":")
        if "/" in name or not want:
            sys.exit("bad pin '%s', want <header>:<sha256>" % pin)
        data = package.read(ENTRY_DIR + name)
        got = hashlib.sha256(data).hexdigest()
        if got != want:
            sys.exit("%s %s: sha256 %s, expected %s" % (name, version, got, want))
        with open(os.path.join(out, name), "wb") as f:
            f.write(data)
        print("%s %s (sha256 %s) -> %s" % (name, version, got, out))


if __name__ == "__main__":
    main()
