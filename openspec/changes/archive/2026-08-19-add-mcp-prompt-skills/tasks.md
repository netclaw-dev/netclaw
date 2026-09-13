## 1. Product and contract updates

- [x] 1.1 Update PRD-006 with prompt discovery, skill adaptation, permissions, and explicit first-slice exclusions.
- [x] 1.2 Update the `netclaw-operations` system skill and increment its metadata version.

## 2. Unified skill source and index

- [x] 2.1 Add typed file and MCP prompt sources to `SkillEntry`.
- [x] 2.2 Make `SkillRegistry` compose file and per-server MCP inventories in one atomic snapshot.
- [x] 2.3 Add per-audience skill index publication through the existing MCP server policy.
- [x] 2.4 Add registry and audience-index tests for refresh, collision, and permission behavior.

## 3. MCP prompt discovery

- [x] 3.1 Extend MCP client initialization and snapshots with prompt descriptors.
- [x] 3.2 Extend catalog polling and fingerprints with prompt descriptors.
- [x] 3.3 Publish canonical MCP prompt skill entries after connect and refresh.
- [x] 3.4 Add lifecycle tests for unsupported, empty, changed, failed, and removed prompt catalogs.

## 4. Prompt load

- [x] 4.1 Add the source-neutral MCP prompt loader boundary.
- [x] 4.2 Add the optional `skill_load` prompt argument map and source checks.
- [x] 4.3 Validate missing and unknown arguments before `prompts/get`.
- [x] 4.4 Render attributed role-tagged text and reject unsupported content.
- [x] 4.5 Add focused skill-load and MCP adapter tests.

## 5. Smoke and behavioral proof

- [x] 5.1 Add a deterministic parameterized prompt to both smoke MCP transports.
- [x] 5.2 Add an end-to-end smoke test for prompt discovery and load.
- [x] 5.3 Add relevant and unrelated MCP prompt eval cases.

## 6. Verification

- [x] 6.1 Run focused actor, daemon, and smoke MCP tests.
- [ ] 6.2 Run `./evals/run-evals.sh`.
- [x] 6.3 Run `dotnet slopwatch analyze`.
- [x] 6.4 Run `pwsh ./scripts/Add-FileHeaders.ps1 -Verify`.
- [x] 6.5 Run `openspec validate add-mcp-prompt-skills --strict`.
