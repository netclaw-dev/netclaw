Source PRDs: `PRD-001`, `PRD-004`, `PRD-007`

Canonical execution plan: [Coordination replacement plan](../../../.systematize/plans/coordination-replacement/plan.html)

## Why

The draft PRs #2420–2422 add overlapping session, child-run, and tool-loop state. Issue #2453 confirms that one invalid adoption event can poison replay, while the target refactor cannot read deployed `tta-v1` journal events.

Netclaw needs reliable parent and child work, safe recovery, and clear tool guidance. A smaller design can retain useful composition and cooperation behavior while reducing coordination state and test cost.

## What Changes

- Replace the draft coordination mechanisms from PRs #2420–2422 with a smaller design that uses current session and tool owners.
- `spawn_agent` and routed skill starts return after validated durable acceptance. They do not wait for child completion.
- Deliver a child result when it becomes ready. Do not order results by child completion time.
- Preserve parent safety: a result cannot interrupt an active model call or incomplete tool batch. Run ordinary user inputs one authority per turn: each turn takes the leading inputs of one requester and keeps the authority of that requester.
- Preserve approval authority, cancellation, recurrence control, and restart recovery.
- Preserve deployed journals. Define and test compatibility for `tta-v1`, `cra-v1`, `cre-v1`, and extended existing records before any new writer uses the target journal format.
- Preserve acceptance identities, terminal results, authority, pending consent, job lineage, and exactly-once transfer of delivered child results. Define safe handling for invalid records without executing uncertain input or inferring duplicates from prompt text.
- Keep the cause of missing source identity in issue #2453 unresolved until evidence proves it.
- Retain useful composition and cooperation guidance. Use a small set of behavior-based eval cases for delegation quality and guidance use.
- Keep this change within the single-process MVP. Do not add distributed workers, broad model benchmarks, or a large new eval framework.
- Treat reminder-origin recovery and prompt delivery of recovery failures as a separate bounded deployment prerequisite. Do not expand the coordination replacement to include a scheduling redesign.

## Capabilities

### New Capabilities

- `agent-coordination`: Add the reusable coordination skill and its workflow contract.
- `background-subagent-runs`: Define durable child-run lifecycle, result delivery, and restart behavior.

### Modified Capabilities

- `turn-loop-governance`: Replace repeated-tool and child-loop limits with the intended recurrence contract.
- `netclaw-session`: Define input progress, duplicate delivery, journal compatibility, and restart recovery rules.
- `netclaw-subagents`: Define child acceptance, result handoff, approval authority, cancellation, and recovery behavior.
- `tool-authorization`: Record durable child consent without permission to replay uncertain child work.
- `skill-execution-routing`: Preserve routed skill behavior and child launch authority.
- `netclaw-agent-memory`: Retain useful deployment guidance for delegation and cooperation.

## Impact

- Source PRDs: `PRD-001` FR-002, FR-003, FR-006, FR-011, FR-013, and FR-017; `PRD-004` CLI-001A; `PRD-007` identity and delegation guidance.
- `PRD-001` FR-017 records accepted child behavior. Keep wire fields and migration mechanics in design work.
- Candidate owners: `SessionInputFlow`, `SessionToolWorkflow`, `SessionExecutionLifetime`, and `SessionState`.
- The pinned draft adds `agent-coordination` and `background-subagent-runs`. It modifies `turn-loop-governance` for recurrence, plus the existing capabilities above. These are the current capability candidates for delta specs.
- The pinned draft also edits `progressive-tool-disclosure`, `session-config-decomposition`, and `session-state-machine`. Treat these as impact candidates only. Add a delta only if the approved replacement changes their requirements.
- Child workers remain ephemeral actors. The parent session owns durable acceptance, terminal results, and result delivery. Child start retries use the accepted run identity.
- Reminder redelivery remains outside this change. The cause of missing source identity in issue #2453 remains unresolved and needs a separate disposition before affected data can migrate.
- Security impact: preserve parent authority, requester identity, approval checks, and default-deny tool access across child work and recovery.
- Operational impact: operators must retain existing journals and receive a documented migration or compatibility path. Do not discard affected session data by default. Reminder repair remains a separate deployment prerequisite.
- Validation should cover input progress and closure, duplicate and distinct source identities, invalid-event rejection before persistence, child completion and failure, approval and cancellation, old journal replay, and a bounded set of eval cases. Test reminder recovery in its separate bounded fix.
