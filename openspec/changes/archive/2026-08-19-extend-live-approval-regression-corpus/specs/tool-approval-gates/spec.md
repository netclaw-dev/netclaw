## ADDED Requirements

### Requirement: Executable post-1952 live approval regression corpus

The shell-policy evidence catalog SHALL contain one executable live regression
for each representative post-1952 evidence case T01 through T21. Each
regression SHALL identify its source evidence file and source evidence ID. It
SHALL retain the source classification and intended policy outcome.

Executable commands SHALL be identity-free and SHALL preserve the
policy-relevant shell grammar of the source shape. Display-only redactions that
would become shell operators SHALL NOT be executed as literal fixture input.

The real shell policy coordinator SHALL evaluate every regression. Each row
SHALL assert the final outcome, deny reason, approval candidates, messy status,
approval option keys, and approval-actor contact count that are applicable to
that outcome. Evidence classifications SHALL NOT grant authority.

#### Scenario: Every representative post-1952 case executes once

- **WHEN** the live regression fixture loads
- **THEN** source evidence IDs T01 through T21 each occur exactly once
- **AND** policy case IDs L12 through L32 each occur exactly once
- **AND** every case executes through the real coordinator

#### Scenario: Source evidence remains exactly linked

- **WHEN** the evidence contract validates a live regression
- **THEN** its source file and evidence ID resolve to one harvested case
- **AND** its digest includes the harvested command shape
- **AND** its classification equals the harvested classification
- **AND** its target outcome equals its executable policy expectation

#### Scenario: Display redaction does not change executable grammar

- **WHEN** a harvested command shape contains a display-only placeholder
- **THEN** the executable fixture uses an identity-free shell literal
- **AND** it preserves the original command chain, path boundary, redirect,
  or dynamic construct under test
- **AND** it does not interpret an angle-bracket placeholder as a redirect

#### Scenario: Current fact gaps remain strict

- **WHEN** the coordinator evaluates the curated default-GET `gh api` cases
  or the static Bash arithmetic echo case
- **THEN** it requires approval under the current parser facts
- **AND** Netclaw does not infer executable-private operation semantics

#### Scenario: Agent-alignment cases do not gain authority

- **WHEN** the coordinator evaluates a case classified as
  `AgentAlignmentDebt`
- **THEN** the classification does not provide candidate coverage
- **AND** the current call remains approval-gated

#### Scenario: Executable evidence drift is explicit

- **WHEN** a source shape, executable command, evidence link, classification,
  expected outcome, correction, approval shape, or actor-contact count changes
- **THEN** the locked live-regression digest changes
- **AND** the evidence contract fails until the new artifact is reviewed and
  deliberately accepted

#### Scenario: Corpus contains no source identity

- **WHEN** the PII contract scans the added executable fixtures
- **THEN** it finds no local username, private repository, channel, thread,
  host, email, token, or secret
