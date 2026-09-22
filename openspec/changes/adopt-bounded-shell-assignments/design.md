## Context

See [proposal.md](proposal.md) for the reason for this change.

ShellSyntaxTree `0.4.0-beta.5` supplies typed facts for a small set of exact shell assignments.
Netclaw currently treats those forms as unresolved and offers only a one-time approval.
The native shell environment already owns both parser construction and child process construction.
Approval candidates already cross the policy, parent bridge, actor, and store boundaries.
The approval store uses a closed version-3 JSON form and rejects malformed data.

## Goals / Non-Goals

**Goals:**

- Bind each parser initial-state assertion to the exact child process contract.
- Add the assignment constraint to the existing approval candidate and grant path.
- Permit reuse only when the command tokens and assignment constraint both match.
- Keep raw assignment values out of the durable approval store.
- Preserve all current authority checks and fail closed on incomplete facts.

**Non-Goals:**

- Netclaw will not parse shell assignment grammar.
- Netclaw will not define executable-specific environment rules.
- This change will not add a user configuration option.
- This change will not authorize an assignment that ShellSyntaxTree leaves unresolved.

## Decisions

### The shell environment owns the parser mode

`ShellExecutionEnvironment` already carries the data that controls analysis and process launch.
It will select the strongest valid ShellSyntaxTree initial-state mode from that immutable data.

The Bash environment will select `FreshNonInteractiveNoStartup` only for GNU Bash 5.2 or 5.3 under the sanitized `/bin/bash -c` contract.
The sanitizer must remove each startup override, imported function, and loader variable that the parser contract names.
The fixed Bash arguments must exclude behavior options that can execute hidden content, such as xtrace.

The PowerShell environment will select `IsolatedNonInteractiveNoProfile` only for a new noninteractive process with profiles disabled.
Any contract mismatch will select unknown state or reject the final launch.

This design reuses the existing environment object.
A separate mode flag could drift from the actual launch data and would duplicate security state.

### A required typed constraint qualifies each approval candidate

Netclaw will add a required `ApprovalAssignmentConstraint` value to each approval candidate and bridge form.
The constraint has exactly two valid states: `None` and `ExactDigest`.
`ExactDigest` contains an `ApprovalAssignmentDigest` value object.
The digest validates the `sha256:` prefix and 64 lowercase hexadecimal characters.
It exposes its text through an explicit `Value` property.

The coordinator will create one digest only when ShellSyntaxTree supplies complete assignment facts for the occurrence.
The digest input uses one canonical byte format in this order:

1. Four ASCII magic bytes equal to `NCAS`.
2. A four-byte unsigned version equal to `1` in network byte order.
3. One shell byte, where Bash is `1` and PowerShell is `2`.
4. A four-byte unsigned assignment count in network byte order.
5. Each name as strict UTF-8 after a four-byte byte length.
6. One scope byte, where shell state is `1` and command environment is `2`.
7. One environment-effect byte, where false is `0` and true is `1`.
8. Each exact effective value as strict UTF-8 after a four-byte byte length.

The UTF-8 encoder rejects invalid UTF-16 instead of using replacement text.
Byte lengths and fixed enum values prevent platform and delimiter ambiguity.
SHA-256 gives a stable identity without durable raw values.
The typed value prevents accidental use as a general string.

A synthetic token was considered for the existing verb list.
That option could expose a secret value through logs and user interfaces.
It could also confuse command-token policy with an environment constraint.

### Every authority boundary carries and compares the digest

The call-local `ApprovalCandidate` will require the constraint in its constructor.
The parent bridge candidate will require the same constraint in its constructor.
`ToolApprovalGrant` will carry the candidate through prompt response and actor persistence.
The actor will store it in the durable `ApprovalEntry` for token-prefix shell grants.

Each bridge and pending record will reject an invalid constraint state.
The pending approval state and recovery record will preserve the same constraint.
The approval actor owns the durable grant after it validates the response against the pending request.

An assignment-qualified candidate can match only an entry with the same digest.
An unqualified candidate can match only an entry without a digest.
Legacy exact entries and non-shell entries cannot carry a digest.

The digest check occurs before scope reuse.
Session, folder, repository, and global scope checks remain unchanged after that check.
The safe-verb policy accepts only the explicit `None` state.
A digest creation error keeps the complete call on the one-time approval path.

### Assignment approvals use versioned reusable option keys

An assignment-qualified prompt will use new versioned keys for each reusable scope.
The `Once` and `Deny` keys will keep their current wire values.
The current runtime will map each versioned key to its existing approval decision.
It will accept a key only when the pending prompt offered that exact key.

An older runtime will ignore the new candidate fields in a pending journal event.
Its closed option-key switch will map each versioned reusable key to `Denied`.
This behavior prevents an older runtime from storing an unqualified grant after a rollback.

A resolved reusable decision will redrive the original call with its exact one-time key.
The redrive plan will not carry a reusable decision override.
The durable approval store remains the authority for later calls.
An older strict reader will reject the qualified store member before it can grant access.

### The version-3 store uses one optional closed-form member

The JSON codec will add `assignmentDigest` to the allowed version-3 token-prefix form.
The writer will omit it for an unqualified grant.
The reader will reject it on legacy exact entries and non-shell entries.
The reader will reject a malformed digest and make the complete store unavailable.

The comparer, list output, revoke path, and duplicate detection will include the digest.
This preserves distinct grants for the same tokens under different assignment constraints.

An approval-store version increase was considered.
The current version-3 format already supports optional members on closed entry forms.
The strict older reader will reject the new member and fail closed after a rollback.

### Unresolved facts keep one-time authority

ShellSyntaxTree must report each assignment with an exact effective value and a supported scope.
Netclaw will not create a reusable candidate when any required fact is missing.
The current complex-command path will then offer `Once` and `Deny`.

Hard-deny, path, audience, protected-path, repository, and final launch checks retain their current order and authority.

## Actor Boundaries and Persistence

The approval coordinator owns digest creation for the current call.
The parent approval bridge transports the digest without a new authority decision.
The approval actor validates the response against pending state and owns grant creation.
The store codec validates the durable representation before any entry can authorize.

Raw assignment values exist only in the current ShellSyntaxTree result and existing command evidence.
Approval candidate records and the approval JSON store receive only the typed constraint or its digest.

## Failure Modes and Recovery

- **Incomplete parser facts**: The coordinator offers only one-time approval.
- **Parser and launch mismatch**: The final policy check blocks process start.
- **Lost constraint across a bridge**: A required constructor or state check rejects the copy.
- **Changed assignment value**: The calculated digest changes and reuse fails.
- **Malformed durable digest**: The codec marks the complete store unavailable.
- **Older runtime reads a pending prompt**: Its closed key map denies each versioned reusable option.
- **Older runtime reads a resolved prompt**: The redrive plan grants only the exact call once.
- **Older runtime reads the approval store**: Its strict reader rejects entries with `assignmentDigest`.

An operator can recover from the rollback case with the existing approval-store backup.
The operator can also remove assignment-qualified entries before the rollback.

## Risks / Trade-offs

- **A digest hides the raw value from store inspection** → The prompt and current command evidence show the assignment before approval.
- **A hash can reveal a low-entropy value through guesses** → The store omits names and values, and file access remains restricted.
- **Older binaries reject durable entries** → Versioned option keys and strict store parsing fail closed after rollback.
- **A future parser adds a scope** → Unknown scope values prevent reusable grant creation until Netclaw supports them.
- **A launch contract changes after analysis** → The final policy check uses the same environment and rejects the mismatch.
- **A host administrator replaces a shell at the same path** → The daemon trusts the probed host until restart.

The host installation is a trusted boundary.
The agent cannot replace `/bin/bash` or the selected PowerShell host through ordinary user authority.
An administrator must restart Netclaw after a shell upgrade or replacement.

## Migration Plan

1. Publish ShellSyntaxTree `0.4.0-beta.5` after its security and release gates pass.
2. Update Netclaw to the public package.
3. Add the typed digest across the existing approval path.
4. Update the version-3 codec, operations skill, runbook, and tests.
5. Run focused mutation tests, evaluation tests, and the native smoke suite.
6. Deploy the Netclaw binary after the pull request passes its gates.

Existing approval entries load with no digest.
They map to the explicit `None` constraint and do not authorize assignment-qualified candidates.
The first persistent approval for such a candidate writes the optional member.
