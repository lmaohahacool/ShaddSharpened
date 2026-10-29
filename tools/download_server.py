#!/usr/bin/env python3
"""Serve build artifacts for direct browser download (live-preview friendly).

Usage: python3 tools/download_server.py <directory> [port]

GET /           -> index page listing every file with size + SHA-256, so a
                   truncated or altered transfer is detectable before use
GET /<name>     -> the file, with Range support and attachment disposition
"""
import hashlib
import os
import re
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.getcwd()
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8000

FILES = {}
for name in sorted(os.listdir(ROOT)):
    path = os.path.join(ROOT, name)
    if os.path.isfile(path):
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        FILES[name] = (os.path.getsize(path), h.hexdigest())

ROWS = "\n".join(
    f"<tr><td><a href=\"/{n}\">{n}</a></td><td class=r>{s:,}</td>"
    f"<td><code>{sha[:16]}&hellip;</code></td></tr>"
    for n, (s, sha) in FILES.items()
)

PAGE = f"""<!doctype html><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>Shaft Additionality — downloads</title>
<style>
body{{font:16px/1.6 system-ui,sans-serif;max-width:760px;margin:6vh auto;padding:0 16px;color:#222}}
table{{border-collapse:collapse;width:100%}}td,th{{border:1px solid #ddd;padding:6px 10px;text-align:left}}
td.r{{text-align:right;white-space:nowrap}}code{{background:#eee;padding:1px 5px;border-radius:4px}}
h1{{color:#b8860b}}.box{{background:#fff8e1;border:1px solid #ffcc44;border-radius:8px;padding:10px 14px}}
</style>
<h1>Shaft Additionality — downloads</h1>
<p>Every file below is served straight from the build sandbox. After any download,
<b>right-click &rarr; Properties</b> and compare the size with the table; if it differs,
the transfer was cut &mdash; just retry that file (downloads resume).</p>
<table><tr><th>File</th><th>Bytes</th><th>SHA-256</th></tr>
{ROWS}
</table>
<div class=box>
<p><b>Option A &mdash; one file:</b> download <code>ShaftAdditionality-win-x64.zip</code>,
extract, run <code>ShaftAdditionality.exe</code> inside.</p>
<p><b>Option B &mdash; antivirus-proof:</b> <code>ShaftAdditionality-win-x64-PROTECTED.zip</code>
is the same archive, password <code>shaft123</code> (stops security software from
rewriting the exe inside while it downloads). Windows asks for the password when extracting.</p>
<p><b>Option C &mdash; small pieces:</b> download all eight <code>&hellip;.partNN</code> files plus
<code>recombine.bat</code> into one folder, then double-click <code>recombine.bat</code>.
It joins them into the zip and tells you the size to check.</p>
<p>SmartScreen may warn once (unsigned exe): <i>More info &rarr; Run anyway</i>.</p>
</div>
"""


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send_zip_headers(self, size, start, end, partial):
        self.send_response(206 if partial else 200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        if partial:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")

    def do_HEAD(self):
        name = self.path.split("?")[0].lstrip("/")
        if name in FILES:
            self._send_zip_headers(FILES[name][0], 0, FILES[name][0] - 1, False)
            self.end_headers()
        else:
            self.send_error(404)

    def do_GET(self):
        path = self.path.split("?")[0]
        if path in ("/", "/index.html"):
            body = PAGE.encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        name = path.lstrip("/")
        if name not in FILES or "/" in name or name in (".", ".."):
            self.send_error(404)
            return
        size = FILES[name][0]
        start, end, partial = 0, size - 1, False
        rng = self.headers.get("Range")
        if rng:
            m = re.match(r"bytes=(\d*)-(\d*)", rng)
            if m and (m.group(1) or m.group(2)):
                partial = True
                if m.group(1):
                    start = int(m.group(1))
                    if m.group(2):
                        end = min(int(m.group(2)), size - 1)
                else:
                    start = max(size - int(m.group(2)), 0)
                if start >= size:
                    self.send_response(416)
                    self.send_header("Content-Range", f"bytes */{size}")
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
        self._send_zip_headers(size, start, end, partial)
        self.send_header("Content-Disposition", f'attachment; filename="{name}"')
        self.end_headers()
        with open(os.path.join(ROOT, name), "rb") as f:
            f.seek(start)
            left = end - start + 1
            while left > 0:
                chunk = f.read(min(1 << 20, left))
                if not chunk:
                    break
                self.wfile.write(chunk)
                left -= len(chunk)

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    print(f"Serving {len(FILES)} files from {ROOT} on 0.0.0.0:{PORT}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
