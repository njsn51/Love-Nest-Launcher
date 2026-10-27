import os
import re
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 18765
FILE_PATH = sys.argv[2] if len(sys.argv) > 2 else "test-payload.rar"
CHUNK = 64 * 1024
DELAY = 0.035

PAYLOAD = open(FILE_PATH, "rb").read()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))

    def _headers(self, status, length, content_range=None):
        self.send_response(status)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(length))
        if content_range:
            self.send_header("Content-Range", content_range)
        self.send_header("Connection", "close")
        self.end_headers()

    def do_HEAD(self):
        self._headers(200, len(PAYLOAD))

    def do_GET(self):
        start = 0
        end = len(PAYLOAD) - 1
        range_header = self.headers.get("Range")
        partial = False
        if range_header:
            match = re.match(r"bytes=(\d+)-(\d*)", range_header)
            if match:
                start = int(match.group(1))
                if match.group(2):
                    end = min(int(match.group(2)), end)
                if start <= end:
                    partial = True
        if start > end or start >= len(PAYLOAD):
            self.send_error(416)
            return
        length = end - start + 1
        if partial:
            self._headers(206, length, f"bytes {start}-{end}/{len(PAYLOAD)}")
        else:
            self._headers(200, length)
        try:
            offset = start
            while offset <= end:
                n = min(CHUNK, end - offset + 1)
                self.wfile.write(PAYLOAD[offset:offset + n])
                self.wfile.flush()
                offset += n
                time.sleep(DELAY)
        except (BrokenPipeError, ConnectionResetError):
            pass


server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
print(f"Range test server on http://127.0.0.1:{PORT}/test.rar ({len(PAYLOAD)} bytes)", flush=True)
server.serve_forever()
