## ADDED Requirements

### Requirement: Sub-agent approval bridge preserves prompt correlation

Sub-agent approval prompts SHALL use the same channel-agnostic `ToolInteractionRequest` contract as parent-session tool prompts. The request SHALL use a parent-scoped correlation call id that is unique per bridged approval request while preserving the child call id as part of the correlation value. The request SHALL preserve tool name, display text, exact blocked patterns, candidate verbs, per-candidate directories, cwd, messy-command flag, computed approval options, requester identity, principal, audience-derived authority, and adopted-context safety metadata from the parent turn authority context.

#### Scenario: Sub-agent prompt includes approval candidates and options
- **GIVEN** a sub-agent shell tool call requires approval
- **WHEN** the parent approval bridge emits the prompt
- **THEN** the prompt includes the exact blocked patterns shown to the user
- **AND** the prompt includes candidate verbs and per-candidate directories for grant persistence
- **AND** the prompt includes the same computed approval options the parent approval gate produced

#### Scenario: Sub-agent prompt carries adopted-context safety metadata
- **GIVEN** a sub-agent was spawned from a parent turn with adopted context
- **WHEN** the sub-agent emits an approval prompt
- **THEN** the prompt includes adopted-context and third-party adopted-context flags
- **AND** the prompt includes adopted speaker ids when present

#### Scenario: Duplicate child call ids do not share approval state
- **GIVEN** two bridged sub-agent approval waits have the same child-local tool call id
- **WHEN** the parent approval bridge emits prompts for both waits
- **THEN** each prompt uses a distinct parent-scoped call id
- **AND** approving one prompt cannot complete or authorize the other wait

### Requirement: Sub-agent approval responses do not execute expired work

Approval responses for sub-agent prompts SHALL execute a tool only while the originating sub-agent wait is still live and correlated to the pending call id. A response that arrives after the sub-agent wait was cancelled, completed, or abandoned SHALL fail closed as expired and SHALL NOT execute the gated tool.

#### Scenario: Late approval after cancellation is expired
- **GIVEN** a sub-agent approval prompt is pending
- **AND** the parent cancels the `spawn_agent` call before the user responds
- **WHEN** the user later approves the stale prompt
- **THEN** the sub-agent tool is not executed
- **AND** the response is treated as expired or no-longer-pending

#### Scenario: Live session response requires live approval wait
- **GIVEN** the parent session still has persisted prompt metadata for a sub-agent approval
- **AND** the child sub-agent approval wait has already been cancelled or completed
- **WHEN** an approval response arrives while the parent session is processing
- **THEN** the response is rejected as expired
- **AND** no approval grant is applied to execute stale sub-agent work

#### Scenario: Durable grant is written only after live wait is claimed
- **GIVEN** a sub-agent approval response requests a session or persistent grant
- **AND** the child approval wait is cancelled before the parent claims the response
- **WHEN** the response is handled
- **THEN** no durable approval grant is written
- **AND** the response is rejected as expired

#### Scenario: No bridge fails closed
- **GIVEN** a sub-agent tool call requires approval
- **AND** no parent approval bridge is available
- **WHEN** the tool executor reports that approval is required
- **THEN** no approval prompt is emitted
- **AND** the gated tool is not executed
- **AND** the sub-agent completes with a failed `SubAgentResult`
