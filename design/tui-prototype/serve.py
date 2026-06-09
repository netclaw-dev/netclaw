#!/usr/bin/env python3
"""Static server for the prototype with caching disabled.

ES modules cache aggressively; serving no-store guarantees a reload always
picks up the latest source (for the dev loop and the tailnet viewer alike).
"""
import http.server
import socketserver
from pathlib import Path

ROOT = str(Path(__file__).resolve().parent)
PORT = 8777


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=ROOT, **kwargs)

    def end_headers(self):
        self.send_header("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0")
        self.send_header("Pragma", "no-cache")
        super().end_headers()

    def log_message(self, *args):
        pass


socketserver.TCPServer.allow_reuse_address = True
with socketserver.TCPServer(("127.0.0.1", PORT), Handler) as httpd:
    httpd.serve_forever()
