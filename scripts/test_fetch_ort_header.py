#!/usr/bin/env python3
"""HttpFile in fetch-ort-header.py must insist on a 206 reply to its Range request.

A server that ignores Range and sends 200 with the whole body would hand the zip reader the wrong
bytes at the wrong offset; the read has to stop with a message naming the status instead.
Loopback http.server only, no network.
"""
import http.server
import importlib.util
import io
import os
import sys
import threading
import unittest
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("fetch_ort_header", os.path.join(HERE, "fetch-ort-header.py"))
fetch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fetch)

ENTRY = "build/native/include/onnxruntime_c_api.h"
PAYLOAD = b"/* header */\n" * 200


def make_zip():
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_STORED) as z:
        z.writestr(ENTRY, PAYLOAD)
    return buf.getvalue()


class Handler(http.server.BaseHTTPRequestHandler):
    body = b""
    honour_range = True

    def log_message(self, *args):
        pass

    def do_HEAD(self):
        self.send_response(200)
        self.send_header("Content-Length", str(len(self.body)))
        self.end_headers()

    def do_GET(self):
        rng = self.headers.get("Range")
        if rng and self.honour_range:
            first, _, last = rng.split("=", 1)[1].partition("-")
            first, last = int(first), int(last)
            chunk = self.body[first:last + 1]
            self.send_response(206)
            self.send_header("Content-Range", "bytes %d-%d/%d" % (first, last, len(self.body)))
        else:
            chunk = self.body
            self.send_response(200)
        self.send_header("Content-Length", str(len(chunk)))
        self.end_headers()
        self.wfile.write(chunk)


def serve(honour_range):
    handler = type("H", (Handler,), {"body": make_zip(), "honour_range": honour_range})
    server = http.server.HTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, "http://127.0.0.1:%d/pkg.nupkg" % server.server_address[1]


class HttpFileTest(unittest.TestCase):
    def setUp(self):
        urllib.request.install_opener(urllib.request.build_opener(urllib.request.ProxyHandler({})))

    def run_server(self, honour_range):
        server, url = serve(honour_range)
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return url

    def test_range_ignored_exits_naming_status(self):
        f = fetch.HttpFile(self.run_server(False))
        with self.assertRaises(SystemExit) as ctx:
            f.read(10)
        message = str(ctx.exception)
        self.assertIn("200", message)
        self.assertIn("206", message)

    def test_range_ignored_stops_zip_open(self):
        with self.assertRaises(SystemExit):
            zipfile.ZipFile(fetch.HttpFile(self.run_server(False)))

    def test_range_honoured_reads_exact_bytes(self):
        f = fetch.HttpFile(self.run_server(True))
        f.seek(4)
        self.assertEqual(f.read(6), make_zip()[4:10])
        self.assertEqual(f.tell(), 10)

    def test_range_honoured_opens_zip(self):
        package = zipfile.ZipFile(fetch.HttpFile(self.run_server(True)))
        self.assertEqual(package.read(ENTRY), PAYLOAD)


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)
