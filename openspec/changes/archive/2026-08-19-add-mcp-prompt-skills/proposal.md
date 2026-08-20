## Why

MCP servers can expose reusable prompts, but Netclaw only discovers their tools.
The skill system can expose these workflows without a second model-facing catalog.

This change implements GitHub issue #1806 and PRD-006 requirements MCP-003 through MCP-009.

## What Changes

- Discover prompt descriptors when an MCP server declares prompt support.
- Publish tools and prompts in one immutable MCP server generation.
- Map each prompt to `mcp__<server>__<prompt>` in the unified skill registry.
- Extend `skill_load` with an optional string argument map for MCP prompts.
- Validate prompt arguments before the runtime calls `prompts/get`.
- Preserve prompt roles, source identity, and server generation in the result.
- Apply the existing MCP server grant to prompt discovery and prompt use.
- Add a parameterized prompt to the smoke MCP server.
- Add focused tests and behavioral evals for prompt discovery and use.
- Update PRD-006 and the `netclaw-operations` system skill.

This slice does not add MCP resources, proactive subscriptions, an HTTP catalog, or a TUI change.
It also does not add MCP completion API support or a prompt-specific model tool.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-mcp`: Add prompt discovery, lifecycle, permission, and failure requirements.
- `skill-tools`: Let `skill_load` resolve file skills and MCP prompt skills by logical name.
- `skill-index-compression`: Include compact MCP prompt descriptors in the existing skill index.
- `audience-context-filtering`: Filter MCP prompt skills through the existing server grant.

## Impact

The change affects MCP connection snapshots, skill registry entries, `skill_load`, and session context assembly.
It also affects the smoke MCP server, MCP tests, skill tests, eval cases, PRD-006, and operations guidance.

No configuration shape changes.
No new permission category appears.
No new network endpoint appears.

The runtime will fail visibly for invalid arguments, stale generations, unsupported prompt content, and server faults.
