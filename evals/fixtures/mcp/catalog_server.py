#!/usr/bin/env python3
"""A deterministic MCP stdio server that gives the eval daemon a full tool catalog.

A production daemon has many MCP tools. Their names and descriptions share common
words ("list", "get", "status") with the built-in Netclaw tools. A search for a
built-in tool must still find it in that catalog. Each tool here returns an empty
result; no eval case needs its data.
"""

import json
import sys

TOOLS = [
    ("list_projects", "List the projects of a workspace with their status."),
    ("list_workspaces", "List all workspaces that the caller can read."),
    ("list_archived", "List archived documents. Use restore_document to restore one."),
    ("list_documents", "List the documents of a project, newest first."),
    ("list_tasks", "List the tasks of a project with their status and due dates."),
    ("list_recent_changes", "List recent changes across all projects."),
    ("list_members", "List the members of a workspace and their roles."),
    ("list_labels", "List the labels that a project defines."),
    ("get_project_context", "Get project information. With no project ID, list all projects."),
    ("get_project_status", "Get the status of a project and list its open tasks."),
    ("get_document", "Get one document by ID. Use list_documents to find IDs."),
    ("get_task_status", "Get the status and run history of one task."),
    ("search_documents", "Search documents by text and list the matches."),
    ("search_tasks", "Search tasks by text, status, or schedule."),
    ("create_task", "Create a task in a project."),
    ("update_task", "Update the status or the schedule of a task."),
    ("archive_document", "Archive a document by ID."),
    ("restore_document", "Restore an archived document by ID."),
]


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
                    "capabilities": {"tools": {}},
                    "serverInfo": {
                        "name": "netclaw-eval-catalog-server",
                        "version": "1.0.0",
                    },
                },
            })
        elif method == "tools/list":
            send({
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {
                    "tools": [{
                        "name": name,
                        "description": description,
                        "inputSchema": {
                            "type": "object",
                            "properties": {"id": {"type": "string"}},
                        },
                    } for name, description in TOOLS],
                },
            })
        elif method == "tools/call":
            send({
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {"content": [{"type": "text", "text": "[]"}]},
            })
        elif request_id is not None:
            send({
                "jsonrpc": "2.0",
                "id": request_id,
                "error": {"code": -32601, "message": f"Method not found: {method}"},
            })
    except Exception as error:
        sys.stderr.write(f"catalog server error: {error}\n")
        sys.stderr.flush()
