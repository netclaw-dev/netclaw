## Context

Use the [Netclaw engineering glossary](../../../docs/spec/GLOSSARY.md) for the
terms in this design. A remediation code is the internal
`ToolRemediationCode` enum value that selects one bounded remediation.

Before this change, each tool path wrote its own correction instruction. The
receipt also stored a free-form string that no shared component consumed.

```text
parent path -> "Error ... call set_working_directory"
child path  -> separately constructed text
receipt     -> arbitrary remediation string
```

This design separates three facts:

```text
tool result       = factual text about what happened
tool receipt      = trusted internal outcome for the call
remediation code  = one closed enum value for a correctable outcome
```

The receipt is ephemeral. Netclaw does not store it in actor events, snapshots,
protobuf, or public tool results. The tool result remains a string for public
API compatibility.

## Goals / Non-Goals

**Goals:**

- Replace free-form remediation strings with a closed remediation code.
- Require one valid code for every recoverable correction.
- Produce the final correction instruction through one shared formatter.
- Give parent and child sessions the same instruction for the same facts.
- Preserve tool authority, approval behavior, persistence, and public APIs.

**Non-Goals:**

- Execute or retry the suggested action automatically.
- Grant file, shell, project, or approval authority.
- Add output continuation or successful spill data to a correction.
- Persist the remediation code across actor recovery.
- Add a public remediation API or wire format.

## End-to-End Example

A relative file call has no project or session directory:

```text
model call:
  file_read(Path = "README.md")

tool result:
  "Error: invalid_context: No project or session directory is available."

internal receipt:
  category    = RecoverableCorrection
  remediation = SetWorkingDirectory

final model message when set_working_directory is visible:
  "Error: invalid_context: No project or session directory is available.
   Next action: call set_working_directory with an allowed project directory
   for this task, then retry the failed tool call."
```

Netclaw does not call `set_working_directory`. The model decides whether to
make a new tool call. That call still runs normal authorization.

## Decisions

### Use a closed remediation code

`ToolRemediationCode` contains the supported corrective actions:

```text
SetWorkingDirectory            A relative tool call has no usable path base.
UseManagedTemporaryDirectory   A shell call can use the managed temporary
                               directory instead of host temporary storage.
ProvideUniqueOldString
                      file_edit found zero matches or several ambiguous matches.
```

`ToolInvocationReceipt` enforces these rules:

```text
RecoverableCorrection + defined code  -> valid
RecoverableCorrection + no code       -> reject construction
Success + remediation code            -> reject construction
undefined enum value                   -> reject construction
```

The receipt contains no dynamic path or free-form instruction. The factual tool
result carries details such as a path or match count.

### Format the correction once

`ToolRemediationPresenter` is a pure formatter. Parent and child execution paths
call it once before they deliver the tool-role message to the model.

```text
formatter inputs:
  current model-result text
  validated receipt
  whether set_working_directory is visible

formatter output:
  the original result
  plus one fixed next action when the action is usable
```

The formatter does not inspect tool arguments. It does not execute a tool. It
does not change the receipt or grant authority.

### Do not recommend a hidden tool

Some audiences and subagents cannot use `set_working_directory`. Netclaw must
not tell those models to call a tool that is absent from their tool set.

```text
receipt remediation = SetWorkingDirectory

set_working_directory visible:
  append the fixed next action

set_working_directory hidden:
  return the factual error unchanged
```

This rule prevents an impossible instruction and avoids disclosure of a hidden
tool name. It does not turn the correction into another action. The caller can
still provide a valid absolute path or change the session configuration.

### Keep retry state separate

The managed-temporary correction key prevents a correction loop in the shell
approval path. The remediation code does not replace that state.

```text
first shell call uses host temporary storage
  -> result suggests the managed temporary directory
  -> receipt code is UseManagedTemporaryDirectory
  -> session arms the existing correction key

model retries the exact corrected call
  -> session consumes the key
  -> normal shell authorization runs again
```

Project declaration and file-edit corrections have no automatic retry state.
The model must author the next call.

### Keep persistence boundaries unchanged

The main session persists its tool-role message. A subagent keeps its tool-role
message only for the child run. Neither actor persists the receipt or
remediation code.

```text
main session before actor restart:
  durable chat message = factual result + fixed next action
  ephemeral receipt    = RecoverableCorrection(SetWorkingDirectory)

main session after actor restart:
  durable chat message = restored
  ephemeral receipt    = absent
  automatic action     = none

subagent after child completion:
  child chat message = discarded
  ephemeral receipt  = discarded
```

## User Stories

### A file call needs a project directory

1. The model calls `file_read` with a relative path.
2. No project or session base exists.
3. The tool returns a factual `invalid_context` result.
4. The receipt uses `RecoverableCorrection(SetWorkingDirectory)`.
5. The formatter adds one fixed action when the tool is visible.
6. The model can declare a project and retry.

### A file edit has an ambiguous match

1. The model calls `file_edit` with an `OldString` that matches three locations.
2. The tool changes no file.
3. The result reports the match count and canonical file path.
4. The receipt uses `RecoverableCorrection(ProvideUniqueOldString)`.
5. The formatter tells the model to use a unique value or `ReplaceAll=true`.

### A shell call should use the managed temporary directory

1. The model authors a shell call under host temporary storage.
2. Policy proposes the managed temporary directory as the bounded alternative.
3. The receipt uses `RecoverableCorrection(UseManagedTemporaryDirectory)`.
4. The formatter tells the model to use the managed temporary directory.
5. A later retry still passes the complete shell authorization policy.

## Risks / Trade-offs

### A correction needs dynamic detail

Example: `file_edit` must report the number of matches and the file path.
The tool result carries those facts. The receipt still carries only the enum.

### Old producer text repeats the formatter action

Example: a tool result already says "call set_working_directory." The formatter
would add the same instruction again. Updated producers therefore return only
the factual error. Integration tests assert one final action.

### A producer creates an incomplete receipt

Example: a producer creates `RecoverableCorrection` without a code. The receipt
constructor rejects it before the actor receives it.

### Direct tool tests do not contain the final instruction

Direct tool tests see the factual result and receipt. Parent and child actor
tests verify the final model message after the shared formatter runs.

## Migration Plan

1. Add `ToolRemediationCode` with the three supported values.
2. Convert the path, managed-temporary, and `file_edit` producers.
3. Add `ToolRemediationPresenter` to parent and child message construction.
4. Remove duplicate action text from those producers.
5. Add receipt validation and actor integration tests.

Rollback is a code rollback. No stored data requires migration.

## Open Questions

None.
