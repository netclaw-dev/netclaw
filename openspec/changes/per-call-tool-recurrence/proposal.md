## Why

The hard tool limits stop useful long tasks but miss short loops that interleave other calls.
The approved plan requires exact recurrence protection and removes both limits in the first runtime change.

Source requirements: [PRD-001](../../../docs/prd/PRD-001-netclaw-mvp.md), FR-002, FR-003, FR-004, FR-011, and FR-012.
[PRD-006](../../../docs/prd/PRD-006-mcp-tool-integration.md) supplies the tool-result contract.
Use [the engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

## What Changes

- Extend `TurnStateTracker` with exact per-call recurrence across unrelated feedback rounds.
- Preserve the existing adjacent period-one through period-three guard where no valid repeat rule applies.
- Count duplicate calls once per feedback round. Preserve distinct typed outcomes and separate multiplicity evidence.
- Refuse repeated calls before dispatch. Settle terminal loops without another model response.
- Admit narrow runtime-owned pending-job status rechecks. Never infer a repeat exception from model text.
- Preserve unresolved parent evidence through framework-owned journal events and snapshots.
- **BREAKING**: Remove `Session.MaxToolIterationsPerTurn`, its schema entry, and the child iteration limit.
- Retain operation deadlines, explicit cancellation, empty-response guards, and usage counters.
- Preserve the prior laboratory, replay, shadow, strict model, sensitivity, and focused mutation acceptance gates.
- Update the operations skill and configuration guidance in the same PR.

In scope: deterministic signatures, both current actor tool loops, parent recovery, bounded recent evidence, targeted evals, and configuration migration.
Out of scope: background children, peer messages, semantic classifiers, supervisor agents, renewable budgets, and durable child resumption.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `turn-loop-governance`: Replace count-based termination with exact recurrence, repeat rules, durable parent evidence, and runtime settlement.
- `netclaw-subagents`: Remove the child ceiling and require the shared detector contract.
- `netclaw-tools`: Retain typed result evidence, explicit wait facts, and paired synthetic refusals.
- `tool-authorization`: Adopt canonical admitted authority at the durable task-consumption boundary.

## Impact

Owners: `TurnStateTracker`, `ToolCycleSignatureFactory`, `ActiveToolBatchTracker`, `LlmSessionActor`, and `SubAgentActor`.
The status tool supplies wait facts through the existing internal receipt path.
The session owns framework-safe detector records; children retain actor-local evidence.
The change adds no package or operator detector knob.

### Security and operational impact

Normal ACL, path, approval prompt, and tool timeout gates still apply to admitted calls.
Detector feedback grants no authority and claims no successful execution.
Diagnostics contain decisions, counts, and evidence-gap categories. They contain no private payload or digest.
Private session persistence can retain detector digests; published artifacts cannot contain private replay data.
Activation follows the proof gates. This task does not deploy the daemon.
New `tta-v1` records require a compatible reader or a recorded pre-upgrade backup before a rollback to the prior binary.
Backup restoration loses journal state after the backup and cannot undo external effects.
The change does not claim an executed rollback procedure.

### Prior change reconciliation

`stop-repeated-tool-cycles` describes work already present in the base branch and unfinished rollout gates.
This change supersedes its staged-limit-removal, final-model-call, and actor-local-only detector proposals.
Its unfulfilled proof obligations remain mandatory. Existing completed repairs remain preserved behavior.
