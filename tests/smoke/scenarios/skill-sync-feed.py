#!/usr/bin/env python3
"""Serve a mutable loopback RFC skill feed for the native smoke scenario."""

from __future__ import annotations

import hashlib
import http.server
import json
import pathlib
import sys


def content_digest(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def skill_content(phase: str) -> bytes:
    version = "1.0.0" if phase == "A" else "2.0.0"
    return (
        "---\n"
        "name: smoke-feed-skill\n"
        f"description: Native skill sync phase {phase}.\n"
        "metadata:\n"
        f'  version: "{version}"\n'
        "---\n\n"
        f"# Native skill sync phase {phase}\n"
    ).encode()


def resource_content(phase: str) -> bytes:
    return f"native skill sync resource phase {phase}\n".encode()


class FeedHandler(http.server.BaseHTTPRequestHandler):
    server: "FeedServer"

    def log_message(self, format: str, *args: object) -> None:
        return

    def do_GET(self) -> None:
        phase = self.server.phase_file.read_text().strip()
        version = "1.0.0" if phase == "A" else "2.0.0"
        skill = skill_content(phase)
        resource = resource_content(phase)
        base_url = f"http://127.0.0.1:{self.server.server_port}"

        if self.path == "/health":
            self.send_body(b"healthy\n", "text/plain")
            return
        if self.path == "/.well-known/agent-skills/index.json":
            index = {
                "skills": [
                    {
                        "name": "smoke-feed-skill",
                        "type": "skill",
                        "description": "Native skill sync smoke proof",
                        "url": f"{base_url}/skill.md",
                        "digest": f"sha256:{content_digest(skill)}",
                        "version": version,
                        "resources": [
                            {
                                "path": "references/proof.txt",
                                "url": f"{base_url}/proof.txt",
                                "digest": f"sha256:{content_digest(resource)}",
                            }
                        ],
                    }
                ]
            }
            self.send_body(json.dumps(index).encode(), "application/json")
            return
        if self.path == "/skill.md":
            self.send_body(skill, "text/markdown")
            return
        if self.path == "/proof.txt":
            self.send_body(resource, "text/plain")
            return
        if self.path == "/subagents/v1/index.json" or self.path == "/manifest.json":
            self.send_error(404)
            return
        self.send_error(404)

    def send_body(self, body: bytes, content_type: str) -> None:
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class FeedServer(http.server.ThreadingHTTPServer):
    def __init__(self, phase_file: pathlib.Path):
        super().__init__(("127.0.0.1", 0), FeedHandler)
        self.phase_file = phase_file


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("usage: skill-sync-feed.py <phase-file>")

    server = FeedServer(pathlib.Path(sys.argv[1]))
    print(
        f"[skill-feed:listening] http://127.0.0.1:{server.server_port}",
        flush=True,
    )
    server.serve_forever()


if __name__ == "__main__":
    main()
