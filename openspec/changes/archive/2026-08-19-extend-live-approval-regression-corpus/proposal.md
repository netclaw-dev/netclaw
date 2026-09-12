## Why

PRD-002 SEC-009 requires shell authorization to remain fail-closed, while live approval-fatigue work requires evidence that ordinary diagnostics are not prompted unnecessarily. The post-1952 harvest classifies 21 representative prompts, but those sanitized command shapes are not executed through the coordinator and therefore cannot protect later policy simplification.

## What Changes

- Curate parse-preserving, identity-free commands for all 21 post-1952 evidence cases.
- Bind every curated command to its source evidence ID, classification, intended outcome, approval shape, and actor-contact count.
- Execute the cases through the real shell policy coordinator alongside the existing D, A, and L matrices.
- Keep expected approvals and current ShellSyntaxTree fact gaps strict; do not reinterpret executable-private arguments or convert agent-alignment guidance into authority.
- Add mutation checks for source shapes, links, classifications, commands, outcomes, corrections, approval shapes, and actor counts.

Out of scope: changing production policy, widening a safe catalog, parsing `gh` operations in Netclaw, adding Bash arithmetic grammar, or claiming the sample represents every prompt in the source window.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: Extend the executable sanitized approval corpus with post-1952 live prompt regressions and exact evidence linkage.

## Impact

- **Tests and evidence:** `netclaw-policy-fixtures.json`, its source-generated models, coordinator fixture tests, evidence-contract tests, and the post-1952 harvest linkage.
- **Security:** The change adds no authority. Expected approvals and unresolved general parser facts remain promptable through the real coordinator.
- **Operations:** Future policy and refactor pull requests receive a broader should-prompt/should-allow regression gate derived from live traffic.
- **APIs and persistence:** No public API, actor protocol, approval store, session history, configuration, or dependency change.
