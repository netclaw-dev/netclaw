## Context

Workspace tools return model-facing text through the public `INetclawTool`
contract. They also publish one internal call-local receipt. The receipt now
stores a free-form remediation string. Parent and child execution paths build
some correction text separately. No shared component turns the receipt into a
model instruction.

The receipt is ephemeral. It does not enter actor persistence, protobuf,
snapshots, or public tool results. This boundary is the right place for trusted
machine facts. The model-facing result remains a string for compatibility.

## Goals / Non-Goals

**Goals:**

- Replace free-form remediation identifiers with a closed internal type.
- Require every recoverable correction to carry one valid remediation.
- Generate the final correction instruction through one parent-and-child path.
- Keep failure detail in the originating tool or policy result.
- Preserve tool authority, approval behavior, actor persistence, and public APIs.

**Non-Goals:**

- Automatically retry, rewrite, or execute a corrected tool call.
- Add general advice, output continuation, or successful spill data to remediation.
- Change tool exposure, shell parsing, or approval policy.
- Persist remediation across turns or actor recovery.
- Add a public remediation API or wire format.

## Decisions

### Use a closed remediation code

Add an internal `ToolRemediationCode` enum for the supported actions. Keep
dynamic paths in the bounded factual result. The receipt carries only the enum.

The first code set is:

- `SetWorkingDirectory`
- `UseSessionScratch`
- `ProvideUniqueOldString`

`ToolInvocationReceipt` validates the enum. A `RecoverableCorrection` receipt
requires one defined code. Every other outcome rejects remediation.

This is safer than a string because new values require a deliberate code change.
It also avoids a second free-form field that could carry invalid paths or
instruction-like text.

### Present the correction once

Add one internal presenter in the actor tool-execution layer. It accepts the
tool-role message, the validated receipt, and whether `set_working_directory`
is visible. It appends one short action for the remediation code. Parent and
child paths call it once after execution and before result delivery.

The original result owns the failure facts. The presenter owns only the next
action. Tools and approval policy therefore stop repeating the action text.
This keeps the public string result useful for direct tool callers and gives
both actor paths the same instruction.

The presenter is pure. It does not inspect tool arguments. It suppresses the
project-declaration action when that tool is hidden. It does not grant
authority. It does not call another tool. It returns the original result for a
non-corrective receipt.

### Keep retry state separate

The existing session-scratch correction key remains the loop-control mechanism
for the shell approval path. The typed remediation does not replace that state.
It describes the next action only. Project declaration and file-edit corrections
remain model decisions and do not gain automatic retry state.

### Keep actor and persistence boundaries unchanged

Receipts remain internal call-local data. Batch and streaming messages may carry
the internal receipt in memory. Durable chat messages still contain only the
model-facing string. Actor recovery does not restore remediation receipts.

## Risks / Trade-offs

- **Risk: A new correction needs more context.** -> Put bounded facts in the
  originating result. Keep the receipt closed and do not parse the result.
- **Risk: The presenter repeats legacy action text.** -> Remove action wording
  from each updated producer. Keep tests that assert one action in the final
  parent and child message.
- **Risk: A producer emits an incomplete corrective receipt.** -> Make the
  receipt constructor reject `RecoverableCorrection` without remediation.
- **Risk: Direct tool tests no longer see full guidance.** -> Direct tool results
  keep the failure reason. Actor integration tests own the final instruction.

## Migration Plan

1. Add the closed remediation enum and strict receipt validation.
2. Convert all three current string producers.
3. Add the shared presenter to parent and child result construction.
4. Remove repeated action text from producers.
5. Add constructor, direct-tool, parent, and child regressions.

Rollback is a code rollback. There is no stored data to migrate.

## Open Questions

None.
