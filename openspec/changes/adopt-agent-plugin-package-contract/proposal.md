## Why

PRD-004 makes the CLI the primary operator interface for external capabilities.
The current stack exposes Codex skill packages as the core plugin model.

Netclaw must use the portable Agent Plugins package as its primary contract.
Host-specific manifests must remain explicit compatibility formats.

## What Changes

- **BREAKING**: Replace `netclaw skill plugin` with the top-level `netclaw plugin` command family before release.
- **BREAKING**: Replace `/api/skills/plugins` with `/api/plugins` before release.
- Add portable Agent Plugins 1.0.0 package support through root `plugin.json`.
- Keep Codex package support through an explicit compatibility adapter.
- Separate Git archive acquisition from package format interpretation.
- Publish supported skills through the existing skill inventory and logical skill tools.
- Keep one external sync actor and add a focused managed plugin sync participant.
- Add JSON output for plugin list operations.
- Preserve safe daemon problem details in CLI failures.
- Align issues #2134, #2135, and website issue #119 with the portable contract.

This change supports public GitHub repositories only.
This change does not add a marketplace, private Git credentials, plugin subagents, MCP activation, hooks, LSP configuration, or host execution.

## Capabilities

### New Capabilities

- `managed-agent-plugins`: Define package formats, source lifecycle, durable state, secure acquisition, sync, publication, and failure isolation.

### Modified Capabilities

- `netclaw-cli`: Add the top-level plugin command family and its stable text, JSON, error, confirmation, and exit contracts.
- `skill-tools`: Add managed plugin skills to inventory precedence without physical path disclosure or extra authority.

## Impact

This change affects PRD-001, PRD-002, and PRD-004 behavior.
It affects configuration, JSON schema, SQLite state, daemon routes, CLI routes, sync services, skill inventory, tests, and system skills.

The daemon remains the authority for validation, reference resolution, configuration writes, acquisition, and publication.
The CLI remains a thin presentation client.

The existing archive limits, path containment checks, scanner contract, immutable revision directories, and managed-file protection remain required.
Manifest fields cannot grant Netclaw tool or subagent authority.

Operators will see a new top-level command family and a new portable package format.
Existing stack commands and routes have no release compatibility guarantee because they have not shipped.
