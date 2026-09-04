# netclaw-testing Specification

## Purpose

Define test categorization and CI requirements for provider-independent
verification.

This capability uses these [engineering glossary](../../../docs/spec/GLOSSARY.md) terms:

- [Local-control proof](../../../docs/spec/GLOSSARY.md#local-control-proof)
- [Pairing code](../../../docs/spec/GLOSSARY.md#pairing-code)
- [Device token](../../../docs/spec/GLOSSARY.md#device-token)
- [Authority](../../../docs/spec/GLOSSARY.md#authority)
- [Durable and ephemeral](../../../docs/spec/GLOSSARY.md#durable-and-ephemeral)

## Required Proof Layers

```text
proof codec tests
  -> validator tests with virtual time
  -> real HTTP endpoint matrix
  -> CLI-to-daemon integration
  -> process smoke with daemon restart
```

The endpoint matrix SHALL execute real endpoint requests.
Labels that do not configure the tested exposure mode or credential are not sufficient.

| Example case | Expected result |
|---|---|
| `tailscale-funnel` + valid proof + no bearer | Code created |
| `reverse-proxy` + no proof + valid device bearer | Unauthorized; no code |
| `local` + cross-home proof + bootstrap bearer | Unauthorized; no code |
| Any mode + repeated proof | Unauthorized; original code unchanged |
| `reverse-proxy` + forwarded loopback + device bearer + no proof | Unauthorized; no code |
| Any remote mode + valid host proof + no device bearer | Code created |
| Same proof twice at the exact 30-second boundary | Second request denied |
| Code expires during the registry write | One device and one returned token |
| Non-loopback HTTP remote endpoint | CLI rejects before code input |
| Remote redirect or invalid success JSON | CLI fails without saved state |

```text
One authority rule
  +-> configured daemon route + valid proof, any mode -> success
  +-> proxy route + local address, no proof ----------> denial
  +-> any route + device token only ------------------> denial
```

The diagram is schematic.
It omits the proof format and the pairing-code exchange.

## Requirements
### Requirement: CI-required tests are provider-independent

The required CI suite SHALL not depend on live model providers.

Required CI coverage for channel adapters SHALL also not depend on live external
chat platforms (including Discord and Mattermost). Channel behavior SHALL be
verifiable using offline fakes, fixtures, or deterministic simulators. Tests
that require a live external chat platform (such as Testcontainers-based
Mattermost integration tests) SHALL be kept out of the required CI suite.

#### Scenario: CI execution without provider secrets

- **WHEN** CI executes required tests without provider credentials
- **THEN** all required tests pass using fakes/mocks/stubs

#### Scenario: CI execution without live Discord instance

- **GIVEN** CI has no Discord token and no live Discord connectivity
- **WHEN** required test suites run
- **THEN** Discord adapter and approval fallback behavior are validated offline
- **AND** required suites pass without external Discord dependencies

#### Scenario: CI execution without live Mattermost instance

- **GIVEN** CI has no Mattermost token and no live Mattermost connectivity
- **WHEN** required test suites run
- **THEN** Mattermost adapter, conformance contract suites, and approval
  fallback behavior are validated offline
- **AND** required suites pass without external Mattermost dependencies
- **AND** Testcontainers-based Mattermost integration tests are not part of the
  required suite

### Requirement: Optional live smoke tests

The system SHALL support optional smoke tests against live endpoints.

#### Scenario: Developer runs live smoke test

- **WHEN** a developer invokes smoke tests explicitly
- **THEN** live provider checks execute and report actionable diagnostics

#### Scenario: Tailscale-only Ollama server not reachable in CI

- **GIVEN** Ollama server is only reachable on Tailscale
- **WHEN** CI runs without Tailscale connectivity
- **THEN** CI-required test suites still pass because live smoke tests are not required

### Requirement: Coding-context evals use isolated deterministic fixtures
The behavioral eval suite SHALL support focused multi-turn coding-context cases where every scored run receives a fresh Git repository, linked worktree, unique named session, deterministic file state, and independent filesystem assertions.

#### Scenario: Main and child context lifecycle is evaluated across turns
- **GIVEN** a fresh linked-worktree fixture and unique resumed session
- **WHEN** one turn establishes file context, a later turn delegates coding, and a final turn reports resulting context
- **THEN** assertions inspect JSON tool behavior, structured child metadata, and direct Git/filesystem state

#### Scenario: Baseline and treatment results are comparable
- **GIVEN** baseline and treatment images use the same model settings and prompt variants
- **WHEN** the focused coding-context category is run repeatedly
- **THEN** results retain correctness, orientation-call, clarification, token, cache, and latency metrics for comparison

### Requirement: Execution-context isolation has automated proof

The test suite SHALL prove that admitted authority is required, parallel calls do not share mutable call state, unavailable requested capabilities fail without fallback, child deltas merge only after success, and asynchronous Git enrichment respects audience and turn-generation gates.

#### Scenario: Parallel execution regression test

- **GIVEN** a deterministic test pipeline with two concurrent tool calls
- **WHEN** each call records different file activity
- **THEN** each result contains only its own activity
- **AND** both retain the same immutable admitted-turn authority

#### Scenario: Public Git gate regression test

- **GIVEN** a fake Git inspector that records invocations
- **WHEN** a Public working-context snapshot is composed
- **THEN** the inspector records no invocation
- **AND** no internal path is rendered

#### Scenario: Stale continuation regression test

- **GIVEN** controllable asynchronous Git inspection results for consecutive turns
- **WHEN** the earlier result completes after the later turn becomes active
- **THEN** the earlier result is discarded without sleeps
- **AND** only the correlated result can affect the active prompt

### Requirement: Container upgrade compatibility proof

The smoke suite SHALL verify upgrade from the latest stable Netclaw container to a locally built image using only an isolated temporary configuration volume.

#### Scenario: Stable-to-local upgrade

- **GIVEN** the latest stable image has written or consumed a legacy config in a disposable volume
- **WHEN** a uniquely tagged local image starts against the same volume
- **THEN** the new image SHALL become healthy without modifying the file on startup
- **AND** an explicit migration SHALL preserve effective role and capability values
- **AND** switching away from and back to a definition SHALL preserve its overrides

#### Scenario: Production state isolation

- **WHEN** the upgrade smoke runs
- **THEN** it SHALL use a newly created absolute temporary directory or uniquely named test volume
- **AND** it SHALL NOT mount or inspect the default or operator-provided Netclaw home

### Requirement: Pairing security tests prove host success and remote denial

The required suite SHALL test each exposure mode against every supported credential and local-control proof class.
Each denied case SHALL prove that no pairing code was created.
The required suite SHALL not require a live tunnel provider.

#### Scenario: Every exposure mode permits a valid host proof

- **GIVEN** the test matrix contains every supported exposure mode
- **WHEN** a host caller submits a valid proof in each mode
- **THEN** each case creates one pairing code

#### Scenario: Route metadata does not change the authority decision

- **GIVEN** the suite sends requests through direct and proxy-shaped routes
- **WHEN** each request changes its source address, forwarded headers, and device bearer
- **THEN** only requests with a valid local-control proof create a code
- **AND** each denied request leaves the prior code state unchanged

#### Scenario: Remote credentials never replace a host proof

- **GIVEN** the caller has no valid host proof
- **WHEN** the caller uses no token, a device token, or a bootstrap token in each exposure mode
- **THEN** every case denies code generation
- **AND** every case proves that no code exists

#### Scenario: Proof and key failure matrix remains deterministic

- **GIVEN** stale, future, changed, repeated, cross-home, malformed, and unsupported proofs
- **WHEN** the required suite validates each case with virtual time
- **THEN** every result matches the reviewed security matrix
- **AND** no case needs network access or a live tunnel process

#### Scenario: Process test proves durable state across restart

- **GIVEN** the current daemon stores a paired device in an isolated Netclaw home
- **WHEN** the process smoke restarts the daemon with that home
- **THEN** the prior device token still authenticates
- **AND** the host can create another pairing code

#### Scenario: Time boundaries cannot split one transaction

- **GIVEN** virtual time moves past code expiration after exchange admission
- **WHEN** the durable device write succeeds
- **THEN** the test observes one device, one consumed code, and one returned token

#### Scenario: Remote transport failures preserve client state

- **GIVEN** a non-loopback HTTP endpoint, redirect, timeout, or invalid success response
- **WHEN** the CLI processes each case
- **THEN** every case fails without a saved token or endpoint
