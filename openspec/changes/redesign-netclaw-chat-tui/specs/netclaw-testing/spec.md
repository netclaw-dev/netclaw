## ADDED Requirements

### Requirement: Chat output contracts have deterministic headless proof

Headless tests SHALL inject every supported `SessionOutput` type through the
chat presentation boundary. Tests SHALL verify stable identity, lifecycle,
settlement, complete detail, and responsive layout without a live provider.

#### Scenario: Parallel tools complete out of order

- **GIVEN** headless chat receives tool starts A, B, and C
- **WHEN** results arrive in the order B, C, and A
- **THEN** snapshots show three distinct matching results
- **AND** no result replaces an unrelated row

#### Scenario: Every output type has a disposition

- **WHEN** the output contract test enumerates the supported `SessionOutput`
  union
- **THEN** every type maps to a visible, deliberately hidden, or security-
  filtered presentation disposition
- **AND** an unclassified type fails the test

#### Scenario: Responsive snapshot matrix

- **WHEN** representative active and settled Turns render at 40, 60, 80, and
  120 columns
- **THEN** no unrelated events share one line
- **AND** required identity and lifecycle state remain visible

### Requirement: Chat input contracts have typed-key proof

Headless typed-key tests SHALL cover submit, `Shift+Enter`, prompt history,
draft restoration, double Escape, approval denial, `Ctrl+O`, detail scroll, and
multiline paste. They SHALL also cover the active-turn Queue Shelf and the
session actor's FIFO batch. Time-based key sequences SHALL use `TimeProvider`.

#### Scenario: Shift Enter does not submit

- **WHEN** the test enters text and sends `Shift+Enter`
- **THEN** the input contains one newline
- **AND** the submit observer receives no value

#### Scenario: Approval detail preserves selection

- **GIVEN** Allow once is selected
- **WHEN** the test sends `Ctrl+O`, PageDown, and `Ctrl+O`
- **THEN** the detail expands, moves, and collapses
- **AND** Allow once remains selected
- **AND** no approval response occurs before Enter

#### Scenario: Double Escape uses virtual time

- **GIVEN** a nonempty recalled prompt
- **WHEN** the test sends two Escape keys inside the configured virtual-time
  window
- **THEN** the input clears without `Task.Delay` or `Thread.Sleep`

#### Scenario: Active-turn prompts use one model call

- **GIVEN** one model call remains active
- **WHEN** the typed-key test submits three later prompts
- **THEN** the Queue Shelf shows all three prompts in FIFO order
- **AND** the session actor test observes one follow-up model call
- **AND** that model call contains all three prompts in FIFO order

#### Scenario: Missing rationale fails before tool dispatch

- **GIVEN** a new tool call omits `_rationale`
- **WHEN** the shared execution preflight validates the call
- **THEN** the test executor receives no invocation
- **AND** the model receives a correction result for that call
- **AND** no approval request occurs

### Requirement: Disposable visual checkpoints prove the chat grammar

Development review SHALL use temporary video tapes outside the repository.
These tapes SHALL not enter CI or the permanent smoke suite.

The review SHALL cover the core chat, rich activity with approval, and the
Inspector with responsive layout. Each review SHALL retain a video and selected
frame images as temporary proof.

#### Scenario: Visual checkpoint review

- **WHEN** a developer reaches one of the three visual checkpoints
- **THEN** a temporary tape runs the real published CLI against a test daemon
- **AND** the developer reviews the video and selected frame images
- **AND** the developer records material visual defects before the next checkpoint
- **AND** no checkpoint tape becomes a CI or repository asset

#### Scenario: Full-screen regression suite

- **WHEN** Termina inline support enters the Netclaw dependency graph
- **THEN** the existing init, config, model, provider, and approval TUI smoke
  flows retain their full-screen behavior
- **AND** `./scripts/smoke/run-smoke.sh light` passes
