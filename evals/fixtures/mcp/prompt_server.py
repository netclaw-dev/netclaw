#!/usr/bin/env python3
"""A deterministic MCP stdio server for prompt skill evals."""

import json
import sys


def send(message):
    sys.stdout.write(json.dumps(message, separators=(",", ":")) + "\n")
    sys.stdout.flush()


for line in sys.stdin:
    try:
        request = json.loads(line)
        method = request.get("method")
        request_id = request.get("id")

        if method == "initialize":
            send({
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {
                    "protocolVersion": request["params"]["protocolVersion"],
                    "capabilities": {
                        "tools": {},
                        "prompts": {"listChanged": False},
                    },
                    "serverInfo": {
                        "name": "netclaw-eval-prompt-server",
                        "version": "1.0.0",
                    },
                },
            })
        elif method == "tools/list":
            send({"jsonrpc": "2.0", "id": request_id, "result": {"tools": []}})
        elif method == "prompts/list":
            send({
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {
                    "prompts": [{
                        "name": "property-analytics",
                        "title": "Property analytics workflow",
                        "description": (
                            "Use this skill for complete-month property analytics "
                            "through the live query endpoint."
                        ),
                        "arguments": [{
                            "name": "property",
                            "description": "The property identifier.",
                            "required": True,
                        }],
                    }],
                },
            })
        elif method == "prompts/get":
            property_name = request.get("params", {}).get("arguments", {}).get("property", "")
            send({
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {
                    "description": "A deterministic analytics workflow.",
                    "messages": [{
                        "role": "user",
                        "content": {
                            "type": "text",
                            "text": (
                                "EVAL-MCP-PROMPT-7421: Use the live query endpoint for "
                                f"property {property_name}. Compare only complete calendar months."
                            ),
                        },
                    }],
                },
            })
        elif request_id is not None:
            send({
                "jsonrpc": "2.0",
                "id": request_id,
                "error": {"code": -32601, "message": f"Method not found: {method}"},
            })
    except Exception as error:
        sys.stderr.write(f"prompt server error: {error}\n")
        sys.stderr.flush()
