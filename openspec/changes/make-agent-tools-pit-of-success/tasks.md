## 1. PR 1 - Contract and sanitized evidence

- [x] 1.1 Add a PII-free fixture schema for observed tool-friction cases, including expected structured tool, outcome, approval requirement, and prohibited raw identifiers.
- [x] 1.2 Add sanitized cases for recursive search, batch reads, JSON projection, image metadata, spilled output continuation, failed file activity, and subagent catalog overexposure.
- [x] 1.3 Add deterministic fixture binding, mutation, and PII-audit tests so every policy-relevant field participates in the contract.
- [x] 1.4 Record the current core schema count/bytes and subagent schema count/bytes as a reproducible baseline without logging schema bodies.
- [x] 1.5 Validate this OpenSpec change strictly and commit the planning, deterministic fixture, and schema-footprint slice independently.

## 2. PR 2 - Progressive disclosure foundation

- [x] 2.1 Add explicit Core/Deferred registration metadata with Deferred as the safe default and preserve existing registry lookup identities.
- [x] 2.2 Mark the specified workspace/discovery tools Core and leave specialty first-party and MCP tools Deferred.
- [x] 2.3 Generalize the discovered-tool cache so leases and eviction apply to deferred first-party tools as well as MCP tools.
- [x] 2.4 Extend the compact audience-filtered capability catalog to include deferred first-party tool names and short hints without full schemas.
- [x] 2.5 Make search, suggestions, and load apply the same deployment, audience, grant, and feature filters without revealing hidden names.
- [x] 2.6 Add snapshots for exact core names, initial schema count/bytes, deferred discovery, load activation, eviction, and authorization-after-load.

## 3. PR 3 - Subagent progressive disclosure

- [x] 3.1 Seed subagents from the policy-exposed Core set instead of every discoverable registration.
- [x] 3.2 Add an ephemeral child exposure cache and intercept successful load_tool results within the child actor.
- [x] 3.3 Preserve recursive-spawn denial across child catalog, search, suggestions, load, and dispatch paths.
- [x] 3.4 Emit PII-free child core/deferred/loaded counts without tool payloads, paths, schema bodies, or hidden names.
- [x] 3.5 Add parent/child parity tests, child-local lease isolation, model-failure eviction, and high-cardinality catalog regression coverage.

## 4. PR 4 - Workspace path and outcome foundation

- [x] 4.1 Add one shared relative-path resolver using valid project directory then immutable session directory, never process cwd.
- [x] 4.2 Route existing first-party read, list, write, edit, and attach paths through the shared resolver before their existing scoped policies.
- [x] 4.3 Add the call-local typed outcome receipt and central exception/policy classifications without changing public INetclawTool signatures.
- [x] 4.4 Convert first-party workspace tools to report exact success, invalid-input, denial, not-found, transient, or correction outcomes.
- [x] 4.5 Replace argument-based RecentFiles inference for workspace tools with canonical successful file activity from the receipt.
- [x] 4.6 Prove failed file operations and failed set_working_directory calls cannot change RecentFiles, project scope, or project instructions.
- [x] 4.7 Add POSIX and native-Windows tests for relative paths, traversal, missing bases, symlinks, protected paths, and absolute-path compatibility.

## 5. PR 5 - Structured workspace primitives

- [x] 5.1 Implement bounded file_search with literal name/content modes, scoped root authorization, deterministic ordering, and no directory-symlink traversal.
- [x] 5.2 Implement atomic bounded file_read_many with complete prevalidation, per-file ceilings, total ceiling, and canonical successful activity.
- [x] 5.3 Implement bounded json_read using System.Text.Json and RFC 6901 pointers with atomic pointer validation.
- [x] 5.4 Extend file_read image inspection with bounded PNG/JPEG/GIF/WebP dimensions and malformed-header fail-closed behavior.
- [x] 5.5 Register only the small structured workspace schemas as Core and update compact capability hints.
- [x] 5.6 Add count/byte/result ceilings, cancellation, access-denial, symlink, binary, encoding, malformed JSON, and cross-platform tests.

## 6. PR 6 - Spill continuation and conditional schemas

- [ ] 6.1 Implement core tool_output_read by opaque call id with bounded windows and current-session-only spill resolution.
- [ ] 6.2 Make spill creation and continuation share one call-id sanitizer and reject traversal, controls, missing ids, and cross-session access.
- [ ] 6.3 Add source-generator support for explicit conditional tool variants and oneOf schemas without changing single-shape schemas.
- [ ] 6.4 Convert the observed mode-dependent first-party tools and reject zero/multiple matching branches before execution.
- [ ] 6.5 Add schema snapshots, generated-code tests, malformed-branch tests, spill-redaction tests, and public API compatibility checks.

## 7. PR 7 - Replay, documentation, and rollout proof

- [ ] 7.1 Replay every sanitized friction fixture through real registration, policy, dispatch, outcome, and working-context paths.
- [ ] 7.2 Prove representative structured workflows require no shell approval while equivalent shell/Python fallbacks remain approval-gated.
- [ ] 7.3 Update embedded operating guidance and repo-owned skills to prefer structured workspace tools and progressive discovery without memorizing command syntax.
- [ ] 7.4 Add operator diagnostics for core/deferred/loaded counts and outcome categories with PII and payload exclusion tests.
- [ ] 7.5 Run Release build, full tests, headers, formatting, strict OpenSpec, changed-file Slopwatch, PII audit, and API compatibility gates.
- [ ] 7.6 Run native Windows and Linux validation.
- [ ] 7.7 Rebase the final stack on upstream/dev, merge in order, remove merged worktrees, produce a binary-swap build, and harvest a new sanitized live window.
