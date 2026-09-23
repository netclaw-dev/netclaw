# SPEC-009: MCP Integration Contract

Source PRDs: `PRD-006`, `PRD-004`, `PRD-002`

Cross-cutting terms use [the engineering glossary](GLOSSARY.md).

## Purpose

Define configuration, validation, policy enforcement, and diagnostics behavior
for MCP server integration.

## Server Configuration

- MCP servers are configured as named profiles
- each profile declares transport details and auth material source
- profiles are disabled by default until explicitly enabled

## Validation Contract

- `netclaw mcp validate` checks connectivity, protocol handshake, and tool
  discovery
- validation returns structured pass/fail with remediation guidance

## Runtime Contract

- tool discovery from enabled MCP profiles occurs during startup
- discovered tools are exposed through Netclaw tool registry
- tool invocation is gated by ACL/policy grants

## Result Artifact Contract

An MCP tool result can contain readable text and binary artifacts. Netclaw
projects both products once and keeps artifact bytes out of model text.

The following flow is schematic. It shows each required security and modality
decision.

```text
MCP SDK result
    |
    v
project text and apply copy limits to artifact candidates
    |
    v
scan bytes before a file write
    |
    v
require a supported scanner-verified MIME
    |
    v
write below the session artifact directory
    |
    +--> normal file output --> user
    |
    +--> catalog and active-model check --> normal model input
```

The MCP server name and MIME claim can inform the scan. Only the verified MIME
selects the final extension and output MIME.

| Decision | Owner | State lifetime |
|---|---|---|
| MCP result shape, content order, and copy limits | `McpToolResultFormatter` | Call-local |
| Scan order and defensive candidate limits | MCP artifact materializer | Call-local |
| Verified MIME | `IContentScanner` | Call-local |
| Stored path | `SessionStoragePaths.ArtifactDirectory` | Durable file |
| User and model registration | `ToolExecutionOutputs` | Call-local |
| Provider compatibility check | Session tool pipeline | Actor-local turn state |

The projection accepts at most ten candidates and 25 MiB of aggregate candidate bytes from one MCP result.
The materializer enforces the same limits before scan and file work.
These bounds do not limit memory that the MCP SDK already used to create the result.

The materializer registers outputs only after all candidates finish. Caller
cancellation removes files from the call and registers no artifact outputs.

Positive example: A server returns a valid PNG with `image/png`. The scanner
verifies it, and Netclaw sends one file output to the user. An image-capable
model also receives the file through the normal model-input path.

Negative example: A server labels three arbitrary bytes as `image/png`.
Netclaw writes no file, registers no output, and adds a safe rejection note.

Negative example: A server returns a valid PNG to a text-only model. The user
receives the file, but the model receives no image bytes. The tool result states
that the current model cannot inspect the image.

## Failure Handling

- unreachable MCP server does not crash session actors
- failed servers are marked degraded and excluded from invocation
- retries are bounded and observable

## Diagnostics

- report server state: healthy | degraded | unavailable
- include discovered tool count per server
- include last error and timestamp
- distinguish auth-required, auth-failed, and unreachable states on the daemon runtime path
- never claim OAuth auth failure from an offline-only probe when daemon truth is unavailable
