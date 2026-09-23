## Why

Source PRDs: `PRD-006`, `PRD-002`

MCP tools can return binary artifacts, but Netclaw currently keeps only a text marker. The user and the model cannot access the actual artifact.

## What Changes

- Project MCP tool results into ordered model text and binary artifact candidates.
- Scan each candidate with the existing content scanner before any file write.
- Store accepted artifacts in the existing session artifact directory.
- Register each accepted artifact through the existing user file-output sink.
- Register supported artifacts through the existing model-input sink when the active model supports the required modality.
- Keep text available when one artifact fails admission.
- Add visible notes for rejected artifacts and model-modality gaps.
- Keep existing text-only MCP results and tool-error receipts compatible.

In scope for MVP:

- MCP image, audio, and embedded-resource data blocks that contain bytes.
- Existing content verification, MIME catalog, session storage, and tool-output paths.
- Deterministic positive and negative tests through the real MCP STDIO server.

Out of scope for MVP:

- New MIME signatures, antivirus functions, content extraction, or media conversion.
- A new MCP media pipeline, channel-specific MCP messages, or provider-specific policy.
- Prevention of large MCP responses before the MCP SDK materializes them.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-mcp`: Add verified artifact retention, user delivery, model-modality routing, and visible rejection behavior for MCP tool results.

## Impact

Affected code includes the MCP result formatter, the daemon MCP manager, daemon dependency registration, and the deterministic MCP test server.

The change adds no configuration, persistence record, actor message, or public wire contract. Existing text-only MCP tools keep their current result text.

Security impact:

- MCP MIME claims and filenames remain untrusted.
- The existing scanner must verify content before Netclaw writes or registers a file.
- Scanner-verified MIME selects the final extension and output MIME.
- Count and aggregate-byte limits bound post-SDK artifact work.

Operational impact:

- Accepted MCP artifacts persist below the current session artifact directory.
- Primary channel subscribers receive them through the existing `FileOutput` path.
- A daemon restart activates the behavior; no migration or feature flag is necessary.
