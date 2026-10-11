## Purpose

Give the parent small workflow guides for scoped child work, independent evidence review, and complete artifact delivery.

## ADDED Requirements

### Requirement: Parent-inline progressive workflow selection

The system SHALL expose a model-invocable `agent-coordination` system skill under existing discovery and audience policy.
The skill SHALL remain inline in the parent and SHALL omit `metadata.subagent`.
The base SHALL contain the common authority, workspace, evidence, cancellation, and delivery rules.
The base SHALL direct the parent to read only the selected workflow through logical skill APIs.
The skill SHALL contain these resources:

- `references/analyze-plan.md`
- `references/parallel-research.md`
- `references/implement-review.md`
- `references/diagnose-fix-verify.md`
- `assets/findings.md`
- `assets/plan.md`

#### Scenario: Select a code workflow
- **GIVEN** an eligible parent receives a substantial implementation task with independent review scope
- **WHEN** the parent selects the coordination skill
- **THEN** the parent loads the skill by canonical name and reads `references/implement-review.md`
- **AND** the load does not route the coordinator itself to a child

#### Scenario: Preserve access denial
- **GIVEN** the skill or child tools are unavailable for the current audience
- **WHEN** the parent considers delegation
- **THEN** the parent follows the current policy and reports the relevant limitation
- **AND** the guidance does not teach physical-root access or a policy bypass

#### Scenario: Avoid unnecessary workflow resources
- **WHEN** the parent selects one workflow
- **THEN** the guide does not require all four workflow resources before useful work
- **AND** each ordinary resource response fits the default inline limit with its runtime wrapper

#### Scenario: Retain spill behavior under smaller limits
- **GIVEN** the operator configures an inline limit below the selected resource response size
- **WHEN** the runtime returns the resource through normal spill behavior
- **THEN** the parent uses `tool_output_read` for the receipt
- **AND** the guide does not derive a physical skill path

### Requirement: Structured analysis and complete plan handoff

The analyze-plan resource SHALL assign source analysis and a complete plan to distinct scoped tasks.
The parent SHALL inspect the findings before it assigns the plan task.
The findings template SHALL include evidence IDs, revisions, sources, owners, constraints, uncertainty, and unresolved choices.
The plan template SHALL link findings to scope, actions, acceptance checks, risks, and open decisions.
The parent SHALL check the complete Markdown artifact before user delivery.

#### Scenario: Deliver a grounded plan
- **GIVEN** the analyst supplies revision-bound findings with source evidence
- **WHEN** a worker writes the requested plan
- **THEN** the parent checks the finding references and acceptance checks
- **AND** the parent delivers the reviewed Markdown file through normal attachment policy

#### Scenario: Reject an incomplete plan claim
- **WHEN** a child returns a summary or a template with unresolved placeholders as a complete plan
- **THEN** the parent identifies the missing work before it claims completion
- **AND** a success flag or an artifact path does not satisfy the acceptance checks

### Requirement: Independent research and defect evidence

The parallel-research resource SHALL assign distinct questions and result paths.
The parent SHALL reconcile evidence and report unresolved conflicts without a majority-vote rule.
The diagnose-fix-verify resource SHALL preserve expected behavior, observed behavior, a defect counterexample, and the exact candidate revision.
It SHALL separate repair from verification and SHALL distinguish an environment failure from a detected product defect.

#### Scenario: Reconcile conflicting research
- **WHEN** independent children cite conflicting sources
- **THEN** the parent inspects the source evidence and records the unresolved conflict if necessary
- **AND** agreement between model outputs does not establish truth

#### Scenario: Verify a defect repair
- **GIVEN** a baseline counterexample fails for the stated defect
- **WHEN** the worker supplies a candidate and an independent verifier repeats the defect check
- **THEN** the parent compares baseline failure and candidate success on the stated revisions

#### Scenario: Do not claim a repair from a failed environment
- **WHEN** the verifier cannot run the defect check because a dependency is unavailable
- **THEN** the parent reports the verification gap
- **AND** the parent does not classify that result as a detected defect or a successful repair

### Requirement: Workspace ownership and independent code review

Each child task SHALL state its scope, revision, permitted workspace, output path, acceptance checks, and forbidden edits.
The implement-review resource SHALL assign one writer to each workspace.
Concurrent writers SHALL use separate authorized worktrees.
Read-only children SHALL use distinct report paths when they share source inputs.
The parent SHALL preserve operator checkout changes and SHALL serialize edits when authorized isolation is unavailable.
A separate reviewer SHALL inspect the exact candidate and SHALL not edit the candidate patch.
Workflow instructions and paths SHALL create no additional file or shell authority.

#### Scenario: Use isolated writers
- **GIVEN** two child tasks need concurrent source edits
- **WHEN** the parent prepares their work
- **THEN** each child receives a separate authorized worktree and explicit scope
- **AND** the parent checks each patch before integration

#### Scenario: Reject implicit shared-checkout permission
- **GIVEN** an operator checkout contains uncommitted changes
- **WHEN** independent worktree creation is unavailable
- **THEN** the parent preserves those changes and serializes permitted writes or reports the constraint
- **AND** different target filenames do not imply concurrent-write authorization

#### Scenario: Review the current candidate
- **GIVEN** a worker finishes a patch at one revision
- **WHEN** the parent assigns independent review
- **THEN** the reviewer receives that revision, original acceptance checks, and worker evidence
- **AND** the reviewer reports stale evidence if the candidate changes

### Requirement: Background control without unavailable communication

The coordinator SHALL use the background start and result contracts from the preceding stack PRs.
The parent SHALL load the deferred status/cancel tool explicitly when necessary.
The guide SHALL distinguish accepted cancellation, dispatch closure, and terminal result.
The parent SHALL inspect confirmed and unknown effects before it creates a replacement child.
The guide SHALL preserve original requester authority and SHALL provide no private messages, child questions, peer tools, or live steering.
The guide SHALL not require a routine tight status poll loop.

#### Scenario: Continue work after acceptance
- **WHEN** the parent receives an accepted run ID
- **THEN** the parent can continue independent work and receive the later attributed result
- **AND** status/cancel remains available through explicit deferred tool load and ordinary policy

#### Scenario: Inspect partial effects after cancellation
- **WHEN** the parent cancels an owned child
- **THEN** the parent distinguishes acceptance from dispatch closure and terminal completion
- **AND** the parent reviews the bounded framework-only partial result before replacement

#### Scenario: Preserve the original requester boundary
- **GIVEN** a different speaker asks the parent to control another requester's child
- **WHEN** the runtime denies status or cancellation under the owner contract
- **THEN** the guide does not grant broader same-session authority

#### Scenario: Do not claim restart resume or private steering
- **WHEN** a child is Lost or needs revised instructions
- **THEN** the parent reports loss or cancels and assigns a revised task as authorized
- **AND** the guide does not claim automatic resume or an unavailable message tool

### Requirement: Parent evidence review and user delivery

The parent SHALL inspect child artifacts and evidence before it claims task completion.
The parent SHALL own user messages and requested file delivery through existing policy.
The guide SHALL state that a stored artifact does not prove delivery or task success.
The skill SHALL encourage useful delegation for substantial work and SHALL not require a child for every trivial task.

#### Scenario: Deliver a requested artifact
- **GIVEN** a reviewed artifact satisfies the task checks
- **WHEN** the attachment action succeeds under ordinary policy
- **THEN** the parent can report the artifact as delivered

#### Scenario: Report a blocked attachment
- **GIVEN** a child writes the requested file
- **WHEN** the parent cannot attach the file
- **THEN** the parent reports the delivery limitation and the authorized artifact location
- **AND** the parent does not claim delivery

#### Scenario: Keep a trivial task local
- **WHEN** the user requests a small task without useful independent scope
- **THEN** the guide permits the parent to complete it directly
- **AND** it does not require extra agents to satisfy the workflow
